using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Pmad.CSharpNav.Application.Ports;

namespace Pmad.CSharpNav.Infrastructure.Workspace;

/// <summary>
/// Immutable point-in-time snapshot of a Roslyn solution compilation state.
/// </summary>
public sealed class RoslynWorkspaceSnapshot : IWorkspaceSnapshot
{
    private readonly ConcurrentDictionary<ProjectId, Task<Compilation?>> _compilationCache = new();

    public long SnapshotVersion { get; }
    public string SolutionPath { get; }
    public bool IsStale { get; }
    public Solution Solution { get; }

    public RoslynWorkspaceSnapshot(
        Solution solution,
        string solutionPath,
        long snapshotVersion,
        bool isStale = false)
    {
        Solution = solution;
        SolutionPath = solutionPath;
        SnapshotVersion = snapshotVersion;
        IsStale = isStale;
    }

    public Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken ct = default)
    {
        return _compilationCache.GetOrAdd(
            projectId,
            id =>
            {
                var project = Solution.GetProject(id);
                return project != null
                    ? project.GetCompilationAsync(ct)
                    : Task.FromResult<Compilation?>(null);
            });
    }

    public DocumentId? FindDocumentId(string filePath)
    {
        var normalized = NormalizePath(filePath);

        foreach (var project in Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null && string.Equals(NormalizePath(doc.FilePath), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return doc.Id;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<DocumentId> GetDocumentIds(string filePath)
    {
        var normalized = NormalizePath(filePath);
        var list = new List<DocumentId>();

        foreach (var project in Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null && string.Equals(NormalizePath(doc.FilePath), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(doc.Id);
                }
            }
        }

        return list;
    }

    public Project? GetPreferredProject(string? projectName = null, string? targetFramework = null)
    {
        var projects = Solution.Projects.ToList();

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var matching = projects.Where(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.FilePath, projectName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matching.Count == 1) return matching[0];

            if (matching.Count > 1 && !string.IsNullOrWhiteSpace(targetFramework))
            {
                var tfmMatch = matching.FirstOrDefault(p =>
                    p.Name.Contains(targetFramework, StringComparison.OrdinalIgnoreCase) ||
                    (p.OutputFilePath != null && p.OutputFilePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase)));
                if (tfmMatch != null) return tfmMatch;
            }

            return matching.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            var tfmMatch = projects.FirstOrDefault(p =>
                p.Name.Contains(targetFramework, StringComparison.OrdinalIgnoreCase) ||
                (p.OutputFilePath != null && p.OutputFilePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase)));
            if (tfmMatch != null) return tfmMatch;
        }

        return projects.FirstOrDefault();
    }

    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
