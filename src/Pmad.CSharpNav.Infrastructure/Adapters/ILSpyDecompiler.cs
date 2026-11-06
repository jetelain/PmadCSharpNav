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

        // If queried by source code position: file, line, column
        if (!string.IsNullOrWhiteSpace(criteria.File) && criteria.Line.HasValue && criteria.Column.HasValue)
        {
            if (roslynSnapshot == null) return null;
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
                    var pos = textLine.Start + Math.Clamp(criteria.Column.Value - 1, 0, textLine.Span.Length);
                    var semModel = await doc.GetSemanticModelAsync(ct);
                    var root = await doc.GetSyntaxRootAsync(ct);
                    var token = root?.FindToken(pos);
                    var symbol = semModel?.GetSymbolInfo(token?.Parent ?? root!, ct).Symbol;

                    if (symbol != null)
                    {
                        var containingType = symbol.ContainingType ?? (symbol as INamedTypeSymbol);
                        if (containingType != null)
                        {
                            targetTypeName = containingType.ToDisplayString();
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
        else if (!string.IsNullOrWhiteSpace(criteria.Symbol))
        {
            // Parse FQN symbol or Doc-ID like T:System.Uri, M:System.Uri.IsLoopback, or System.Collections.Generic.List<T>.Add
            var fullSym = criteria.Symbol.Trim();
            char? docPrefix = null;
            if (fullSym.Length > 2 && fullSym[1] == ':' && char.IsLetter(fullSym[0]))
            {
                docPrefix = char.ToUpperInvariant(fullSym[0]);
                fullSym = fullSym[2..];
            }

            var parenIdx = fullSym.IndexOf('(');
            if (parenIdx >= 0)
            {
                fullSym = fullSym[..parenIdx];
            }

            if (docPrefix == 'T')
            {
                targetTypeName = fullSym;
            }
            else
            {
                var lastDot = fullSym.LastIndexOf('.');
                if (lastDot > 0)
                {
                    targetTypeName = fullSym[..lastDot];
                    targetMemberName = fullSym[(lastDot + 1)..];
                }
                else
                {
                    targetTypeName = fullSym;
                }
            }
        }

        if (assemblyPath == null && compilation != null && !string.IsNullOrWhiteSpace(targetTypeName))
        {
            var cleanTypeName = NormalizeTypeNameForMetadata(targetTypeName);
            var typeSymbol = compilation.GetTypeByMetadataName(cleanTypeName)
                ?? compilation.GetTypeByMetadataName(targetTypeName);

            if (typeSymbol != null)
            {
                assemblyPath = ResolveAssemblyPath(compilation, typeSymbol.ContainingAssembly);
            }
        }

        if (assemblyPath == null && compilation != null && !string.IsNullOrWhiteSpace(criteria.Assembly))
        {
            assemblyPath = ResolveAssemblyPathByName(compilation, criteria.Assembly);
        }

        // Check other projects in the solution if not found in preferred project
        if (assemblyPath == null && roslynSnapshot != null && (!string.IsNullOrWhiteSpace(targetTypeName) || !string.IsNullOrWhiteSpace(criteria.Assembly)))
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

                if (assemblyPath == null && !string.IsNullOrWhiteSpace(criteria.Assembly))
                {
                    assemblyPath = ResolveAssemblyPathByName(projCompilation, criteria.Assembly);
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
                if (!string.IsNullOrWhiteSpace(criteria.Assembly))
                {
                    var cand1 = Path.Combine(runtimeDir, criteria.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? criteria.Assembly : criteria.Assembly + ".dll");
                    if (File.Exists(cand1)) assemblyPath = cand1;
                }
                if (assemblyPath == null && !string.IsNullOrWhiteSpace(targetTypeName))
                {
                    assemblyPath = ResolveRuntimeAssemblyForType(runtimeDir, NormalizeTypeNameForMetadata(targetTypeName));
                }
                if (assemblyPath == null)
                {
                    var coreLib = Path.Combine(runtimeDir, "System.Private.CoreLib.dll");
                    if (File.Exists(coreLib)) assemblyPath = coreLib;
                }
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

        return DecompileInternal(finalAssemblyPath, targetTypeName, targetMemberName, isRefAssemblyOnly);
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
            var cleanMemberName = memberName;
            var parenIdx = cleanMemberName.IndexOf('(');
            if (parenIdx >= 0)
            {
                cleanMemberName = cleanMemberName[..parenIdx];
            }
            if (cleanMemberName.Contains('.'))
            {
                cleanMemberName = cleanMemberName[(cleanMemberName.LastIndexOf('.') + 1)..];
            }
            cleanMemberName = cleanMemberName.Trim();

            // Single member decompilation
            var member = typeDefinition.Members.FirstOrDefault(m =>
                string.Equals(m.Name, cleanMemberName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));

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
        var trimmed = value.Trim();
        if (trimmed.Length > 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]))
        {
            return trimmed[2..];
        }
        return trimmed;
    }

    private static string? ResolveRuntimeAssemblyForType(string runtimeDir, string cleanTypeName)
    {
        // Try loaded or standard BCL type resolution first
        var runtimeType = Type.GetType(cleanTypeName, throwOnError: false, ignoreCase: true);
        if (runtimeType != null && !string.IsNullOrWhiteSpace(runtimeType.Assembly.Location) && File.Exists(runtimeType.Assembly.Location))
        {
            return runtimeType.Assembly.Location;
        }

        // Try System.Runtime.dll which holds type forwarders for core BCL types (like System.Uri -> System.Private.Uri)
        var sysRuntime = Path.Combine(runtimeDir, "System.Runtime.dll");
        if (File.Exists(sysRuntime))
        {
            return sysRuntime;
        }

        return null;
    }

    private static string NormalizeTypeNameForMetadata(string typeName)
    {
        // Convert C# generic syntax like List<T> or Dictionary<TKey, TValue> to List`1 or Dictionary`2
        if (!typeName.Contains('<')) return typeName;

        var baseName = typeName[..typeName.IndexOf('<', StringComparison.Ordinal)];
        var genericPart = typeName[(typeName.IndexOf('<', StringComparison.Ordinal) + 1)..typeName.LastIndexOf('>')];
        var argCount = genericPart.Count(c => c == ',') + 1;

        return $"{baseName}`{argCount}";
    }
}
