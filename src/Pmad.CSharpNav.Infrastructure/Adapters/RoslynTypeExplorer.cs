using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;
using Pmad.CSharpNav.Infrastructure.Workspace;
using DomainAccessibility = Pmad.CSharpNav.Domain.Accessibility;
using DomainSymbolKind = Pmad.CSharpNav.Domain.SymbolKind;

namespace Pmad.CSharpNav.Infrastructure.Adapters;

/// <summary>
/// Semantic type and member inspector, evaluating class declarations, hierarchies,
/// and variable inferred types across partial classes and compilation models.
/// </summary>
public sealed class RoslynTypeExplorer : ITypeExplorer
{
    public async Task<TypeExplorationResult?> ExploreTypeByNameAsync(
        IWorkspaceSnapshot snapshot,
        string typeName,
        ExploreOptions options,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        INamedTypeSymbol? foundType = null;

        foreach (var project in roslynSnapshot.Solution.Projects)
        {
            var compilation = await roslynSnapshot.GetCompilationAsync(project.Id, ct);
            if (compilation == null) continue;

            // Try exact metadata name lookup
            foundType = compilation.GetTypeByMetadataName(typeName);
            if (foundType != null) break;

            // If generic, try normalizing to arity notation
            var cleanTypeName = NormalizeTypeNameForMetadata(typeName);
            if (cleanTypeName != typeName)
            {
                foundType = compilation.GetTypeByMetadataName(cleanTypeName);
                if (foundType != null) break;
            }

            // Try finding by simple name in source declarations
            var simpleName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
            var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.Type,
                cancellationToken: ct);

            foundType = declarations.OfType<INamedTypeSymbol>().FirstOrDefault();
            if (foundType != null) break;
        }

        // If not found in source, search external referenced assemblies
        if (foundType == null)
        {
            var preferredProject = roslynSnapshot.GetPreferredProject(targetFramework: options.Framework);
            if (preferredProject != null)
            {
                var compilation = await roslynSnapshot.GetCompilationAsync(preferredProject.Id, ct);
                if (compilation != null)
                {
                    foundType = FindExternalType(compilation, typeName);
                }
            }
        }

        if (foundType == null) return null;

