namespace Pmad.CSharpNav.Application.Ports;

/// <summary>
/// Represents an immutable, point-in-time snapshot of the solution compilation state.
/// Pure application interface with no dependency on Roslyn types.
/// </summary>
public interface IWorkspaceSnapshot
{
    long SnapshotVersion { get; }
    string SolutionPath { get; }
    bool IsStale { get; }
}
