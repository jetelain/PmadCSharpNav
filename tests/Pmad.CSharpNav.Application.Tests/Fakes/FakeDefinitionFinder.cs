using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Tests.Fakes;

public sealed class FakeDefinitionFinder : IDefinitionFinder
{
    public SymbolResolutionResult? LocationResolutionResult { get; set; }
    public List<SymbolResolutionResult> NameResolutionResults { get; set; } = new();
    public List<DefinitionLocation> DefinitionsToReturn { get; set; } = new();

    public Task<SymbolResolutionResult?> ResolveSymbolAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        CancellationToken ct = default) =>
        Task.FromResult(LocationResolutionResult);

    public Task<IReadOnlyList<SymbolResolutionResult>> ResolveSymbolsByNameAsync(
        IWorkspaceSnapshot snapshot,
        string symbolName,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SymbolResolutionResult>>(NameResolutionResults);

    public Task<IReadOnlyList<DefinitionLocation>> FindDefinitionsAsync(
        IWorkspaceSnapshot snapshot,
        SymbolIdentifier symbol,
        DefinitionSearchOptions options,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DefinitionLocation>>(DefinitionsToReturn);
}
