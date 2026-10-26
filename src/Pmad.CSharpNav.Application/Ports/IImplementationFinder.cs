using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record ImplementationSearchOptions(
    bool ExcludeTests = false,
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false);

/// <summary>
/// Port for finding implementations and overrides of types and members.
/// </summary>
public interface IImplementationFinder
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

    Task<IReadOnlyList<ImplementationLocation>> FindImplementationsAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        ImplementationSearchOptions options,
        CancellationToken ct = default);
}
