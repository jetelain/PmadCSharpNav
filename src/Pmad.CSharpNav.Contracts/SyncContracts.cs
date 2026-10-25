namespace Pmad.CSharpNav.Contracts;

public sealed record SyncRequest(
    string? FilePath = null,
    string? Content = null);

public sealed record SyncResponse(
    IReadOnlyList<string> SyncedFiles,
    long SnapshotVersion);
