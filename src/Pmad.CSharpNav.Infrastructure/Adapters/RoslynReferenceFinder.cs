using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;
using Pmad.CSharpNav.Infrastructure.Workspace;
using DomainReferenceLocation = Pmad.CSharpNav.Domain.ReferenceLocation;
using DomainSymbolKind = Pmad.CSharpNav.Domain.SymbolKind;

namespace Pmad.CSharpNav.Infrastructure.Adapters;

/// <summary>
/// Finds symbol references, call sites, implementations, and overrides across a Roslyn solution.
/// </summary>
public sealed class RoslynReferenceFinder : IReferenceFinder, IDefinitionFinder, IImplementationFinder
{
    public async Task<SymbolResolutionResult?> ResolveSymbolAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var docId = roslynSnapshot.FindDocumentId(file);
        if (docId == null)
        {
            return await ResolveSymbolInMappedFileAsync(roslynSnapshot, file, line, column, ct);
        }

        var document = roslynSnapshot.Solution.GetDocument(docId);
        if (document == null) return null;

        var sourceText = await document.GetTextAsync(ct);
        if (line < 1 || line > sourceText.Lines.Count) return null;

        var textLine = sourceText.Lines[line - 1];
        var position = textLine.Start + Math.Clamp(column - 1, 0, textLine.Span.Length);

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return null;

        var token = root.FindToken(position);
        var node = token.Parent;
        if (node == null) return null;

        var symbol = semanticModel.GetSymbolInfo(node, ct).Symbol
                     ?? semanticModel.GetDeclaredSymbol(node, ct);

        if (symbol == null)
        {
            // Try parent expressions if token was part of a larger expression
            var parent = node.Parent;
            while (parent != null && symbol == null)
            {
                symbol = semanticModel.GetSymbolInfo(parent, ct).Symbol
                         ?? semanticModel.GetDeclaredSymbol(parent, ct);
                parent = parent.Parent;
            }
        }

        if (symbol == null) return null;

        var target = MapSymbolToIdentifier(symbol);
        var targetKind = MapSymbolKind(symbol);
        var definedLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var (definedFile, definedLine, _) = GetMappedLocation(definedLoc);

