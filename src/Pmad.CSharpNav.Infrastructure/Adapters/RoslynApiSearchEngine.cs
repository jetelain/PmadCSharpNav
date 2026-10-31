using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;
using Pmad.CSharpNav.Infrastructure.Workspace;
using DomainSymbolKind = Pmad.CSharpNav.Domain.SymbolKind;

namespace Pmad.CSharpNav.Infrastructure.Adapters;

/// <summary>
/// Searches APIs across local source projects and referenced external assemblies (NuGet/BCL).
/// Features MVID-based disk caching for external symbols and background indexing.
/// </summary>
public sealed class RoslynApiSearchEngine : IApiSearchEngine
{
    private readonly ConcurrentDictionary<string, List<ApiDefinition>> _assemblyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheDirectory;
    private volatile string _indexState = "ready";

    public RoslynApiSearchEngine(string? customCacheDir = null)
    {
        _cacheDirectory = customCacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PmadCSharpNav",
            "cache");

        Directory.CreateDirectory(_cacheDirectory);
    }

    public string GetIndexState() => _indexState;

    public async Task<IReadOnlyList<ApiDefinition>> SearchAsync(
        IWorkspaceSnapshot snapshot,
        ApiSearchCriteria criteria,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var solutionDir = !string.IsNullOrEmpty(roslynSnapshot.SolutionPath)
            ? (Path.GetDirectoryName(roslynSnapshot.SolutionPath) ?? "")
            : "";
        var results = new List<ApiDefinition>();

        var hasProjectFilter = !string.IsNullOrWhiteSpace(criteria.Project);
        var isAll = string.Equals(criteria.Scope, "all", StringComparison.OrdinalIgnoreCase);
        var isSourceOnly = string.Equals(criteria.Scope, "source", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(criteria.Scope, "solution", StringComparison.OrdinalIgnoreCase);
        var isDependenciesOnly = string.Equals(criteria.Scope, "dependencies", StringComparison.OrdinalIgnoreCase);

        var scopeMatchesSourceProject = !isAll && !isSourceOnly && !isDependenciesOnly &&
                                        roslynSnapshot.Solution.Projects.Any(p => RoslynReferenceFinder.MatchesProject(p, criteria.Scope));

        var searchSource = !isDependenciesOnly && (isAll || isSourceOnly || scopeMatchesSourceProject);
        var searchExternal = !hasProjectFilter
            ? (isAll || isDependenciesOnly || (!isSourceOnly && !scopeMatchesSourceProject))
            : (isDependenciesOnly || (!isAll && !isSourceOnly && !scopeMatchesSourceProject));

        var activePatterns = new List<string>();
        if (criteria.Patterns != null)
        {
            foreach (var p in criteria.Patterns)
            {
                if (!string.IsNullOrWhiteSpace(p) && !activePatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    activePatterns.Add(p);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(criteria.Pattern) && !activePatterns.Contains(criteria.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            activePatterns.Add(criteria.Pattern);
        }

        var patternRegexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in activePatterns)
        {
            if (p.Contains('*') || p.Contains('?'))
            {
                var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                patternRegexes[p] = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        // 1. Search source code declarations
        if (searchSource)
        {
            var sourceProjects = roslynSnapshot.Solution.Projects.ToList();
            if (hasProjectFilter)
            {
                sourceProjects = sourceProjects.Where(p => RoslynReferenceFinder.MatchesProject(p, criteria.Project!)).ToList();
            }

            if (!isAll && !isSourceOnly)
            {
                sourceProjects = sourceProjects.Where(p => RoslynReferenceFinder.MatchesProject(p, criteria.Scope)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(criteria.Framework))
            {
                var filtered = sourceProjects.Where(p =>
                    p.Name.Contains(criteria.Framework, StringComparison.OrdinalIgnoreCase) ||
                    (p.OutputFilePath != null && p.OutputFilePath.Contains(criteria.Framework, StringComparison.OrdinalIgnoreCase))).ToList();
                if (filtered.Count > 0)
                {
                    sourceProjects = filtered;
                }
            }

            foreach (var project in sourceProjects)
            {
                var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                    project,
                    name => MatchesAnyPatternName(name, activePatterns, patternRegexes),
                    cancellationToken: ct);

                foreach (var symbol in declarations)
                {
                    if (MatchesCriteria(symbol, criteria) && MatchesAnyPatternForSymbol(symbol, activePatterns, patternRegexes))
                    {
                        var fullDoc = RoslynTypeExplorer.ExtractFullDocumentation(symbol);
                        var summary = RoslynTypeExplorer.ExtractDocumentation(symbol);
                        if (MatchesDoc(fullDoc, criteria.DocText))
                        {
                            results.Add(CreateDefinition(symbol, project.Name, isExternal: false, summary, solutionDir));
                        }
                    }
                }
            }
        }

        // 2. Search external referenced libraries across all solution projects
        if (searchExternal)
        {
            var projects = roslynSnapshot.Solution.Projects.ToList();
            if (hasProjectFilter)
            {
                projects = projects.Where(p => RoslynReferenceFinder.MatchesProject(p, criteria.Project!)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(criteria.Framework))
            {
                var filtered = projects.Where(p =>
                    p.Name.Contains(criteria.Framework, StringComparison.OrdinalIgnoreCase) ||
                    (p.OutputFilePath != null && p.OutputFilePath.Contains(criteria.Framework, StringComparison.OrdinalIgnoreCase))).ToList();
                if (filtered.Count > 0)
                {
                    projects = filtered;
                }
            }

            var projectAssemblyNames = new HashSet<string>(
                roslynSnapshot.Solution.Projects.Select(p => p.AssemblyName),
                StringComparer.OrdinalIgnoreCase);
            var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects)
            {
                var compilation = await roslynSnapshot.GetCompilationAsync(project.Id, ct);
                if (compilation == null) continue;

                var externalSymbols = await GetExternalApiDefinitionsAsync(compilation, criteria.Scope, projectAssemblyNames, seenAssemblies, ct);

                foreach (var api in externalSymbols)
                {
                    if (MatchesCriteria(api, criteria, activePatterns, patternRegexes) && MatchesDoc(api.Summary, criteria.DocText))
                    {
                        results.Add(api);
                    }
                }
            }
        }

        // Deduplicate by symbol identity
        var deduplicated = results
            .GroupBy(r => !string.IsNullOrEmpty(r.SymbolId) ? r.SymbolId : $"{r.Assembly}#{r.Name}#{r.Signature}")
            .Select(g => g.First())
            .ToList();

        // Rank candidate matches before applying limit
        var ranked = deduplicated
            .OrderBy(r => GetTier(r, activePatterns))
            .ThenBy(r => IsType(r.Kind) ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return ranked;
    }

    private async Task<List<ApiDefinition>> GetExternalApiDefinitionsAsync(
        Compilation compilation,
        string scope,
        HashSet<string> projectAssemblyNames,
        HashSet<string> seenAssemblies,
        CancellationToken ct)
    {
        var allExternal = new List<ApiDefinition>();

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference peRef) continue;

            var asmSymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
            if (asmSymbol == null) continue;

            if (projectAssemblyNames.Contains(asmSymbol.Name))
            {
                continue;
            }

            if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "dependencies", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(asmSymbol.Name, scope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var asmName = asmSymbol.Name;
            if (!seenAssemblies.Add(asmName))
            {
                continue;
            }

            var mvid = asmSymbol.Identity.ToString();
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mvid));
            var hash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
            var cacheFile = Path.Combine(_cacheDirectory, $"{SanitizeFileName(asmName)}_v2_{hash}.json");

            if (_assemblyCache.TryGetValue(asmName, out var cached))
            {
                allExternal.AddRange(cached);
                continue;
            }

            // Try reading disk cache
            if (File.Exists(cacheFile))
            {
                try
                {
                    var diskJson = await File.ReadAllTextAsync(cacheFile, ct);
                    var diskDefinitions = JsonSerializer.Deserialize<List<ApiDefinition>>(diskJson);
                    if (diskDefinitions != null)
                    {
                        _assemblyCache[asmName] = diskDefinitions;
                        allExternal.AddRange(diskDefinitions);
                        continue;
                    }
                }
                catch
                {
                    // Fallback to scanning if disk cache is corrupted
                }
            }

            // Scan public types in assembly namespace
            var scanned = ScanAssemblyPublicApis(asmSymbol);
            _assemblyCache[asmName] = scanned;
            allExternal.AddRange(scanned);

            // Persist to disk in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(scanned);
                    await File.WriteAllTextAsync(cacheFile, json);
                }
                catch { }
            }, CancellationToken.None);
        }

        return allExternal;
    }

    private static List<ApiDefinition> ScanAssemblyPublicApis(IAssemblySymbol assembly)
    {
        var list = new List<ApiDefinition>();
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var childNs in ns.GetNamespaceMembers())
            {
                stack.Push(childNs);
            }

            foreach (var type in ns.GetTypeMembers())
            {
                ScanType(type, assembly.Name, list);
            }
        }

        return list;
    }

    private static void ScanType(INamedTypeSymbol type, string assemblyName, List<ApiDefinition> list)
    {
        if (type.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
        {
            return;
        }

        var typeSummary = RoslynTypeExplorer.ExtractXmlSummary(type.GetDocumentationCommentXml());
        list.Add(MapSymbolToApiDefinition(type, assemblyName, isExternal: true, typeSummary));

        foreach (var member in type.GetMembers())
        {
            if (member.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public ||
                member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (member is INamedTypeSymbol nestedType)
            {
                ScanType(nestedType, assemblyName, list);
            }
            else
            {
                var memberSummary = RoslynTypeExplorer.ExtractXmlSummary(member.GetDocumentationCommentXml());
                list.Add(MapSymbolToApiDefinition(member, assemblyName, isExternal: true, memberSummary));
            }
        }
    }

    private static bool MatchesAnyPatternName(string name, List<string> activePatterns, Dictionary<string, Regex> regexes)
    {
        if (activePatterns.Count == 0) return true;
        for (int i = 0; i < activePatterns.Count; i++)
        {
            var p = activePatterns[i];
            regexes.TryGetValue(p, out var reg);
            if (MatchesPattern(name, p, reg))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesAnyPatternForSymbol(ISymbol symbol, List<string> activePatterns, Dictionary<string, Regex> regexes)
    {
        if (activePatterns.Count == 0) return true;

        var simpleName = symbol.Name;
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var cleanFullName = CleanSignatureOrName(fullName);

        for (int i = 0; i < activePatterns.Count; i++)
        {
            var p = activePatterns[i];
            regexes.TryGetValue(p, out var reg);
            if (reg != null)
            {
                if (reg.IsMatch(simpleName) || reg.IsMatch(fullName))
                    return true;
            }
            else
            {
                if (string.Equals(simpleName, p, StringComparison.OrdinalIgnoreCase) ||
                    simpleName.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                    IsQualifiedSuffixMatch(cleanFullName, p) ||
                    MatchesPascalCaseWord(simpleName, p) ||
                    cleanFullName.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool MatchesPattern(string name, string? pattern, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;

        if (regex != null)
        {
            if (regex.IsMatch(name)) return true;
        }

        if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;

        if (pattern.Contains('.'))
        {
            var simple = pattern[(pattern.LastIndexOf('.') + 1)..];
            if (string.Equals(name, simple, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractSimpleName(string name)
    {
        var clean = name;
        var parenIndex = clean.IndexOf('(');
        if (parenIndex >= 0)
        {
            clean = clean[..parenIndex];
        }
        var dotIndex = clean.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            clean = clean[(dotIndex + 1)..];
        }
        var angleIndex = clean.IndexOf('<');
        if (angleIndex >= 0)
        {
            clean = clean[..angleIndex];
        }
        return clean.Trim();
    }

    private static string CleanSignatureOrName(string name)
    {
        var clean = name;
        var parenIndex = clean.IndexOf('(');
        if (parenIndex >= 0)
        {
            clean = clean[..parenIndex];
        }
        var angleIndex = clean.IndexOf('<');
        if (angleIndex >= 0)
        {
            clean = clean[..angleIndex];
        }
        return clean.Trim();
    }

    private static string? ExtractNamespace(string fullName)
    {
        var clean = CleanSignatureOrName(fullName);
        var lastDot = clean.LastIndexOf('.');
        if (lastDot <= 0) return null;
        return clean[..lastDot];
    }

    private static bool IsQualifiedSuffixMatch(string cleanFullName, string pattern)
    {
        if (string.Equals(cleanFullName, pattern, StringComparison.OrdinalIgnoreCase))
            return true;
        if (cleanFullName.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool MatchesPascalCaseWord(string name, string pattern)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pattern)) return false;

        for (int i = 0; i < name.Length; i++)
        {
            bool isBoundary = i == 0 ||
                              name[i - 1] == '_' ||
                              (char.IsUpper(name[i]) && char.IsLower(name[i - 1])) ||
                              (!char.IsLetterOrDigit(name[i - 1]) && char.IsLetterOrDigit(name[i]));

            if (isBoundary)
            {
                if (name.AsSpan(i).StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsType(DomainSymbolKind kind) =>
        kind is DomainSymbolKind.Class
            or DomainSymbolKind.Interface
            or DomainSymbolKind.Record
            or DomainSymbolKind.Struct
            or DomainSymbolKind.Enum;

    private static bool IsMember(DomainSymbolKind kind) =>
        kind is DomainSymbolKind.Method
            or DomainSymbolKind.Property
            or DomainSymbolKind.Field
            or DomainSymbolKind.Event
            or DomainSymbolKind.Constructor;

    private static int GetTier(ApiDefinition api, List<string> patterns)
    {
        if (patterns.Count == 0) return 5;

        var shortName = ExtractSimpleName(api.Name);
        var cleanFullName = CleanSignatureOrName(api.Name);
        bool isType = IsType(api.Kind);
        bool isMember = !isType;

        // Tier 1 (Exact Type Name): Type symbol (class, interface, record, struct, enum) whose short name equals a pattern (case-insensitive).
        if (isType && patterns.Any(p => string.Equals(shortName, p, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        // Tier 2 (Exact Member Name): Member symbol (method, property, field, event, constructor) whose short name equals a pattern (case-insensitive).
        if (isMember && patterns.Any(p => string.Equals(shortName, p, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        // Tier 3 (Qualified Suffix Match): Type.Member or Namespace.Type suffix match.
        if (patterns.Any(p => IsQualifiedSuffixMatch(cleanFullName, p)))
        {
            return 3;
        }

        // Tier 4 (Prefix / PascalCase Word Match): Symbol name starts with the pattern or matches a PascalCase word boundary.
        if (patterns.Any(p => shortName.StartsWith(p, StringComparison.OrdinalIgnoreCase) || MatchesPascalCaseWord(shortName, p)))
        {
            return 4;
        }

        // Tier 5 (Substring / Glob / Regex Match): General substring or wildcard match.
        return 5;
    }

    private static bool MatchesDoc(string? summary, string? docQuery)
    {
        if (string.IsNullOrWhiteSpace(docQuery)) return true;
        if (string.IsNullOrWhiteSpace(summary)) return false;

        var tokens = docQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(t => summary.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesCriteria(ISymbol symbol, ApiSearchCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Kind))
        {
            var kindStr = criteria.Kind.ToLowerInvariant();
            var symKind = symbol switch
            {
                INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol => "field",
                _ => "other"
            };

            if (!symKind.Contains(kindStr, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (string.Equals(criteria.Access, "public", StringComparison.OrdinalIgnoreCase) &&
            symbol.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
        {
            return false;
        }

        if (string.Equals(criteria.Access, "internal", StringComparison.OrdinalIgnoreCase) &&
            symbol.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Internal &&
            symbol.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal)
        {
            return false;
        }

        var isObsolete = symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "ObsoleteAttribute" or "Obsolete");
        if (!criteria.IncludeObsolete && isObsolete)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Namespace))
        {
            var ns = symbol.ContainingNamespace?.ToDisplayString();
            if (ns == null || (!ns.Equals(criteria.Namespace, StringComparison.OrdinalIgnoreCase) &&
                               !ns.StartsWith(criteria.Namespace + ".", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.Assembly))
        {
            var asm = symbol.ContainingAssembly?.Name;
            if (!string.Equals(asm, criteria.Assembly, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.InheritsFrom))
        {
            if (symbol is not INamedTypeSymbol typeSymbol)
            {
                return false;
            }
            bool inherits = false;
            var baseType = typeSymbol.BaseType;
            while (baseType != null)
            {
                if (baseType.Name.Equals(criteria.InheritsFrom, StringComparison.OrdinalIgnoreCase) ||
                    baseType.ToDisplayString().Equals(criteria.InheritsFrom, StringComparison.OrdinalIgnoreCase))
                {
                    inherits = true;
                    break;
                }
                baseType = baseType.BaseType;
            }
            if (!inherits)
            {
                foreach (var iface in typeSymbol.AllInterfaces)
                {
                    if (iface.Name.Equals(criteria.InheritsFrom, StringComparison.OrdinalIgnoreCase) ||
                        iface.ToDisplayString().Equals(criteria.InheritsFrom, StringComparison.OrdinalIgnoreCase))
                    {
                        inherits = true;
                        break;
                    }
                }
            }
            if (!inherits) return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.ReturnsType))
        {
            ITypeSymbol? returnType = symbol switch
            {
                IMethodSymbol m => m.ReturnType,
                IPropertySymbol p => p.Type,
                IFieldSymbol f => f.Type,
                _ => null
            };
            if (returnType == null) return false;
            if (!returnType.Name.Equals(criteria.ReturnsType, StringComparison.OrdinalIgnoreCase) &&
                !returnType.ToDisplayString().Equals(criteria.ReturnsType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCriteria(
        ApiDefinition api,
        ApiSearchCriteria criteria,
        List<string> activePatterns,
        Dictionary<string, Regex> regexes)
    {
        if (activePatterns.Count > 0)
        {
            var simpleName = ExtractSimpleName(api.Name);
            var cleanFullName = CleanSignatureOrName(api.Name);
            bool anyMatched = false;

            for (int i = 0; i < activePatterns.Count; i++)
            {
                var p = activePatterns[i];
                regexes.TryGetValue(p, out var reg);
                if (reg != null)
                {
                    if (reg.IsMatch(simpleName) || reg.IsMatch(api.Name))
                    {
                        anyMatched = true;
                        break;
                    }
                }
                else
                {
                    if (string.Equals(simpleName, p, StringComparison.OrdinalIgnoreCase) ||
                        simpleName.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                        IsQualifiedSuffixMatch(cleanFullName, p) ||
                        MatchesPascalCaseWord(simpleName, p) ||
                        api.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    {
                        anyMatched = true;
                        break;
                    }
                }
            }

            if (!anyMatched) return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Kind))
        {
            if (!api.Kind.ToString().Equals(criteria.Kind, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (string.Equals(criteria.Access, "internal", StringComparison.OrdinalIgnoreCase))
        {
            // External APIs are public metadata; internal filter excludes external dependencies
            return false;
        }

        if (!criteria.IncludeObsolete && api.IsObsolete)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Namespace))
        {
            var ns = ExtractNamespace(api.Name);
            if (ns == null || (!ns.Equals(criteria.Namespace, StringComparison.OrdinalIgnoreCase) &&
                               !ns.StartsWith(criteria.Namespace + ".", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.Assembly))
        {
            if (!string.Equals(api.Assembly, criteria.Assembly, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.ReturnsType))
        {
            if (api.Returns == null && api.Signature == null) return false;
            bool matchesReturn = (api.Returns != null && (api.Returns.Equals(criteria.ReturnsType, StringComparison.OrdinalIgnoreCase) ||
                                                          api.Returns.Contains(criteria.ReturnsType, StringComparison.OrdinalIgnoreCase))) ||
                                 (api.Signature != null && (api.Signature.StartsWith(criteria.ReturnsType + " ", StringComparison.OrdinalIgnoreCase) ||
                                                            api.Signature.Contains(" " + criteria.ReturnsType + " ", StringComparison.OrdinalIgnoreCase)));
            if (!matchesReturn) return false;
        }

        return true;
    }

    private static ApiDefinition CreateDefinition(ISymbol symbol, string assembly, bool isExternal, string? summary, string? solutionDir = null)
    {
        var kind = symbol switch
        {
            INamedTypeSymbol t when t.TypeKind == TypeKind.Interface => DomainSymbolKind.Interface,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Enum => DomainSymbolKind.Enum,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Struct => DomainSymbolKind.Struct,
            INamedTypeSymbol t when t.IsRecord => DomainSymbolKind.Record,
            INamedTypeSymbol => DomainSymbolKind.Class,
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => DomainSymbolKind.Constructor,
            IMethodSymbol => DomainSymbolKind.Method,
            IPropertySymbol => DomainSymbolKind.Property,
            IFieldSymbol => DomainSymbolKind.Field,
            IEventSymbol => DomainSymbolKind.Event,
            _ => DomainSymbolKind.Class
        };

        var obsoleteAttr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "ObsoleteAttribute" or "Obsolete");
        bool isObsolete = obsoleteAttr != null;
        string? obsoleteMessage = obsoleteAttr?.ConstructorArguments.FirstOrDefault().Value as string;

        var symbolId = SymbolKeyGenerator.GetId(symbol);
        string? file = null;
        int? line = null;
        int? endLine = null;

        var srcLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (srcLoc != null)
        {
            var mapped = srcLoc.GetMappedLineSpan();
            var lineSpan = mapped.IsValid ? mapped : srcLoc.GetLineSpan();
            var rawPath = !string.IsNullOrEmpty(lineSpan.Path) ? lineSpan.Path : srcLoc.SourceTree?.FilePath;
            if (!string.IsNullOrEmpty(rawPath))
            {
                file = !string.IsNullOrEmpty(solutionDir) ? PathNormalizer.GetRelativePath(solutionDir, rawPath) : rawPath;
            }
            line = lineSpan.StartLinePosition.Line + 1;
            endLine = lineSpan.EndLinePosition.Line + 1;
        }

        return new ApiDefinition(
            Name: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Kind: kind,
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            Assembly: assembly,
            IsExternal: isExternal,
            Summary: summary,
            IsObsolete: isObsolete,
            ObsoleteMessage: obsoleteMessage,
            File: file,
            Line: line,
            EndLine: endLine,
            SymbolId: symbolId);
    }

    private static ApiDefinition MapSymbolToApiDefinition(ISymbol symbol, string assembly, bool isExternal, string? summary, string? solutionDir = null)
        => CreateDefinition(symbol, assembly, isExternal, summary, solutionDir);

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
