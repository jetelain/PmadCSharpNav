using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;
using Pmad.CSharpNav.Infrastructure.Workspace;

namespace Pmad.CSharpNav.Infrastructure.Adapters;

/// <summary>
/// Decompiles external assemblies, classes, or single methods using ICSharpCode.Decompiler.
/// Resolves reference assemblies to runtime implementation assemblies.
/// </summary>
public sealed class ILSpyDecompiler : IAssemblyDecompiler
{
    public async Task<DecompiledSource?> DecompileAsync(
        IWorkspaceSnapshot? snapshot,
        DecompileCriteria criteria,
        CancellationToken ct = default)
    {
        var roslynSnapshot = snapshot as RoslynWorkspaceSnapshot;
        var preferredProject = roslynSnapshot?.GetPreferredProject(targetFramework: criteria.Framework);
        var compilation = preferredProject != null
            ? await roslynSnapshot!.GetCompilationAsync(preferredProject.Id, ct)
            : null;

        string? assemblyPath = null;
        string? targetTypeName = StripDocIdPrefix(criteria.Type);
        string? targetMemberName = criteria.Member;
        string? fallbackTypeFromSymbol = null;
        string? fallbackMemberFromSymbol = null;

        // If queried by source code position: file, line, [column]
        if (!string.IsNullOrWhiteSpace(criteria.File) && criteria.Line.HasValue)
        {
            if (roslynSnapshot == null) return null;
            var col = criteria.Column ?? 1;
            var docId = roslynSnapshot.FindDocumentId(criteria.File);
            if (docId != null)
            {
                var doc = roslynSnapshot.Solution.GetDocument(docId);
                if (doc != null)
                {
                    var docCompilation = await roslynSnapshot.GetCompilationAsync(doc.Project.Id, ct);
                    if (docCompilation != null)
                    {
                        compilation = docCompilation;
                    }
                    var sourceText = await doc.GetTextAsync(ct);
                    if (criteria.Line.Value < 1 || criteria.Line.Value > sourceText.Lines.Count)
                    {
                        return null;
                    }
                    var textLine = sourceText.Lines[criteria.Line.Value - 1];
                    var pos = textLine.Start + Math.Clamp(col - 1, 0, textLine.Span.Length);
                    var semModel = await doc.GetSemanticModelAsync(ct);
                    var root = await doc.GetSyntaxRootAsync(ct);
                    if (semModel != null && root != null)
                    {
                        var token = root.FindToken(pos);
                        var node = token.Parent;
                        var symbol = node != null ? (semModel.GetSymbolInfo(node, ct).Symbol ?? semModel.GetDeclaredSymbol(node, ct)) : null;

                        if (symbol == null && node != null)
                        {
                            var parent = node.Parent;
                            while (parent != null && symbol == null)
                            {
                                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax)
                                {
                                    break;
                                }
                                symbol = semModel.GetSymbolInfo(parent, ct).Symbol ?? semModel.GetDeclaredSymbol(parent, ct);
                                parent = parent.Parent;
                            }
                        }

                        if (symbol == null || (!criteria.Column.HasValue && symbol.Locations.Any(l => l.IsInSource)))
                        {
                            foreach (var tok in root.DescendantTokens(textLine.Span))
                            {
                                if (tok.Parent == null) continue;
                                var cand = semModel.GetSymbolInfo(tok.Parent, ct).Symbol;
                                if (cand != null && !cand.Locations.Any(l => l.IsInSource))
                                {
                                    symbol = cand;
                                    break;
                                }
                                symbol ??= cand;
                            }
                        }

                        if (symbol is ILocalSymbol localSym)
                        {
                            symbol = localSym.Type;
                        }
                        else if (symbol is IParameterSymbol paramSym)
                        {
                            symbol = paramSym.Type;
                        }
                        else if (symbol is IFieldSymbol fieldSym && fieldSym.Locations.Any(l => l.IsInSource))
                        {
                            symbol = fieldSym.Type;
                        }
                        else if (symbol is IPropertySymbol propSym && propSym.Locations.Any(l => l.IsInSource))
                        {
                            symbol = propSym.Type;
                        }
                        else if (symbol is IEventSymbol eventSym && eventSym.Locations.Any(l => l.IsInSource))
                        {
                            symbol = eventSym.Type;
                        }

                        if (symbol != null)
                        {
                            var containingType = (symbol as INamedTypeSymbol) ?? symbol.ContainingType;
                            if (containingType != null)
                            {
                                targetTypeName = containingType.OriginalDefinition.ToDisplayString();
                            }
                            if (symbol is not INamedTypeSymbol)
                            {
                                targetMemberName = symbol.Name;
                            }

                            if (compilation != null)
                            {
                                assemblyPath = ResolveAssemblyPath(compilation, symbol.ContainingAssembly);
                            }
                        }
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(criteria.Symbol))
        {
            // Parse FQN symbol or Doc-ID like T:System.Uri, M:System.Uri.IsLoopback, or System.Collections.Generic.List<T>.Add
            var fullSym = criteria.Symbol.Trim();
            char? docPrefix = null;
            if (fullSym.Length > 2 && fullSym[1] == ':' && char.IsLetter(fullSym[0]))
            {
                docPrefix = char.ToUpperInvariant(fullSym[0]);
                if (compilation != null)
                {
                    var resolvedDocSym = DocumentationCommentId.GetFirstSymbolForDeclarationId(fullSym, compilation);
                    if (resolvedDocSym != null)
                    {
                        var containingType = (resolvedDocSym as INamedTypeSymbol) ?? resolvedDocSym.ContainingType;
                        if (containingType != null)
                        {
                            targetTypeName = containingType.OriginalDefinition.ToDisplayString();
                        }
                        if (resolvedDocSym is not INamedTypeSymbol && string.IsNullOrWhiteSpace(targetMemberName))
                        {
                            targetMemberName = resolvedDocSym.Name;
                        }
                        assemblyPath = ResolveAssemblyPath(compilation, resolvedDocSym.ContainingAssembly);
                    }
                }
                fullSym = fullSym[2..];
            }

            if (string.IsNullOrWhiteSpace(targetTypeName))
            {
                var hadParens = false;
                var parenIdx = fullSym.IndexOf('(');
                if (parenIdx >= 0)
                {
                    fullSym = fullSym[..parenIdx];
                    hadParens = true;
                }

                if (docPrefix == 'T')
                {
                    targetTypeName = fullSym;
                }
                else
                {
                    var (splitType, splitMember) = SplitTypeAndMember(fullSym);
                    if (docPrefix != null || hadParens || splitMember is ".ctor" or ".cctor")
                    {
                        targetTypeName = splitType;
                        if (string.IsNullOrWhiteSpace(targetMemberName))
                        {
                            targetMemberName = splitMember;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(targetMemberName) || splitMember == null)
                    {
                        targetTypeName = fullSym;
                    }
                    else
                    {
                        // Unprefixed dotted symbol without parentheses (e.g. "System.Uri" vs "System.Uri.IsLoopback"):
                        // Try resolving fullSym as a type first; fall back to splitType + splitMember if not a type.
                        targetTypeName = fullSym;
                        fallbackTypeFromSymbol = splitType;
                        fallbackMemberFromSymbol = splitMember;
                    }
                }
            }
        }

        assemblyPath ??= await TryResolveAssemblyPathAsync(roslynSnapshot, preferredProject, compilation, targetTypeName, criteria.Assembly, ct);

        if (assemblyPath == null && fallbackTypeFromSymbol != null)
        {
            var fallbackPath = await TryResolveAssemblyPathAsync(roslynSnapshot, preferredProject, compilation, fallbackTypeFromSymbol, criteria.Assembly, ct);
            if (fallbackPath != null)
            {
                targetTypeName = fallbackTypeFromSymbol;
                targetMemberName ??= fallbackMemberFromSymbol;
                assemblyPath = fallbackPath;
                fallbackTypeFromSymbol = null;
            }
        }

        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            var isReferenced = compilation != null && !string.IsNullOrWhiteSpace(criteria.Assembly) &&
                compilation.References.Any(r => r.Display != null && r.Display.Contains(criteria.Assembly, StringComparison.OrdinalIgnoreCase));

            if (isReferenced)
            {
                throw new Pmad.CSharpNav.Application.Exceptions.NavException(
                    "NUGET_RESTORE_REQUIRED",
                    $"Referenced assembly '{criteria.Assembly}' was not found on disk. Please run 'dotnet restore' on the solution.");
            }

            return null;
        }

        // Map reference assembly to implementation assembly
        var (finalAssemblyPath, isRefAssemblyOnly) = MapToImplementationAssembly(assemblyPath);

        if (string.IsNullOrWhiteSpace(targetTypeName))
        {
            return null;
        }

        var result = DecompileInternal(finalAssemblyPath, targetTypeName, targetMemberName, isRefAssemblyOnly);
        if (result == null && fallbackTypeFromSymbol != null)
        {
            var fallbackPath = await TryResolveAssemblyPathAsync(roslynSnapshot, preferredProject, compilation, fallbackTypeFromSymbol, criteria.Assembly, ct) ?? assemblyPath;
            var (finalFallbackPath, fallbackRefOnly) = MapToImplementationAssembly(fallbackPath);
            result = DecompileInternal(finalFallbackPath, fallbackTypeFromSymbol, fallbackMemberFromSymbol, fallbackRefOnly);
        }

        return result;
    }

    private static DecompiledSource? DecompileInternal(
        string assemblyPath,
        string typeName,
        string? memberName,
        bool isRefAssemblyOnly)
    {
        var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
        {
            ThrowOnAssemblyResolveErrors = false
        };

        var decompiler = new CSharpDecompiler(assemblyPath, settings);

        // Normalize type name for ILSpy (e.g. List<T> -> List`1)
        var cleanTypeName = NormalizeTypeNameForMetadata(typeName);
        var fullTypeName = new FullTypeName(cleanTypeName);
        var typeSystem = decompiler.TypeSystem;
        var typeDefinition = typeSystem.MainModule.Compilation.FindType(fullTypeName).GetDefinition();

        if (typeDefinition == null)
        {
            // Try searching case-insensitively
            typeDefinition = typeSystem.MainModule.TypeDefinitions.FirstOrDefault(t =>
                string.Equals(t.FullName, cleanTypeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, cleanTypeName, StringComparison.OrdinalIgnoreCase));
        }

        if (typeDefinition == null)
        {
            return null;
        }

        if (typeDefinition.ParentModule != null && typeDefinition.ParentModule != typeSystem.MainModule)
        {
            var targetAssemblyName = typeDefinition.ParentModule.AssemblyName;
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null && !string.IsNullOrWhiteSpace(targetAssemblyName))
            {
                var targetPath = Path.Combine(runtimeDir, targetAssemblyName + ".dll");
                if (File.Exists(targetPath) && !string.Equals(assemblyPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return DecompileInternal(targetPath, typeName, memberName, isRefAssemblyOnly: false);
                }
            }
        }

        string decompiledSource;

        if (!string.IsNullOrWhiteSpace(memberName))
        {
            var cleanMemberName = NormalizeMemberName(memberName);

            // Single member decompilation
            var member = typeDefinition.Members.FirstOrDefault(m =>
                string.Equals(m.Name, cleanMemberName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase) ||
                (cleanMemberName == ".ctor" && m.SymbolKind == ICSharpCode.Decompiler.TypeSystem.SymbolKind.Constructor && !m.IsStatic) ||
                (cleanMemberName == ".cctor" && m.SymbolKind == ICSharpCode.Decompiler.TypeSystem.SymbolKind.Constructor && m.IsStatic));

            if (member == null || member.MetadataToken.IsNil)
            {
                return null;
            }

            try
            {
                decompiledSource = decompiler.DecompileAsString(member.MetadataToken).Trim();
            }
            catch
            {
                return null;
            }
        }
        else
        {
            // Decompile entire type
            decompiledSource = decompiler.DecompileTypeAsString(fullTypeName).Trim();
        }

        var asmName = Path.GetFileNameWithoutExtension(assemblyPath);

        return new DecompiledSource(
            Assembly: asmName,
            Type: typeName,
            Member: memberName,
            LanguageVersion: "CSharp10",
            Source: decompiledSource,
            IsReferenceAssemblyOnly: isRefAssemblyOnly);
    }

    private static async Task<string?> TryResolveAssemblyPathAsync(
        RoslynWorkspaceSnapshot? roslynSnapshot,
        Project? preferredProject,
        Compilation? compilation,
        string? targetTypeName,
        string? assemblyName,
        CancellationToken ct)
    {
        string? assemblyPath = null;

        if (compilation != null && !string.IsNullOrWhiteSpace(targetTypeName))
        {
            var cleanTypeName = NormalizeTypeNameForMetadata(targetTypeName);
            var typeSymbol = compilation.GetTypeByMetadataName(cleanTypeName)
                ?? compilation.GetTypeByMetadataName(targetTypeName);

            if (typeSymbol != null)
            {
                assemblyPath = ResolveAssemblyPath(compilation, typeSymbol.ContainingAssembly);
            }
        }

        if (assemblyPath == null && compilation != null && !string.IsNullOrWhiteSpace(assemblyName))
        {
            assemblyPath = ResolveAssemblyPathByName(compilation, assemblyName);
        }

        // Check other projects in the solution if not found in preferred project
        if (assemblyPath == null && roslynSnapshot != null && (!string.IsNullOrWhiteSpace(targetTypeName) || !string.IsNullOrWhiteSpace(assemblyName)))
        {
            foreach (var proj in roslynSnapshot.Solution.Projects)
            {
                if (preferredProject != null && proj.Id == preferredProject.Id) continue;
                var projCompilation = await roslynSnapshot.GetCompilationAsync(proj.Id, ct);
                if (projCompilation == null) continue;

                if (assemblyPath == null && !string.IsNullOrWhiteSpace(targetTypeName))
                {
                    var cleanTypeName = NormalizeTypeNameForMetadata(targetTypeName);
                    var typeSymbol = projCompilation.GetTypeByMetadataName(cleanTypeName)
                        ?? projCompilation.GetTypeByMetadataName(targetTypeName);

                    if (typeSymbol != null)
                    {
                        assemblyPath = ResolveAssemblyPath(projCompilation, typeSymbol.ContainingAssembly);
                    }
                }

                if (assemblyPath == null && !string.IsNullOrWhiteSpace(assemblyName))
                {
                    assemblyPath = ResolveAssemblyPathByName(projCompilation, assemblyName);
                }

                if (assemblyPath != null) break;
            }
        }

        // Fallback: check shared runtime directory for BCL assemblies
        if (assemblyPath == null)
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null)
            {
                if (!string.IsNullOrWhiteSpace(assemblyName))
                {
                    var cand1 = Path.Combine(runtimeDir, assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assemblyName : assemblyName + ".dll");
                    if (File.Exists(cand1)) assemblyPath = cand1;
                }
                if (assemblyPath == null && !string.IsNullOrWhiteSpace(targetTypeName))
                {
                    assemblyPath = ResolveRuntimeAssemblyForType(runtimeDir, NormalizeTypeNameForMetadata(targetTypeName));
                }
            }
        }

        return assemblyPath;
    }

