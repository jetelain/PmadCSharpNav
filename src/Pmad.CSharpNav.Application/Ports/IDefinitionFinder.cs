using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record DefinitionSearchOptions(
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false);

/// <summary>
/// Port for finding definition locations of symbols across source code and metadata.
/// </summary>
public interface IDefinitionFinder
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

    Task<IReadOnlyList<DefinitionLocation>> FindDefinitionsAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        DefinitionSearchOptions options,
        CancellationToken ct = default);
}
