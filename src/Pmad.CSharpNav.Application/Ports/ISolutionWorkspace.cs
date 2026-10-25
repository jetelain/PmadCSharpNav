using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Ports;

/// <summary>
/// Port for managing workspace lifecycle, document updates, and snapshot vending.
/// </summary>
public interface ISolutionWorkspace
{
    string State { get; }
    Task<IWorkspaceSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default);
    Task<IWorkspaceSnapshot> SyncDocumentAsync(string filePath, string? content = null, CancellationToken ct = default);
    Task ReloadSolutionAsync(CancellationToken ct = default);
    Task<WorkspaceStatusResponse> GetStatusAsync(CancellationToken ct = default);
}