    private static (string TypeName, string? MemberName) SplitTypeAndMember(string fullSym)
    {
        if (fullSym.EndsWith("..ctor", StringComparison.OrdinalIgnoreCase))
        {
            return (fullSym[..^6], ".ctor");
        }
        if (fullSym.EndsWith("..cctor", StringComparison.OrdinalIgnoreCase))
        {
            return (fullSym[..^7], ".cctor");
        }
        if (fullSym.EndsWith(".#ctor", StringComparison.OrdinalIgnoreCase))
        {
            return (fullSym[..^6], ".ctor");
        }
        if (fullSym.EndsWith(".#cctor", StringComparison.OrdinalIgnoreCase))
        {
            return (fullSym[..^7], ".cctor");
        }

        // Find last dot outside of angle brackets <...>
        int depth = 0;
        int lastDot = -1;
        for (int i = 0; i < fullSym.Length; i++)
        {
            char c = fullSym[i];
            if (c == '<') depth++;
            else if (c == '>' && depth > 0) depth--;
            else if (c == '.' && depth == 0) lastDot = i;
        }

        if (lastDot > 0)
        {
            return (fullSym[..lastDot], fullSym[(lastDot + 1)..]);
        }

        return (fullSym, null);
    }

    private static string NormalizeMemberName(string memberName)
    {
        var clean = memberName.Trim();
        var parenIdx = clean.IndexOf('(');
        if (parenIdx >= 0)
        {
            clean = clean[..parenIdx];
        }

        if (clean.EndsWith("..ctor", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".#ctor", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".ctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "#ctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "ctor", StringComparison.OrdinalIgnoreCase))
        {
            return ".ctor";
        }

        if (clean.EndsWith("..cctor", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".#cctor", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".cctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "#cctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "cctor", StringComparison.OrdinalIgnoreCase))
        {
            return ".cctor";
        }

        var backtickIdx = clean.IndexOf('`');
        if (backtickIdx >= 0)
        {
            clean = clean[..backtickIdx];
        }

        var angleIdx = clean.IndexOf('<');
        if (angleIdx >= 0)
        {
            clean = clean[..angleIdx];
        }

        if (clean.Contains('.'))
        {
            clean = clean[(clean.LastIndexOf('.') + 1)..];
        }

        return clean.Trim();
    }

    private static string? ResolveAssemblyPath(Compilation compilation, IAssemblySymbol? assemblySymbol)
    {
        if (assemblySymbol == null) return null;

        var metadataRef = compilation.GetMetadataReference(assemblySymbol) as PortableExecutableReference;
        if (metadataRef?.FilePath != null)
        {
            return metadataRef.FilePath;
        }

        return ResolveAssemblyPathByName(compilation, assemblySymbol.Name);
    }

    private static string? ResolveAssemblyPathByName(Compilation compilation, string assemblyName)
    {
        foreach (var r in compilation.References)
        {
            if (r is PortableExecutableReference peRef && peRef.FilePath != null)
            {
                var fileName = Path.GetFileNameWithoutExtension(peRef.FilePath);
                if (string.Equals(fileName, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return peRef.FilePath;
                }
            }
        }

        return null;
    }

    private static (string Path, bool IsRefOnly) MapToImplementationAssembly(string assemblyPath)
    {
        var normalized = assemblyPath.Replace('\\', '/');

        // Check if path is in reference assembly directory (e.g. packs/Microsoft.NETCore.App.Ref/)
        if (normalized.Contains("/packs/", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains(".Ref/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(assemblyPath);

            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null)
            {
                var candidate = Path.Combine(runtimeDir, fileName);
                if (File.Exists(candidate))
                {
                    return (candidate, false);
                }
            }

            return (assemblyPath, true);
        }

        // Check if path is in a NuGet package ref directory (e.g. /ref/net8.0/ -> /lib/net8.0/)
        if (normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase))
        {
            var libCandidate = normalized.Replace("/ref/", "/lib/", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(libCandidate))
            {
                return (libCandidate, false);
            }
            return (assemblyPath, true);
        }

        return (assemblyPath, false);
    }

    private static string? StripDocIdPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var trimmed = value.Trim().TrimEnd('?');
        if (trimmed.Length > 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]))
        {
            return trimmed[2..].Trim().TrimEnd('?');
        }
        return trimmed;
    }

    private static string? ResolveRuntimeAssemblyForType(string runtimeDir, string cleanTypeName)
    {
        // 1. Try standard BCL / CoreLib type resolution first
        var runtimeType = Type.GetType(cleanTypeName, throwOnError: false, ignoreCase: true);
        if (runtimeType != null && !string.IsNullOrWhiteSpace(runtimeType.Assembly.Location) && File.Exists(runtimeType.Assembly.Location))
        {
            return runtimeType.Assembly.Location;
        }

        // 2. Check loaded assemblies in the current AppDomain
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try
            {
                var loadedType = asm.GetType(cleanTypeName, throwOnError: false, ignoreCase: true);
                if (loadedType != null && !string.IsNullOrWhiteSpace(loadedType.Assembly.Location) && File.Exists(loadedType.Assembly.Location))
                {
                    return loadedType.Assembly.Location;
                }
            }
            catch
            {
                // Ignore assembly inspection errors
            }
        }

        // 3. Check candidate runtime DLLs matching namespace prefixes (longest prefix first)
        var candidateDlls = new List<string>();
        var baseNameWithoutArity = cleanTypeName.Contains('`')
            ? cleanTypeName[..cleanTypeName.IndexOf('`')]
            : cleanTypeName;

        var prefix = baseNameWithoutArity;
        while (!string.IsNullOrWhiteSpace(prefix))
        {
            var candPath = Path.Combine(runtimeDir, prefix + ".dll");
            if (File.Exists(candPath) && !candidateDlls.Contains(candPath, StringComparer.OrdinalIgnoreCase))
            {
                candidateDlls.Add(candPath);
            }

            var lastDot = prefix.LastIndexOf('.');
            if (lastDot <= 0) break;
            prefix = prefix[..lastDot];
        }

        var sysRuntime = Path.Combine(runtimeDir, "System.Runtime.dll");
        if (File.Exists(sysRuntime) && !candidateDlls.Contains(sysRuntime, StringComparer.OrdinalIgnoreCase))
        {
            candidateDlls.Add(sysRuntime);
        }

        var coreLib = Path.Combine(runtimeDir, "System.Private.CoreLib.dll");
        if (File.Exists(coreLib) && !candidateDlls.Contains(coreLib, StringComparer.OrdinalIgnoreCase))
        {
            candidateDlls.Add(coreLib);
        }

        foreach (var candidateDll in candidateDlls)
        {
            if (AssemblyDefinesOrForwardsType(candidateDll, cleanTypeName))
            {
                return candidateDll;
            }
        }

        return null;
    }

    private static bool AssemblyDefinesOrForwardsType(string assemblyPath, string cleanTypeName)
    {
        try
        {
            var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
            {
                ThrowOnAssemblyResolveErrors = false
            };
            var decompiler = new CSharpDecompiler(assemblyPath, settings);
            var fullTypeName = new FullTypeName(cleanTypeName);
            var typeDef = decompiler.TypeSystem.MainModule.Compilation.FindType(fullTypeName).GetDefinition();
            if (typeDef != null) return true;

            return decompiler.TypeSystem.MainModule.TypeDefinitions.Any(t =>
                string.Equals(t.FullName, cleanTypeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, cleanTypeName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeTypeNameForMetadata(string typeName)
    {
        // Convert C# generic syntax like List<T> or Dictionary<TKey, List<TValue>> to List`1 or Dictionary`2
        var openIdx = typeName.IndexOf('<');
        var closeIdx = typeName.LastIndexOf('>');
        if (openIdx < 0 || closeIdx <= openIdx) return typeName;

        var baseName = typeName[..openIdx];
        var genericPart = typeName[(openIdx + 1)..closeIdx];
        int argCount = 1;
        int depth = 0;
        for (int i = 0; i < genericPart.Length; i++)
        {
            char c = genericPart[i];
            if (c == '<') depth++;
            else if (c == '>' && depth > 0) depth--;
            else if (c == ',' && depth == 0) argCount++;
        }

        return $"{baseName}`{argCount}";
    }
}
