using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record ApiSearchCriteria(
    string? Pattern = null,
    string? DocText = null,
    string Scope = "all", // "all", "source", "dependencies", or assembly name
    string? Kind = null,
    string Access = "all",
    string? Framework = null,
    string? Project = null,
    string? Namespace = null,
    string? Assembly = null,
    bool IncludeObsolete = false,
    string? InheritsFrom = null,
    string? ReturnsType = null,
    IReadOnlyList<string>? Patterns = null);

/// <summary>
/// Port for searching APIs across solution sources and referenced assemblies.
/// </summary>
public interface IApiSearchEngine
{
    Task<IReadOnlyList<ApiDefinition>> SearchAsync(
        IWorkspaceSnapshot snapshot,
        ApiSearchCriteria criteria,
        CancellationToken ct = default);

    string GetIndexState();
}
