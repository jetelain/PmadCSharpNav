using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class GetWorkspaceStatusHandler
{
    private readonly ISolutionWorkspace _workspace;

    public GetWorkspaceStatusHandler(ISolutionWorkspace workspace)
    {
        _workspace = workspace;
    }

    public Task<WorkspaceStatusResponse> HandleAsync(CancellationToken ct = default) =>
        _workspace.GetStatusAsync(ct);
}

public sealed class SyncDocumentHandler
{
    private readonly ISolutionWorkspace _workspace;

    public SyncDocumentHandler(ISolutionWorkspace workspace)
    {
        _workspace = workspace;
    }

    public async Task<SyncResponse> HandleAsync(SyncRequest request, CancellationToken ct = default)
    {
        var syncedFiles = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            var snapshot = await _workspace.SyncDocumentAsync(request.FilePath, request.Content, ct);
            syncedFiles.Add(request.FilePath);
            return new SyncResponse(syncedFiles, snapshot.SnapshotVersion);
        }

        var currentSnapshot = await _workspace.GetCurrentSnapshotAsync(ct);
        return new SyncResponse(syncedFiles, currentSnapshot.SnapshotVersion);
    }
}
