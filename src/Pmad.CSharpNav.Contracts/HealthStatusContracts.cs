namespace Pmad.CSharpNav.Contracts;

public sealed record HealthResponse(
    string Status, // "healthy" | "unhealthy"
    string State,  // "starting" | "loading" | "ready"
    int Version = NavConstants.CurrentContractVersion,
    string? SolutionPath = null);

public sealed record WorkspaceStatusResponse(
    string SolutionName,
    string SolutionPath,
    int ProjectCount,
    long MemoryUsageBytes,
    string CacheStatus,
    long SnapshotVersion,
    DateTimeOffset StartedAt,
    string State = "ready");
