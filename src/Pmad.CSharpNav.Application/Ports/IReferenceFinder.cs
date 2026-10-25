using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record ReferenceSearchOptions(
    bool ExcludeTests = false,
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false,
    bool IncludeOverrides = true,
    bool IncludeImplementations = true);

public sealed record SymbolResolutionResult(
    SymbolIdentifier Target,
    string? DefinedIn = null,
    int? DefinedLine = null);

/// <summary>
/// Port for finding references and usages of symbols across source code.
/// </summary>
public interface IReferenceFinder
{
    Task<SymbolResolutionResult?> ResolveSymbolAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        CancellationToken ct = default);

    Task<IReadOnlyList<SymbolResolutionResult>> ResolveSymbolsByNameAsync(
        IWorkspaceSnapshot snapshot,
        string symbolName,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReferenceLocation>> FindReferencesAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        ReferenceSearchOptions options,
        CancellationToken ct = default);
}
