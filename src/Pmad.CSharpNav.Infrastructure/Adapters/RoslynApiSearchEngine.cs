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
                var body = Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".");
                var regexPattern = p.Contains('.')
                    ? "(?:^|\\.)" + body + "$"
                    : "^" + body + "$";
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

                    if (symbol is INamedTypeSymbol namedType)
                    {
                        foreach (var ctor in namedType.InstanceConstructors)
                        {
                            if (ctor.IsImplicitlyDeclared) continue;
                            if (MatchesCriteria(ctor, criteria) && MatchesAnyPatternForSymbol(ctor, activePatterns, patternRegexes))
                            {
                                var ctorFullDoc = RoslynTypeExplorer.ExtractFullDocumentation(ctor);
                                var ctorSummary = RoslynTypeExplorer.ExtractDocumentation(ctor);
                                if (MatchesDoc(ctorFullDoc, criteria.DocText))
                                {
                                    results.Add(CreateDefinition(ctor, project.Name, isExternal: false, ctorSummary, solutionDir));
                                }
                            }
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
            var cacheFile = Path.Combine(_cacheDirectory, $"{SanitizeFileName(asmName)}_v3_{hash}.json");

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

        var simpleName = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && symbol.ContainingType != null
            ? symbol.ContainingType.Name
            : symbol.Name;
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var cleanFullName = CleanSignatureOrName(fullName);

        for (int i = 0; i < activePatterns.Count; i++)
        {
            var p = activePatterns[i];
            regexes.TryGetValue(p, out var reg);
            if (reg != null)
            {
                if (reg.IsMatch(simpleName) || reg.IsMatch(cleanFullName) || reg.IsMatch(fullName))
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
            if (string.IsNullOrWhiteSpace(simple) || simple == "*") return true;
            if (simple.Contains('*') || simple.Contains('?'))
            {
                var simpleRegex = "^" + Regex.Escape(simple).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (Regex.IsMatch(name, simpleRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
            else if (name.Contains(simple, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractSimpleName(string name)
    {
        var clean = CleanSignatureOrName(name);
        var dotIndex = clean.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            clean = clean[(dotIndex + 1)..];
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
        if (clean.Contains('<'))
        {
            clean = StripGenericArguments(clean);
        }
        return clean.Trim();
    }

    private static string StripGenericArguments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                if (depth > 0) depth--;
            }
            else if (depth == 0)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string? ExtractNamespace(ApiDefinition api)
    {
        var target = !string.IsNullOrWhiteSpace(api.ContainingType)
            ? api.ContainingType
            : api.Name;
        var clean = CleanSignatureOrName(target);
        var lastDot = clean.LastIndexOf('.');
        if (lastDot <= 0) return null;
        return clean[..lastDot];
    }

    private static bool IsQualifiedSuffixMatch(string cleanFullName, string pattern)
    {
        var cleanPattern = CleanSignatureOrName(pattern);
        if (string.Equals(cleanFullName, cleanPattern, StringComparison.OrdinalIgnoreCase))
            return true;
        if (cleanFullName.EndsWith("." + cleanPattern, StringComparison.OrdinalIgnoreCase))
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
                              (char.IsUpper(name[i]) && i + 1 < name.Length && char.IsLower(name[i + 1])) ||
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

    private static DomainSymbolKind MapSymbolKind(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol t when t.TypeKind == TypeKind.Interface => DomainSymbolKind.Interface,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Enum => DomainSymbolKind.Enum,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Struct => DomainSymbolKind.Struct,
            INamedTypeSymbol t when t.IsRecord => DomainSymbolKind.Record,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Delegate => DomainSymbolKind.Delegate,
            INamedTypeSymbol => DomainSymbolKind.Class,
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => DomainSymbolKind.Constructor,
            IMethodSymbol => DomainSymbolKind.Method,
            IPropertySymbol => DomainSymbolKind.Property,
            IFieldSymbol => DomainSymbolKind.Field,
            IEventSymbol => DomainSymbolKind.Event,
            _ => DomainSymbolKind.Class
        };

    private static bool MatchesKind(DomainSymbolKind kind, string? kindCriteria)
    {
        if (string.IsNullOrWhiteSpace(kindCriteria) || kindCriteria.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (kindCriteria.Equals("type", StringComparison.OrdinalIgnoreCase))
        {
            return IsType(kind) || kind == DomainSymbolKind.Delegate;
        }

        var kindStr = kind.ToString();
        if (kindCriteria.Contains(','))
        {
            return kindCriteria
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(k => k.Equals("type", StringComparison.OrdinalIgnoreCase)
                    ? (IsType(kind) || kind == DomainSymbolKind.Delegate)
                    : kindStr.Equals(k, StringComparison.OrdinalIgnoreCase));
        }

        return kindStr.Equals(kindCriteria, StringComparison.OrdinalIgnoreCase) ||
               (kind == DomainSymbolKind.Record && kindCriteria.Equals("class", StringComparison.OrdinalIgnoreCase));
    }

    private static ITypeSymbol? GetSymbolReturnType(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol m when m.MethodKind != MethodKind.Constructor => m.ReturnType,
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            IEventSymbol e => e.Type,
            _ => null
        };

    private static bool MatchesTypeName(ITypeSymbol typeSymbol, string targetType)
    {
        var cleanTarget = targetType.Trim().TrimEnd('?');
        if (string.IsNullOrEmpty(cleanTarget)) return false;

        var strippedTarget = StripGenericArguments(cleanTarget);
        var minDisplay = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).TrimEnd('?');
        var fullDisplay = typeSymbol.ToDisplayString().TrimEnd('?');

        if (typeSymbol.Name.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase) ||
            typeSymbol.Name.Equals(strippedTarget, StringComparison.OrdinalIgnoreCase) ||
            minDisplay.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase) ||
            StripGenericArguments(minDisplay).Equals(strippedTarget, StringComparison.OrdinalIgnoreCase) ||
            fullDisplay.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase) ||
            StripGenericArguments(fullDisplay).Equals(strippedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesReturnType(ITypeSymbol returnType, string targetReturnType)
    {
        if (MatchesTypeName(returnType, targetReturnType))
        {
            return true;
        }

        if (returnType is INamedTypeSymbol { IsGenericType: true } namedType)
        {
            foreach (var typeArg in namedType.TypeArguments)
            {
                if (MatchesTypeName(typeArg, targetReturnType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesTypeNameString(string candidateType, string targetType)
    {
        var cleanCandidate = candidateType.Trim().TrimEnd('?');
        var cleanTarget = targetType.Trim().TrimEnd('?');
        if (string.IsNullOrEmpty(cleanCandidate) || string.IsNullOrEmpty(cleanTarget)) return false;

        if (cleanCandidate.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase)) return true;

        var strippedCandidate = StripGenericArguments(cleanCandidate);
        var strippedTarget = StripGenericArguments(cleanTarget);
        if (strippedCandidate.Equals(strippedTarget, StringComparison.OrdinalIgnoreCase)) return true;

        var shortCandidate = ExtractSimpleName(strippedCandidate);
        if (shortCandidate.Equals(strippedTarget, StringComparison.OrdinalIgnoreCase)) return true;

        // Also check generic type argument e.g. Task<Order> matching Order
        var openAngle = cleanCandidate.IndexOf('<');
        var closeAngle = cleanCandidate.LastIndexOf('>');
        if (openAngle >= 0 && closeAngle > openAngle && !cleanTarget.Contains('<'))
        {
            var inner = cleanCandidate[(openAngle + 1)..closeAngle];
            foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (MatchesTypeNameString(part, cleanTarget))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesCriteria(ISymbol symbol, ApiSearchCriteria criteria)
    {
        if (!MatchesKind(MapSymbolKind(symbol), criteria.Kind))
        {
            return false;
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
                if (MatchesTypeName(baseType, criteria.InheritsFrom))
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
                    if (MatchesTypeName(iface, criteria.InheritsFrom))
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
            var returnType = GetSymbolReturnType(symbol);
            if (returnType == null || !MatchesReturnType(returnType, criteria.ReturnsType))
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
                    if (reg.IsMatch(simpleName) || reg.IsMatch(cleanFullName) || reg.IsMatch(api.Name))
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
                        cleanFullName.Contains(p, StringComparison.OrdinalIgnoreCase))
                    {
                        anyMatched = true;
                        break;
                    }
                }
            }

            if (!anyMatched) return false;
        }

        if (!MatchesKind(api.Kind, criteria.Kind))
        {
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
            var ns = ExtractNamespace(api);
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

        if (!string.IsNullOrWhiteSpace(criteria.InheritsFrom))
        {
            if (!IsType(api.Kind) || api.BaseTypes == null || api.BaseTypes.Count == 0)
            {
                return false;
            }
            if (!api.BaseTypes.Any(b => MatchesTypeNameString(b, criteria.InheritsFrom)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.ReturnsType))
        {
            if (string.IsNullOrWhiteSpace(api.Returns) || !MatchesTypeNameString(api.Returns, criteria.ReturnsType))
            {
                return false;
            }
        }

        return true;
    }

    private static ApiDefinition CreateDefinition(ISymbol symbol, string assembly, bool isExternal, string? summary, string? solutionDir = null)
    {
        var kind = MapSymbolKind(symbol);

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
            var isMapped = mapped.HasMappedPath && !string.IsNullOrWhiteSpace(mapped.Path);
            var lineSpan = isMapped ? mapped : srcLoc.GetLineSpan();
            var rawPath = !string.IsNullOrEmpty(lineSpan.Path) ? lineSpan.Path : srcLoc.SourceTree?.FilePath;
            if (!string.IsNullOrEmpty(rawPath))
            {
                file = !string.IsNullOrEmpty(solutionDir) ? PathNormalizer.GetRelativePath(solutionDir, rawPath) : rawPath;
            }
            line = lineSpan.StartLinePosition.Line + 1;
            endLine = RoslynReferenceFinder.GetDeclarationEndLine(symbol, srcLoc, lineSpan, isMapped);
        }

        var returnType = GetSymbolReturnType(symbol);
        var returns = returnType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        IReadOnlyList<string>? baseTypes = null;
        if (symbol is INamedTypeSymbol typeSymbol)
        {
            var bases = new List<string>();
            var bt = typeSymbol.BaseType;
            while (bt != null)
            {
                var display = bt.ToDisplayString();
                if (!bases.Contains(display, StringComparer.OrdinalIgnoreCase))
                {
                    bases.Add(display);
                }
                bt = bt.BaseType;
            }
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                var display = iface.ToDisplayString();
                if (!bases.Contains(display, StringComparer.OrdinalIgnoreCase))
                {
                    bases.Add(display);
                }
            }
            if (bases.Count > 0)
            {
                baseTypes = bases;
            }
        }

        return new ApiDefinition(
            Name: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Kind: kind,
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            Assembly: assembly,
            IsExternal: isExternal,
            Summary: summary,
            Returns: returns,
            IsObsolete: isObsolete,
            ObsoleteMessage: obsoleteMessage,
            File: file,
            Line: line,
            EndLine: endLine,
            SymbolId: symbolId,
            BaseTypes: baseTypes);
    }

    private static ApiDefinition MapSymbolToApiDefinition(ISymbol symbol, string assembly, bool isExternal, string? summary, string? solutionDir = null)
        => CreateDefinition(symbol, assembly, isExternal, summary, solutionDir);

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