        return BuildExplorationResult(foundType, options);
    }

    public async Task<TypeExplorationResult?> ExploreTypeAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        ExploreOptions options,
        CancellationToken ct = default)
    {
        var roslynSnapshot = (RoslynWorkspaceSnapshot)snapshot;
        var docId = roslynSnapshot.FindDocumentId(file);
        if (docId == null) return null;

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

        var typeInfo = semanticModel.GetTypeInfo(node, ct);
        var typeSymbol = typeInfo.Type ?? (semanticModel.GetSymbolInfo(node, ct).Symbol as ITypeSymbol);

        if (typeSymbol == null)
        {
            var parent = node.Parent;
            while (parent != null && typeSymbol == null)
            {
                typeInfo = semanticModel.GetTypeInfo(parent, ct);
                typeSymbol = typeInfo.Type ?? (semanticModel.GetSymbolInfo(parent, ct).Symbol as ITypeSymbol);
                parent = parent.Parent;
            }
        }

        if (typeSymbol is not INamedTypeSymbol namedType)
        {
            return null;
        }

        return BuildExplorationResult(namedType, options);
    }

    private static TypeExplorationResult BuildExplorationResult(INamedTypeSymbol type, ExploreOptions options)
    {
        var members = new List<TypeMemberInfo>();

        var allMembers = options.NoInherited
            ? type.GetMembers().ToList()
            : GetAllMembersRecursive(type);

        foreach (var member in allMembers)
        {
            if (member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            {
                continue;
            }

            var accessibility = MapAccessibility(member.DeclaredAccessibility);
            if (options.Access == "public" && accessibility != DomainAccessibility.Public)
            {
                continue;
            }
            if (options.Access == "internal" && accessibility is not (DomainAccessibility.Public or DomainAccessibility.Internal))
            {
                continue;
            }

            var kind = MapSymbolKind(member);
            var modifiers = GetModifiers(member);
            var signature = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var summary = ExtractDocumentation(member);

            string? accessors = null;
            if (member is IPropertySymbol prop)
            {
                var parts = new List<string>();
                if (prop.GetMethod != null)
                {
                    var getAcc = prop.GetMethod.DeclaredAccessibility != prop.DeclaredAccessibility
                        ? $"{prop.GetMethod.DeclaredAccessibility.ToString().ToLowerInvariant()} get;"
                        : "get;";
                    parts.Add(getAcc);
                }
                if (prop.SetMethod != null)
                {
                    var setWord = prop.SetMethod.IsInitOnly ? "init;" : "set;";
                    var setAcc = prop.SetMethod.DeclaredAccessibility != prop.DeclaredAccessibility
                        ? $"{prop.SetMethod.DeclaredAccessibility.ToString().ToLowerInvariant()} {setWord}"
                        : setWord;
                    parts.Add(setAcc);
                }
                if (parts.Count > 0)
                {
                    accessors = string.Join(" ", parts);
                }
            }

            string? returnType = member switch
            {
                IMethodSymbol m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                IFieldSymbol f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                _ => null
            };

            members.Add(new TypeMemberInfo(
                Name: member.Name,
                Kind: kind,
                Signature: signature,
                Accessibility: accessibility,
                Modifiers: modifiers,
                ReturnType: returnType,
                Summary: summary,
                Accessors: accessors));
        }

        var baseType = type.BaseType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var interfaces = type.AllInterfaces
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();

        var typeModifiers = GetTypeModifiers(type);
        var typeSummary = ExtractDocumentation(type);

        return new TypeExplorationResult(
            Name: type.Name,
            FullName: type.ToDisplayString(),
            Kind: MapSymbolKind(type),
            Accessibility: MapAccessibility(type.DeclaredAccessibility),
            Modifiers: typeModifiers,
            BaseType: baseType,
            Interfaces: interfaces,
            Summary: typeSummary,
            Members: members);
    }

    private static INamedTypeSymbol? FindExternalType(Compilation compilation, string typeName)
    {
        var cleanTypeName = NormalizeTypeNameForMetadata(typeName);
        var simpleName = cleanTypeName.Contains('.') ? cleanTypeName[(cleanTypeName.LastIndexOf('.') + 1)..] : cleanTypeName;

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;

            var direct = asm.GetTypeByMetadataName(cleanTypeName) ?? asm.GetTypeByMetadataName(typeName);
            if (direct != null) return direct;

            var stack = new Stack<INamespaceSymbol>();
            stack.Push(asm.GlobalNamespace);

            while (stack.Count > 0)
            {
                var ns = stack.Pop();
                foreach (var child in ns.GetNamespaceMembers()) stack.Push(child);

                foreach (var type in ns.GetTypeMembers())
                {
                    if (string.Equals(type.Name, simpleName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.ToDisplayString(), typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }
                }
            }
        }

        return null;
    }

    public static string NormalizeTypeNameForMetadata(string typeName)
    {
        if (!typeName.Contains('<')) return typeName;

        var baseName = typeName[..typeName.IndexOf('<', StringComparison.Ordinal)];
        var genericPart = typeName[(typeName.IndexOf('<', StringComparison.Ordinal) + 1)..typeName.LastIndexOf('>')];
        var argCount = genericPart.Count(c => c == ',') + 1;

        return $"{baseName}`{argCount}";
    }

    private static readonly char[] SummarySplitChars = ['\r', '\n', '\t', ' '];

    private static List<ISymbol> GetAllMembersRecursive(INamedTypeSymbol type)
    {
        var memberList = new List<ISymbol>();
        var seen = new HashSet<string>();

        INamedTypeSymbol? current = type;
        while (current != null)
        {
            foreach (var m in current.GetMembers())
            {
                var sig = m.ToDisplayString();
                if (seen.Add(sig))
                {
                    memberList.Add(m);
                }
            }
            current = current.BaseType;
        }

        return memberList;
    }

    private static List<string> GetModifiers(ISymbol member)
    {
        var list = new List<string>();
        if (member.IsStatic) list.Add("static");
        if (member.IsAbstract) list.Add("abstract");
        if (member.IsVirtual) list.Add("virtual");
        if (member.IsOverride) list.Add("override");
        if (member is IMethodSymbol { IsAsync: true }) list.Add("async");
        if (member is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true }) list.Add("readonly");
        return list;
    }

    private static List<string> GetTypeModifiers(INamedTypeSymbol type)
    {
        var list = new List<string>();
        if (type.IsStatic) list.Add("static");
        if (type.IsSealed) list.Add("sealed");
        if (type.IsAbstract && !type.IsStatic) list.Add("abstract");
        if (type.IsRecord) list.Add("record");
        return list;
    }

    private static DomainAccessibility MapAccessibility(Microsoft.CodeAnalysis.Accessibility acc) =>
        acc switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => DomainAccessibility.Public,
            Microsoft.CodeAnalysis.Accessibility.Internal => DomainAccessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.Protected => DomainAccessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.Private => DomainAccessibility.Private,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => DomainAccessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => DomainAccessibility.PrivateProtected,
            _ => DomainAccessibility.Private
        };

    private static DomainSymbolKind MapSymbolKind(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol t when t.TypeKind == TypeKind.Class && t.IsRecord => DomainSymbolKind.Record,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Class => DomainSymbolKind.Class,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Interface => DomainSymbolKind.Interface,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Struct => DomainSymbolKind.Struct,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Enum => DomainSymbolKind.Enum,
            INamedTypeSymbol t when t.TypeKind == TypeKind.Delegate => DomainSymbolKind.Delegate,
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => DomainSymbolKind.Constructor,
            IMethodSymbol => DomainSymbolKind.Method,
            IPropertySymbol => DomainSymbolKind.Property,
            IFieldSymbol => DomainSymbolKind.Field,
            IEventSymbol => DomainSymbolKind.Event,
            _ => DomainSymbolKind.Class
        };

    public static string? ExtractDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(xml))
        {
            var summary = ExtractXmlSummary(xml);
            if (!string.IsNullOrWhiteSpace(summary)) return summary;
        }

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            var leadingTrivia = node.GetLeadingTrivia();
            var docTrivia = leadingTrivia.FirstOrDefault(t =>
                t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia));

            if (docTrivia != default)
            {
                var text = docTrivia.ToFullString();
                var cleaned = string.Join("\n", text.Split('\n')
                    .Select(line => line.TrimStart().TrimStart('/').Trim()));
                var summary = ExtractXmlSummary(cleaned);
                if (!string.IsNullOrWhiteSpace(summary)) return summary;
            }
        }

        return null;
    }

    public static string? ExtractFullDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(xml))
        {
            var fullText = ExtractXmlFullText(xml);
            if (!string.IsNullOrWhiteSpace(fullText)) return fullText;
        }

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            var leadingTrivia = node.GetLeadingTrivia();
            var docTrivia = leadingTrivia.FirstOrDefault(t =>
                t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia));

            if (docTrivia != default)
            {
                var text = docTrivia.ToFullString();
                var cleaned = string.Join("\n", text.Split('\n')
                    .Select(line => line.TrimStart().TrimStart('/').Trim()));
                var fullText = ExtractXmlFullText(cleaned);
                if (!string.IsNullOrWhiteSpace(fullText)) return fullText;
            }
        }

        return null;
    }

    public static string? ExtractXmlSummary(string? xmlDoc)
    {
        if (string.IsNullOrWhiteSpace(xmlDoc)) return null;

        try
        {
            var element = XElement.Parse($"<root>{xmlDoc}</root>");
            var summary = element.Element("summary")?.Value;
            if (summary != null)
            {
                return string.Join(" ", summary.Split(SummarySplitChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
            }
        }
        catch
        {
            // Invalid or unparseable XML comments are ignored
        }

        return null;
    }

    public static string? ExtractXmlFullText(string? xmlDoc)
    {
        if (string.IsNullOrWhiteSpace(xmlDoc)) return null;

        try
        {
            var element = XElement.Parse($"<root>{xmlDoc}</root>");
            var parts = new List<string>();

            var summary = element.Element("summary")?.Value;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                parts.Add(string.Join(" ", summary.Split(SummarySplitChars, StringSplitOptions.RemoveEmptyEntries)).Trim());
            }

            foreach (var param in element.Elements("param"))
            {
                var val = param.Value;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    parts.Add(string.Join(" ", val.Split(SummarySplitChars, StringSplitOptions.RemoveEmptyEntries)).Trim());
                }
            }

            var returns = element.Element("returns")?.Value;
            if (!string.IsNullOrWhiteSpace(returns))
            {
                parts.Add(string.Join(" ", returns.Split(SummarySplitChars, StringSplitOptions.RemoveEmptyEntries)).Trim());
            }

            var remarks = element.Element("remarks")?.Value;
            if (!string.IsNullOrWhiteSpace(remarks))
            {
                parts.Add(string.Join(" ", remarks.Split(SummarySplitChars, StringSplitOptions.RemoveEmptyEntries)).Trim());
            }

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch
        {
            return null;
        }
    }
}