        return new SymbolResolutionResult(target, definedFile, definedLine, targetKind);
    }

    private static async Task<SymbolResolutionResult?> ResolveSymbolInMappedFileAsync(
        RoslynWorkspaceSnapshot roslynSnapshot,
        string file,
        int line,
        int column,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(file);
        var normalizedPath = RoslynWorkspaceSnapshot.NormalizePath(fullPath);
        var targetLine = Math.Max(0, line - 1);
        var targetCol = Math.Max(0, column - 1);

        foreach (var project in roslynSnapshot.Solution.Projects)
        {
            var compilation = await roslynSnapshot.GetCompilationAsync(project.Id, ct);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!await RoslynWorkspaceSnapshot.SyntaxTreeMapsToFileAsync(tree, normalizedPath, ct))
                {
                    continue;
                }

                var root = await tree.GetRootAsync(ct);
                SyntaxToken? bestToken = null;
                SyntaxToken? fallbackToken = null;

                foreach (var token in root.DescendantTokens())
                {
                    if (token.RawKind == (int)SyntaxKind.EndOfFileToken || string.IsNullOrWhiteSpace(token.Text))
                    {
                        continue;
                    }

                    var mapped = token.GetLocation().GetMappedLineSpan();
                    if (!mapped.HasMappedPath || !RoslynWorkspaceSnapshot.PathMatches(mapped.Path, normalizedPath))
                    {
                        continue;
                    }

                    if (mapped.StartLinePosition.Line <= targetLine && mapped.EndLinePosition.Line >= targetLine)
                    {
                        if (fallbackToken == null)
                        {
                            fallbackToken = token;
                        }

                        var onStartLine = mapped.StartLinePosition.Line == targetLine;
                        var onEndLine = mapped.EndLinePosition.Line == targetLine;

                        if ((!onStartLine || mapped.StartLinePosition.Character <= targetCol) &&
                            (!onEndLine || mapped.EndLinePosition.Character >= targetCol))
                        {
                            bestToken = token;
                            break;
                        }
                    }
                }

                bestToken ??= fallbackToken;

                if (bestToken == null) continue;

                var node = bestToken.Value.Parent;
                if (node == null) continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var symbol = semanticModel.GetSymbolInfo(node, ct).Symbol
                             ?? semanticModel.GetDeclaredSymbol(node, ct);

                if (symbol == null)
                {
                    var parent = node.Parent;
                    while (parent != null && symbol == null)
                    {
                        symbol = semanticModel.GetSymbolInfo(parent, ct).Symbol
                                 ?? semanticModel.GetDeclaredSymbol(parent, ct);
                        parent = parent.Parent;
                    }
                }

                if (symbol != null)
                {
                    var target = MapSymbolToIdentifier(symbol);
                    var targetKind = MapSymbolKind(symbol);
                    var definedLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    var (definedFile, definedLine, _) = GetMappedLocation(definedLoc);

                    return new SymbolResolutionResult(target, definedFile, definedLine, targetKind);
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SymbolResolutionResult>> ResolveSymbolsByNameAsync(
        IWorkspaceSnapshot snapshot,
        string symbolName,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var results = new List<SymbolResolutionResult>();

        // Check if symbolName is a DocumentationCommentId (e.g. M:MyNamespace.MyClass.MyMethod or T:MyNamespace.MyClass)
        if (symbolName.Length > 2 && symbolName[1] == ':' && char.IsLetter(symbolName[0]))
        {
            foreach (var project in roslynSnapshot.Solution.Projects)
            {
                var compilation = await roslynSnapshot.GetCompilationAsync(project.Id, ct);
                if (compilation == null) continue;

                var sym = Microsoft.CodeAnalysis.DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolName, compilation);
                if (sym != null)
                {
                    var target = MapSymbolToIdentifier(sym);
                    var targetKind = MapSymbolKind(sym);
                    var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                    var (defFile, defLine, _) = GetMappedLocation(loc);
                    results.Add(new SymbolResolutionResult(target, defFile, defLine, targetKind));
                    return results;
                }
            }
        }

        // Strip parameter lists if provided (e.g. OrderService.PlaceOrder(OrderDto) -> PlaceOrder)
        var cleanName = symbolName.Contains('(') ? symbolName[..symbolName.IndexOf('(', StringComparison.Ordinal)] : symbolName;
        var memberName = cleanName.Contains('.') ? cleanName[(cleanName.LastIndexOf('.') + 1)..] : cleanName;
        var hasTypeQualification = cleanName.Contains('.');
        var typePrefix = hasTypeQualification ? cleanName[..cleanName.LastIndexOf('.')] : null;
        var simpleTypeName = typePrefix != null && typePrefix.Contains('.') ? typePrefix[(typePrefix.LastIndexOf('.') + 1)..] : typePrefix;

        // Check if querying a constructor (e.g. Customer..ctor, Customer.ctor, or .ctor)
        var isCtor = cleanName.EndsWith("..ctor", StringComparison.OrdinalIgnoreCase) ||
                     cleanName.EndsWith(".ctor", StringComparison.OrdinalIgnoreCase);

        var ctorTypeName = isCtor
            ? (cleanName.Contains("..ctor", StringComparison.OrdinalIgnoreCase)
                ? cleanName[..cleanName.IndexOf("..ctor", StringComparison.OrdinalIgnoreCase)]
                : cleanName[..cleanName.IndexOf(".ctor", StringComparison.OrdinalIgnoreCase)])
            : null;

        if (ctorTypeName != null && ctorTypeName.Contains('.'))
        {
            ctorTypeName = ctorTypeName[(ctorTypeName.LastIndexOf('.') + 1)..];
        }

        foreach (var project in roslynSnapshot.Solution.Projects)
        {
            if (isCtor)
            {
                var typeDeclarations = await SymbolFinder.FindSourceDeclarationsAsync(
                    project,
                    name => string.IsNullOrWhiteSpace(ctorTypeName) || string.Equals(name, ctorTypeName, StringComparison.OrdinalIgnoreCase),
                    SymbolFilter.Type,
                    cancellationToken: ct);

                foreach (var symbol in typeDeclarations.OfType<INamedTypeSymbol>())
                {
                    foreach (var ctor in symbol.Constructors)
                    {
                        var target = MapSymbolToIdentifier(ctor);
                        var loc = ctor.Locations.FirstOrDefault(l => l.IsInSource) ?? symbol.Locations.FirstOrDefault(l => l.IsInSource);
                        var (defFile, defLine, _) = GetMappedLocation(loc);
                        results.Add(new SymbolResolutionResult(target, defFile, defLine, DomainSymbolKind.Constructor));
                    }
                }
                continue;
            }

            var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => string.Equals(name, memberName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, cleanName, StringComparison.OrdinalIgnoreCase),
                cancellationToken: ct);

            foreach (var symbol in declarations)
            {
                var fullDisplayName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

                bool matches;
                if (hasTypeQualification)
                {
                    var containingTypeName = symbol.ContainingType?.Name;
                    var containingTypeFqn = symbol.ContainingType?.ToDisplayString();

                    matches = fullDisplayName.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                              (containingTypeName != null && string.Equals(containingTypeName, simpleTypeName, StringComparison.OrdinalIgnoreCase) && string.Equals(symbol.Name, memberName, StringComparison.OrdinalIgnoreCase)) ||
                              (containingTypeFqn != null && string.Equals(containingTypeFqn, typePrefix, StringComparison.OrdinalIgnoreCase) && string.Equals(symbol.Name, memberName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    matches = fullDisplayName.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(symbol.Name, memberName, StringComparison.OrdinalIgnoreCase);
                }

                if (matches)
                {
                    var target = MapSymbolToIdentifier(symbol);
                    var targetKind = MapSymbolKind(symbol);
                    var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    var (defFile, defLine, _) = GetMappedLocation(loc);

                    results.Add(new SymbolResolutionResult(target, defFile, defLine, targetKind));
                }
            }
        }

        // Deduplicate candidates
        return results
            .GroupBy(r => (r.Target.Assembly, r.Target.ContainingType, r.Target.MemberSignature, r.DefinedIn, r.DefinedLine))
            .Select(g => g.First())
            .ToList();
    }

    public async Task<IReadOnlyList<DomainReferenceLocation>> FindReferencesAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        ReferenceSearchOptions options,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var solution = roslynSnapshot.Solution;

        // Resolve symbol in solution
        var resolvedSymbol = await ResolveSymbolInSolutionAsync(roslynSnapshot, symbol, ct);
        if (resolvedSymbol == null) return Array.Empty<DomainReferenceLocation>();

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(resolvedSymbol, solution, ct);
        var results = new List<DomainReferenceLocation>();
        var mappedSourceTextCache = new Dictionary<string, SourceText>(StringComparer.OrdinalIgnoreCase);

        foreach (var refSymbol in referencedSymbols)
        {
            foreach (var location in refSymbol.Locations)
            {
                var project = location.Document.Project;

                // Filter test projects if requested
                if (options.ExcludeTests && IsTestProject(project))
                {
                    continue;
                }

                // Filter project if specified
                if (!string.IsNullOrWhiteSpace(options.Project) &&
                    !MatchesProject(project, options.Project))
                {
                    continue;
                }

                // Filter framework if specified
                if (!string.IsNullOrWhiteSpace(options.Framework))
                {
                    var projName = project.Name;
                    var outPath = project.OutputFilePath ?? "";
                    if (!projName.Contains(options.Framework, StringComparison.OrdinalIgnoreCase) &&
                        !outPath.Contains(options.Framework, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var mappedSpan = location.Location.GetMappedLineSpan();
                var isMapped = mappedSpan.HasMappedPath && !string.IsNullOrWhiteSpace(mappedSpan.Path);
                var filePath = isMapped ? mappedSpan.Path : location.Document.FilePath;
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                var lineIndex = isMapped ? mappedSpan.StartLinePosition.Line : location.Location.GetLineSpan().StartLinePosition.Line;
                var colIndex = (isMapped ? mappedSpan.StartLinePosition.Character : location.Location.GetLineSpan().StartLinePosition.Character) + 1;

                var isGenerated = !isMapped && (location.Document is SourceGeneratedDocument || IsGeneratedFile(location.Document.FilePath));
                if (!options.IncludeGenerated && isGenerated)
                {
                    continue;
                }

                SourceText sourceText;
                if (isMapped)
                {
                    if (!mappedSourceTextCache.TryGetValue(filePath, out sourceText!))
                    {
                        sourceText = await roslynSnapshot.GetAdditionalDocumentTextAsync(filePath, ct)
                            ?? (File.Exists(filePath) ? SourceText.From(await File.ReadAllTextAsync(filePath, ct)) : null)
                            ?? await location.Document.GetTextAsync(ct);
                        mappedSourceTextCache[filePath] = sourceText;
                    }
                }
                else
                {
                    sourceText = await location.Document.GetTextAsync(ct);
                }

                // Extract context snippet (1-3 lines)
                var snippet = ExtractSnippet(sourceText, lineIndex);

                // Find containing member
                var root = await location.Document.GetSyntaxRootAsync(ct);
                var token = root?.FindToken(location.Location.SourceSpan.Start);
                var containingMember = token?.Parent?.AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>()
                    .FirstOrDefault();

                var memberName = containingMember switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    ConstructorDeclarationSyntax c => c.Identifier.Text,
                    _ => null
                };

                var kind = ClassifyReference(token?.Parent);

                results.Add(new DomainReferenceLocation(
                    File: filePath,
                    Line: lineIndex + 1,
                    Column: colIndex,
                    Snippet: snippet,
                    ContainingMember: memberName,
                    Kind: kind,
                    IsGenerated: isGenerated));
            }
        }

        // Include overrides if requested
        if (options.IncludeOverrides && resolvedSymbol is (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            var overrides = await SymbolFinder.FindOverridesAsync(resolvedSymbol, solution, cancellationToken: ct);
            foreach (var over in overrides)
            {
                foreach (var loc in over.Locations.Where(l => l.IsInSource))
                {
                    await AddLocationFromSourceAsync(loc, ReferenceKind.Override, results, options, roslynSnapshot, mappedSourceTextCache, ct);
                }
            }
        }

        // Include implementations if requested
        if (options.IncludeImplementations && resolvedSymbol is (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(resolvedSymbol, solution, cancellationToken: ct);
            foreach (var impl in implementations)
            {
                foreach (var loc in impl.Locations.Where(l => l.IsInSource))
                {
                    await AddLocationFromSourceAsync(loc, ReferenceKind.Implementation, results, options, roslynSnapshot, mappedSourceTextCache, ct);
                }
            }
        }

        return results;
    }

    private static async Task AddLocationFromSourceAsync(
        Location loc,
        ReferenceKind kind,
        List<DomainReferenceLocation> results,
        ReferenceSearchOptions options,
        RoslynWorkspaceSnapshot roslynSnapshot,
        Dictionary<string, SourceText> mappedSourceTextCache,
        CancellationToken ct)
    {
        var (targetFile, line, col) = GetMappedLocation(loc);
        if (targetFile == null || line == null) return;

        var mapped = loc.GetMappedLineSpan();
        var isMapped = mapped.HasMappedPath && !string.IsNullOrWhiteSpace(mapped.Path);
        var isGen = !isMapped && IsGeneratedFile(loc.SourceTree?.FilePath ?? targetFile);
        if (!options.IncludeGenerated && isGen) return;

        var document = loc.SourceTree != null ? roslynSnapshot.Solution.GetDocument(loc.SourceTree) : null;
        if (document != null)
        {
            if (options.ExcludeTests && IsTestProject(document.Project))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(options.Project) &&
                !MatchesProject(document.Project, options.Project))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(options.Framework))
            {
                var projName = document.Project.Name;
                var outPath = document.Project.OutputFilePath ?? "";
                if (!projName.Contains(options.Framework, StringComparison.OrdinalIgnoreCase) &&
                    !outPath.Contains(options.Framework, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        string snippet = "";
        if (isMapped)
        {
            if (!mappedSourceTextCache.TryGetValue(targetFile, out var sourceText))
            {
                sourceText = await roslynSnapshot.GetAdditionalDocumentTextAsync(targetFile, ct)
                    ?? (File.Exists(targetFile) ? SourceText.From(await File.ReadAllTextAsync(targetFile, ct)) : null);
                if (sourceText != null)
                {
                    mappedSourceTextCache[targetFile] = sourceText;
                }
            }

            if (sourceText != null)
            {
                snippet = ExtractSnippet(sourceText, line.Value - 1);
            }
        }
        else
        {
            var text = loc.SourceTree != null ? await loc.SourceTree.GetTextAsync(ct) : null;
            if (text != null && line.Value - 1 < text.Lines.Count)
            {
                snippet = ExtractSnippet(text, line.Value - 1);
            }
        }

        results.Add(new DomainReferenceLocation(
            File: targetFile,
            Line: line.Value,
            Column: col ?? 1,
            Snippet: snippet,
            ContainingMember: null,
            Kind: kind,
            IsGenerated: isGen));
    }

    private static ReferenceKind ClassifyReference(SyntaxNode? node)
    {
        if (node == null) return ReferenceKind.TypeReference;

        // 1. Invocation expression (e.g. obj.Method(), Method())
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation != null && (invocation.Expression == node || invocation.Expression.Span.Contains(node.Span)))
        {
            return ReferenceKind.Invocation;
        }

        // 2. Object creation expression (e.g. new Customer(), new Ns.Customer())
        var objectCreation = node.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        if (objectCreation != null && (objectCreation.Type == node || objectCreation.Type.Span.Contains(node.Span)))
        {
            return ReferenceKind.Instantiation;
        }

        // 3. Assignment expression
        var assignment = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assignment != null)
        {
            if (assignment.Left.Span.Contains(node.Span))
            {
                return ReferenceKind.Write;
            }
            return ReferenceKind.Read;
        }

        // 4. Type reference in declarations, bases, parameters, generics
        if (node.AncestorsAndSelf().Any(n => n is TypeSyntax or BaseTypeDeclarationSyntax or ParameterSyntax))
        {
            return ReferenceKind.TypeReference;
        }

        return ReferenceKind.Read;
    }

    private static string ExtractSnippet(SourceText sourceText, int lineIndex)
    {
        var startLine = Math.Max(0, lineIndex - 1);
        var endLine = Math.Min(sourceText.Lines.Count - 1, lineIndex + 1);

        var lines = new List<string>();
        for (int i = startLine; i <= endLine; i++)
        {
            lines.Add(sourceText.Lines[i].ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static bool MatchesProject(Project project, string projectFilter)
    {
        if (string.IsNullOrWhiteSpace(projectFilter))
        {
            return false;
        }

        projectFilter = projectFilter.Trim();

        if (string.Equals(project.Name, projectFilter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(project.AssemblyName, projectFilter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(project.FilePath))
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(project.FilePath);
            var fileName = Path.GetFileName(project.FilePath);
            if (string.Equals(fileNameWithoutExt, projectFilter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, projectFilter, StringComparison.OrdinalIgnoreCase) ||
                RoslynWorkspaceSnapshot.PathMatches(projectFilter, project.FilePath))
            {
                return true;
            }
        }

        var filterWithoutExt = projectFilter.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(projectFilter)
            : projectFilter;

        if (!string.IsNullOrWhiteSpace(filterWithoutExt))
        {
            if (string.Equals(project.Name, filterWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.AssemblyName, filterWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (project.Name.StartsWith(filterWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = project.Name[filterWithoutExt.Length..].TrimStart();
                if (suffix.StartsWith('(') && suffix.EndsWith(')'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTestProject(Project project)
    {
        var name = project.Name;
        if (name.Contains(".Tests", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".Test", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return project.MetadataReferences.Any(m =>
        {
            var path = m.Display ?? "";
            return path.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("mstest", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsGeneratedFile(string? filePath) =>
        !string.IsNullOrEmpty(filePath) && (
            filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase));

    private static (string? FilePath, int? Line, int? Column) GetMappedLocation(Location? loc)
    {
        if (loc == null || !loc.IsInSource) return (null, null, null);

        var mapped = loc.GetMappedLineSpan();
        if (mapped.HasMappedPath && !string.IsNullOrWhiteSpace(mapped.Path))
        {
            return (
                mapped.Path,
                mapped.IsValid ? mapped.StartLinePosition.Line + 1 : null,
                mapped.IsValid ? mapped.StartLinePosition.Character + 1 : null
            );
        }

        var lineSpan = loc.GetLineSpan();
        return (
            loc.SourceTree?.FilePath,
            lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : null,
            lineSpan.IsValid ? lineSpan.StartLinePosition.Character + 1 : null
        );
    }

    private static SymbolIdentifier MapSymbolToIdentifier(ISymbol symbol)
    {
        return new SymbolIdentifier(
            Assembly: symbol.ContainingAssembly?.Name ?? "",
            Namespace: symbol.ContainingNamespace?.ToDisplayString(),
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            MemberSignature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            SymbolId: symbol.GetDocumentationCommentId());
    }

    private static DomainSymbolKind MapSymbolKind(ISymbol symbol) =>
        symbol switch
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

    private static string ExtractIdentifierName(string signature)
    {
        var clean = signature;
        if (clean.Contains('('))
        {
            clean = clean[..clean.IndexOf('(', StringComparison.Ordinal)];
        }
        if (clean.Contains('.'))
        {
            clean = clean[(clean.LastIndexOf('.') + 1)..];
        }
        if (clean.Contains(' '))
        {
            clean = clean[(clean.LastIndexOf(' ') + 1)..];
        }
        return clean.Trim();
    }

    public async Task<IReadOnlyList<DefinitionLocation>> FindDefinitionsAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        DefinitionSearchOptions options,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var solution = roslynSnapshot.Solution;

        var resolvedSymbol = await ResolveSymbolInSolutionAsync(roslynSnapshot, symbol, ct);
        if (resolvedSymbol == null) return Array.Empty<DefinitionLocation>();

        var sourceDef = await SymbolFinder.FindSourceDefinitionAsync(resolvedSymbol, solution, cancellationToken: ct);
        var targetSymbol = sourceDef ?? resolvedSymbol.OriginalDefinition ?? resolvedSymbol;

        var results = new List<DefinitionLocation>();
        var sourceLocations = targetSymbol.Locations.Where(l => l.IsInSource && l.SourceTree != null).ToList();

        if (sourceLocations.Count > 0)
        {
            foreach (var loc in sourceLocations)
            {
                var mapped = loc.GetMappedLineSpan();
                var isMapped = mapped.HasMappedPath && !string.IsNullOrWhiteSpace(mapped.Path);
                var filePath = isMapped ? mapped.Path : loc.SourceTree?.FilePath;
                if (filePath == null) continue;

                var isGen = !isMapped && IsGeneratedFile(loc.SourceTree?.FilePath ?? filePath);
                if (!options.IncludeGenerated && isGen) continue;

                var document = roslynSnapshot.Solution.GetDocument(loc.SourceTree);
                if (document != null)
                {
                    if (!string.IsNullOrWhiteSpace(options.Project) &&
                        !MatchesProject(document.Project, options.Project))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(options.Framework))
                    {
                        var projName = document.Project.Name;
                        var outPath = document.Project.OutputFilePath ?? "";
                        if (!projName.Contains(options.Framework, StringComparison.OrdinalIgnoreCase) &&
                            !outPath.Contains(options.Framework, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                }

                var span = isMapped ? mapped : loc.GetLineSpan();
                var line = span.StartLinePosition.Line + 1;
                var col = span.StartLinePosition.Character + 1;
                var endLine = GetDeclarationEndLine(targetSymbol, loc, span, isMapped);

                SourceText sourceText;
                if (isMapped)
                {
                    sourceText = await roslynSnapshot.GetAdditionalDocumentTextAsync(filePath, ct)
                        ?? (File.Exists(filePath) ? SourceText.From(await File.ReadAllTextAsync(filePath, ct)) : null)
                        ?? await loc.SourceTree!.GetTextAsync(ct);
                }
                else
                {
                    sourceText = await loc.SourceTree!.GetTextAsync(ct);
                }
                var snippet = ExtractSnippet(sourceText, line - 1);

                results.Add(new DefinitionLocation(
                    File: filePath,
                    Line: line,
                    Column: col,
                    Snippet: snippet,
                    ContainingMember: targetSymbol is not INamedTypeSymbol ? targetSymbol.Name : null,
                    ContainingType: targetSymbol.ContainingType?.ToDisplayString() ?? (targetSymbol as INamedTypeSymbol)?.ToDisplayString(),
                    Kind: MapSymbolKind(targetSymbol),
                    IsMetadata: false,
                    IsGenerated: isGen,
                    SymbolId: targetSymbol.GetDocumentationCommentId(),
                    EndLine: endLine));
            }
        }
        else
        {
            results.Add(new DefinitionLocation(
                File: null,
                Line: null,
                Column: null,
                Snippet: null,
                ContainingMember: targetSymbol is not INamedTypeSymbol ? targetSymbol.Name : null,
                ContainingType: targetSymbol.ContainingType?.ToDisplayString(),
                AssemblyName: targetSymbol.ContainingAssembly?.Name,
                Kind: MapSymbolKind(targetSymbol),
                IsMetadata: true,
                IsGenerated: false,
                SymbolId: targetSymbol.GetDocumentationCommentId()));
        }

        return results
            .GroupBy(r => (r.File, r.Line, r.Column, r.SymbolId))
            .Select(g => g.First())
            .ToList();
    }

    public async Task<IReadOnlyList<ImplementationLocation>> FindImplementationsAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        ImplementationSearchOptions options,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var solution = roslynSnapshot.Solution;

        var resolvedSymbol = await ResolveSymbolInSolutionAsync(roslynSnapshot, symbol, ct);
        if (resolvedSymbol == null) return Array.Empty<ImplementationLocation>();

        var rawTargets = new List<(ISymbol Symbol, string Kind)>();

        if (resolvedSymbol is INamedTypeSymbol namedType)
        {
            if (namedType.TypeKind == TypeKind.Interface)
            {
                var impls = await SymbolFinder.FindImplementationsAsync(namedType, solution, cancellationToken: ct);
                foreach (var impl in impls)
                {
                    rawTargets.Add((impl, "Implementation"));
                }

                var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(namedType, solution, cancellationToken: ct);
                foreach (var di in derivedInterfaces)
                {
                    rawTargets.Add((di, "DerivedInterface"));
                }
            }
            else if (namedType.TypeKind == TypeKind.Class)
            {
                var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(namedType, solution, cancellationToken: ct);
                foreach (var dc in derivedClasses)
                {
                    rawTargets.Add((dc, "DerivedClass"));
                }
            }
        }
        else if (resolvedSymbol is (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            var impls = await SymbolFinder.FindImplementationsAsync(resolvedSymbol, solution, cancellationToken: ct);
            foreach (var impl in impls)
            {
                rawTargets.Add((impl, "Implementation"));
            }

            var overrides = await SymbolFinder.FindOverridesAsync(resolvedSymbol, solution, cancellationToken: ct);
            foreach (var over in overrides)
            {
                rawTargets.Add((over, "Override"));
            }
        }

        var results = new List<ImplementationLocation>();

        foreach (var (target, kind) in rawTargets)
        {
            foreach (var loc in target.Locations.Where(l => l.IsInSource && l.SourceTree != null))
            {
                var mapped = loc.GetMappedLineSpan();
                var isMapped = mapped.HasMappedPath && !string.IsNullOrWhiteSpace(mapped.Path);
                var filePath = isMapped ? mapped.Path : loc.SourceTree?.FilePath;
                if (filePath == null) continue;

                var isGen = !isMapped && IsGeneratedFile(loc.SourceTree?.FilePath ?? filePath);
                if (!options.IncludeGenerated && isGen) continue;

                var document = roslynSnapshot.Solution.GetDocument(loc.SourceTree);
                if (document != null)
                {
                    if (options.ExcludeTests && IsTestProject(document.Project))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(options.Project) &&
                        !MatchesProject(document.Project, options.Project))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(options.Framework))
                    {
                        var projName = document.Project.Name;
                        var outPath = document.Project.OutputFilePath ?? "";
                        if (!projName.Contains(options.Framework, StringComparison.OrdinalIgnoreCase) &&
                            !outPath.Contains(options.Framework, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                }

                var line = (isMapped ? mapped.StartLinePosition.Line : loc.GetLineSpan().StartLinePosition.Line) + 1;
                var col = (isMapped ? mapped.StartLinePosition.Character : loc.GetLineSpan().StartLinePosition.Character) + 1;

                SourceText sourceText;
                if (isMapped)
                {
                    sourceText = await roslynSnapshot.GetAdditionalDocumentTextAsync(filePath, ct)
                        ?? (File.Exists(filePath) ? SourceText.From(await File.ReadAllTextAsync(filePath, ct)) : null)
                        ?? await loc.SourceTree!.GetTextAsync(ct);
                }
                else
                {
                    sourceText = await loc.SourceTree!.GetTextAsync(ct);
                }
                var snippet = ExtractSnippet(sourceText, line - 1);

                results.Add(new ImplementationLocation(
                    File: filePath,
                    Line: line,
                    Column: col,
                    Snippet: snippet,
                    ContainingMember: target is not INamedTypeSymbol ? target.Name : null,
                    ContainingType: target.ContainingType?.ToDisplayString() ?? (target as INamedTypeSymbol)?.ToDisplayString(),
                    Kind: kind,
                    IsGenerated: isGen));
            }
        }

        return results
            .GroupBy(r => (r.File, r.Line, r.Column))
            .Select(g => g.First())
            .ToList();
    }

    private static async Task<ISymbol?> ResolveSymbolInSolutionAsync(
        RoslynWorkspaceSnapshot roslynSnapshot,
        SymbolIdentifier symbol,
        CancellationToken ct)
    {
        var solution = roslynSnapshot.Solution;
        ISymbol? resolvedSymbol = null;

        foreach (var project in solution.Projects)
        {
            var compilation = await roslynSnapshot.GetCompilationAsync(project.Id, ct);
            if (compilation == null) continue;

            if (!string.IsNullOrWhiteSpace(symbol.SymbolId))
            {
                resolvedSymbol = Microsoft.CodeAnalysis.DocumentationCommentId.GetFirstSymbolForDeclarationId(symbol.SymbolId, compilation);
                if (resolvedSymbol != null) break;
            }

            if (!string.IsNullOrWhiteSpace(symbol.MemberSignature))
            {
                var cleanMemberName = ExtractIdentifierName(symbol.MemberSignature);
                var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                    project,
                    name => string.Equals(name, cleanMemberName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, symbol.MemberSignature, StringComparison.OrdinalIgnoreCase),
                    cancellationToken: ct);

                resolvedSymbol = string.IsNullOrWhiteSpace(symbol.ContainingType)
                    ? declarations.FirstOrDefault()
                    : declarations.FirstOrDefault(d =>
                        string.Equals(d.ContainingType?.ToDisplayString(), symbol.ContainingType, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.ContainingType?.Name, symbol.ContainingType, StringComparison.OrdinalIgnoreCase));

                if (resolvedSymbol != null) break;
            }
            else if (!string.IsNullOrWhiteSpace(symbol.ContainingType))
            {
                resolvedSymbol = compilation.GetTypeByMetadataName(symbol.ContainingType);
                if (resolvedSymbol != null) break;

                var cleanTypeName = symbol.ContainingType.Contains('.')
                    ? symbol.ContainingType[(symbol.ContainingType.LastIndexOf('.') + 1)..]
                    : symbol.ContainingType;

                var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                    project,
                    name => string.Equals(name, cleanTypeName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, symbol.ContainingType, StringComparison.OrdinalIgnoreCase),
                    SymbolFilter.Type,
                    cancellationToken: ct);

                resolvedSymbol = declarations.FirstOrDefault(d =>
                    string.Equals(d.ToDisplayString(), symbol.ContainingType, StringComparison.OrdinalIgnoreCase))
                    ?? declarations.FirstOrDefault();
                if (resolvedSymbol != null) break;
            }
        }

        return resolvedSymbol;
    }

    internal static int GetDeclarationEndLine(ISymbol symbol, Location loc, FileLinePositionSpan identifierSpan, bool isMapped)
    {
        var endLine = identifierSpan.EndLinePosition.Line + 1;
        var tree = loc.SourceTree;
        if (tree == null)
        {
            return endLine;
        }

        var syntaxRef = symbol.DeclaringSyntaxReferences
            .FirstOrDefault(r => r.SyntaxTree == tree && r.Span.Contains(loc.SourceSpan));
        if (syntaxRef == null)
        {
            return endLine;
        }

        var span = syntaxRef.Span;
        if (symbol is IFieldSymbol or IEventSymbol)
        {
            var node = syntaxRef.GetSyntax();
            var fieldDecl = node.Parent?.Parent;
            if (fieldDecl is Microsoft.CodeAnalysis.CSharp.Syntax.BaseFieldDeclarationSyntax)
            {
                span = fieldDecl.Span;
            }
        }

        if (isMapped)
        {
            var mappedSyntaxSpan = tree.GetMappedLineSpan(span);
            if (mappedSyntaxSpan.HasMappedPath &&
                string.Equals(mappedSyntaxSpan.Path, identifierSpan.Path, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(endLine, mappedSyntaxSpan.EndLinePosition.Line + 1);
            }
            return endLine;
        }

        var declLineSpan = tree.GetLineSpan(span);
        return Math.Max(endLine, declLineSpan.EndLinePosition.Line + 1);
    }
}

