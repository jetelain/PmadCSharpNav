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
        var results = new List<ApiDefinition>();

        var searchSource = criteria.Scope is "all" or "source" or "solution";
        var searchExternal = criteria.Scope is "all" or "dependencies";

        Regex? patternRegex = null;
        if (!string.IsNullOrWhiteSpace(criteria.Pattern) && (criteria.Pattern.Contains('*') || criteria.Pattern.Contains('?')))
        {
            var regexPattern = "^" + Regex.Escape(criteria.Pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            patternRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // 1. Search source code declarations
        if (searchSource)
        {
            foreach (var project in roslynSnapshot.Solution.Projects)
            {
                var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                    project,
                    name => MatchesPattern(name, criteria.Pattern, patternRegex),
                    cancellationToken: ct);

                foreach (var symbol in declarations)
                {
                    if (MatchesCriteria(symbol, criteria))
                    {
                        var fullDoc = RoslynTypeExplorer.ExtractFullDocumentation(symbol);
                        var summary = RoslynTypeExplorer.ExtractDocumentation(symbol);
                        if (MatchesDoc(fullDoc, criteria.DocText))
                        {
                            results.Add(MapSymbolToApiDefinition(symbol, project.Name, isExternal: false, summary));
                        }
                    }
                }
            }
        }

        // 2. Search external referenced libraries
        if (searchExternal)
        {
            var preferredProject = roslynSnapshot.GetPreferredProject(targetFramework: criteria.Framework);
            if (preferredProject != null)
            {
                var compilation = await roslynSnapshot.GetCompilationAsync(preferredProject.Id, ct);
                if (compilation != null)
                {
                    var externalSymbols = await GetExternalApiDefinitionsAsync(compilation, criteria.Scope, ct);

                    foreach (var api in externalSymbols)
                    {
                        if (MatchesCriteria(api, criteria, patternRegex) && MatchesDoc(api.Summary, criteria.DocText))
                        {
                            results.Add(api);
                        }
                    }
                }
            }
        }

        return results;
    }

    private async Task<List<ApiDefinition>> GetExternalApiDefinitionsAsync(
        Compilation compilation,
        string scope,
        CancellationToken ct)
    {
        var allExternal = new List<ApiDefinition>();

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference peRef) continue;

            var asmSymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
            if (asmSymbol == null) continue;

            if (scope is not "all" and not "dependencies" &&
                !string.Equals(asmSymbol.Name, scope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var asmName = asmSymbol.Name;
            var mvid = asmSymbol.Identity.ToString();
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mvid));
            var hash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
            var cacheFile = Path.Combine(_cacheDirectory, $"{SanitizeFileName(asmName)}_{hash}.json");

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
                if (type.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
                {
                    continue;
                }

                var typeSummary = RoslynTypeExplorer.ExtractXmlSummary(type.GetDocumentationCommentXml());
                list.Add(MapSymbolToApiDefinition(type, assembly.Name, isExternal: true, typeSummary));

                foreach (var member in type.GetMembers())
                {
                    if (member.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public ||
                        member.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    var memberSummary = RoslynTypeExplorer.ExtractXmlSummary(member.GetDocumentationCommentXml());
                    list.Add(MapSymbolToApiDefinition(member, assembly.Name, isExternal: true, memberSummary));
                }
            }
        }

        return list;
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

        return true;
    }

    private static bool MatchesCriteria(ApiDefinition api, ApiSearchCriteria criteria, Regex? regex)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Pattern))
        {
            var simpleName = ExtractSimpleName(api.Name);
            bool matches;

            if (regex != null)
            {
                matches = regex.IsMatch(simpleName) || regex.IsMatch(api.Name);
            }
            else
            {
                matches = string.Equals(simpleName, criteria.Pattern, StringComparison.OrdinalIgnoreCase) ||
                          simpleName.Contains(criteria.Pattern, StringComparison.OrdinalIgnoreCase) ||
                          api.Name.Contains(criteria.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            if (!matches) return false;
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

        return true;
    }

    private static ApiDefinition MapSymbolToApiDefinition(ISymbol symbol, string assembly, bool isExternal, string? summary)
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

        return new ApiDefinition(
            Name: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Kind: kind,
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            Assembly: assembly,
            IsExternal: isExternal,
            Summary: summary);
    }

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
