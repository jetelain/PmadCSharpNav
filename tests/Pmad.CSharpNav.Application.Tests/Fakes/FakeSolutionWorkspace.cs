using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Tests.Fakes;

public sealed class FakeWorkspaceSnapshot : IWorkspaceSnapshot
{
    public long SnapshotVersion { get; set; } = 1;
    public string SolutionPath { get; set; } = "C:/fake/solution.sln";
    public bool IsStale { get; set; }
}

public sealed class FakeSolutionWorkspace : ISolutionWorkspace
{
    public string State { get; set; } = "ready";
    public IWorkspaceSnapshot Snapshot { get; set; } = new FakeWorkspaceSnapshot();

    public Task<IWorkspaceSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult(Snapshot);

    public Task<IWorkspaceSnapshot> SyncDocumentAsync(string filePath, string? content = null, CancellationToken ct = default) =>
        Task.FromResult(Snapshot);

    public Task ReloadSolutionAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<WorkspaceStatusResponse> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new WorkspaceStatusResponse(
            SolutionName: "FakeSolution",
            SolutionPath: Snapshot.SolutionPath,
            ProjectCount: 1,
            MemoryUsageBytes: 1024,
            CacheStatus: "Ready",
            SnapshotVersion: Snapshot.SnapshotVersion,
            StartedAt: DateTimeOffset.UtcNow));
}
