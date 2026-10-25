using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Tests.Fakes;

public sealed class FakeApiSearchEngine : IApiSearchEngine
{
    public List<ApiDefinition> SearchResults { get; set; } = new();
    public string IndexState { get; set; } = "ready";

    public Task<IReadOnlyList<ApiDefinition>> SearchAsync(
        IWorkspaceSnapshot snapshot,
        ApiSearchCriteria criteria,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApiDefinition>>(SearchResults);

    public string GetIndexState() => IndexState;
}

public sealed class FakeTypeExplorer : ITypeExplorer
{
    public TypeExplorationResult? ExplorationResult { get; set; }

    public Task<TypeExplorationResult?> ExploreTypeByNameAsync(
        IWorkspaceSnapshot snapshot,
        string typeName,
        ExploreOptions options,
        CancellationToken ct = default) =>
        Task.FromResult(ExplorationResult);

    public Task<TypeExplorationResult?> ExploreTypeAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        ExploreOptions options,
        CancellationToken ct = default) =>
        Task.FromResult(ExplorationResult);
}
