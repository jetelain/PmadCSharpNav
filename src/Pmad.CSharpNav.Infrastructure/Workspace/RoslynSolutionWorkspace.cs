using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Infrastructure.Workspace;

/// <summary>
/// Manages the in-memory Roslyn MSBuild workspace, incremental document synchronization,
/// and atomic snapshot swapping.
/// </summary>
public sealed class RoslynSolutionWorkspace : ISolutionWorkspace, IDisposable
{
    private readonly string _targetPath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly TaskCompletionSource _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string _state = "starting";
    private MSBuildWorkspace? _workspace;
    private RoslynWorkspaceSnapshot? _currentSnapshot;
    private long _snapshotVersion;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _isDisposed;

    public string State => _state;

    public RoslynSolutionWorkspace(string targetPath)
    {
        RoslynLocator.EnsureRegistered();
        _targetPath = Path.GetFullPath(targetPath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _state = "loading";
        RoslynLocator.EnsureRegistered();
        try
        {
            await LoadSolutionInternalAsync(ct);
            _state = "ready";
            _initTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _state = "unhealthy";
            _initTcs.TrySetException(ex);
            throw;
        }
    }

    public async Task<IWorkspaceSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot != null)
        {
            return snapshot;
        }

        // Workspace still initializing: wait up to bounded timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

        try
        {
            await _initTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Pmad.CSharpNav.Application.Exceptions.NavException(
                "WORKSPACE_LOADING",
                "Workspace is currently loading. Please retry shortly.");
        }

        snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot == null)
        {
            throw new Pmad.CSharpNav.Application.Exceptions.NavException(
                "WORKSPACE_LOADING",
                "Workspace is currently loading. Please retry shortly.");
        }

        return snapshot;
    }

    public async Task<IWorkspaceSnapshot> SyncDocumentAsync(string filePath, string? content = null, CancellationToken ct = default)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            var current = Volatile.Read(ref _currentSnapshot)
                ?? throw new InvalidOperationException("Workspace has not been initialized.");

            var fullPath = Path.GetFullPath(filePath);
            var text = content != null
                ? SourceText.From(content)
                : (File.Exists(fullPath) ? SourceText.From(await File.ReadAllTextAsync(fullPath, ct)) : null);

            var docIds = current.GetDocumentIds(fullPath);
            Solution newSolution = current.Solution;

            if (docIds.Count > 0)
            {
                foreach (var docId in docIds)
                {
                    if (text == null)
                    {
                        // File deleted
                        newSolution = newSolution.RemoveDocument(docId);
                    }
                    else
                    {
                        // File modified
                        newSolution = newSolution.WithDocumentText(docId, text);
                    }
                }
            }
            else
            {
                var additionalDocIds = current.GetAdditionalDocumentIds(fullPath);
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var isRazorOrCshtml = ext is ".razor" or ".cshtml";

                if (additionalDocIds.Count > 0)
                {
                    foreach (var addId in additionalDocIds)
                    {
                        if (text == null)
                        {
                            newSolution = newSolution.RemoveAdditionalDocument(addId);
                        }
                        else
                        {
                            newSolution = newSolution.WithAdditionalDocumentText(addId, text);
                        }
                    }
                }
                else if (isRazorOrCshtml)
                {
                    if (text == null)
                    {
                        // Non-existent file deletion, no-op
                        return current;
                    }

                    var targetProject = FindOwningProject(current.Solution, fullPath)
                        ?? current.Solution.Projects.FirstOrDefault();

                    if (targetProject == null)
                    {
                        return current;
                    }

                    var newDocId = DocumentId.CreateNewId(targetProject.Id);
                    newSolution = newSolution.AddAdditionalDocument(newDocId, Path.GetFileName(fullPath), text, filePath: fullPath);
                }
                else
                {
                    if (text == null)
                    {
                        // Non-existent file deletion, no-op
                        return current;
                    }

                    // File added: find owning project by matching directory hierarchy
                    var targetProject = FindOwningProject(current.Solution, fullPath)
                        ?? current.Solution.Projects.FirstOrDefault();

                    if (targetProject == null)
                    {
                        return current;
                    }

                    var newDocId = DocumentId.CreateNewId(targetProject.Id);
                    newSolution = newSolution.AddDocument(newDocId, Path.GetFileName(fullPath), text, filePath: fullPath);
                }
            }

            var newVersion = Interlocked.Increment(ref _snapshotVersion);
            var newSnapshot = new RoslynWorkspaceSnapshot(newSolution, _targetPath, newVersion, isStale: current.IsStale);

            Volatile.Write(ref _currentSnapshot, newSnapshot);
            return newSnapshot;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task ReloadSolutionAsync(CancellationToken ct = default)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            _state = "reloading";
            // Mark current snapshot as stale while reloading in background
            var current = Volatile.Read(ref _currentSnapshot);
            if (current != null)
            {
                var staleSnapshot = new RoslynWorkspaceSnapshot(
                    current.Solution,
                    current.SolutionPath,
                    current.SnapshotVersion,
                    isStale: true);
                Volatile.Write(ref _currentSnapshot, staleSnapshot);
            }

            await LoadSolutionInternalAsync(ct);
            _state = "ready";
        }
        catch
        {
            // Restore ready state and non-stale snapshot if reload fails
            var current = Volatile.Read(ref _currentSnapshot);
            if (current != null)
            {
                var restored = new RoslynWorkspaceSnapshot(
                    current.Solution,
                    current.SolutionPath,
                    current.SnapshotVersion,
                    isStale: false);
                Volatile.Write(ref _currentSnapshot, restored);
            }
            _state = "ready";
            throw;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public Task<WorkspaceStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var solutionName = Path.GetFileNameWithoutExtension(_targetPath);
        var projectCount = snapshot?.Solution.ProjectIds.Count ?? 0;
        var version = snapshot?.SnapshotVersion ?? 0;
        var memoryBytes = GC.GetTotalMemory(forceFullCollection: false);

        var status = new WorkspaceStatusResponse(
            SolutionName: solutionName,
            SolutionPath: _targetPath,
            ProjectCount: projectCount,
            MemoryUsageBytes: memoryBytes,
            CacheStatus: "Ready",
            SnapshotVersion: version,
            StartedAt: _startedAt,
            State: snapshot == null ? "loading" : (snapshot.IsStale ? "reloading" : "ready"));

        return Task.FromResult(status);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task LoadSolutionInternalAsync(CancellationToken ct)
    {
        var oldWorkspace = _workspace;
        var newWorkspace = MSBuildWorkspace.Create();

        Solution solution;
        var ext = Path.GetExtension(_targetPath).ToLowerInvariant();

        if (ext is ".sln" or ".slnx")
        {
            solution = await newWorkspace.OpenSolutionAsync(_targetPath, cancellationToken: ct);
        }
        else if (ext is ".csproj")
        {
            var project = await newWorkspace.OpenProjectAsync(_targetPath, cancellationToken: ct);
            solution = project.Solution;
        }
        else
        {
            throw new NotSupportedException($"Target path extension '{ext}' is not supported. Use .sln, .slnx, or .csproj.");
        }
        solution = AttachXmlDocumentationProviders(solution);

        _workspace = newWorkspace;

        var newVersion = Interlocked.Increment(ref _snapshotVersion);
        var newSnapshot = new RoslynWorkspaceSnapshot(solution, _targetPath, newVersion, isStale: false);
        Volatile.Write(ref _currentSnapshot, newSnapshot);

        oldWorkspace?.Dispose();
    }

    private static readonly ConcurrentDictionary<string, XmlDocumentationProvider> _docProviderCache = new(StringComparer.OrdinalIgnoreCase);

    public static Solution AttachXmlDocumentationProviders(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            var newReferences = new List<MetadataReference>();
            var modified = false;

            foreach (var reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference peRef &&
                    !string.IsNullOrWhiteSpace(peRef.FilePath))
                {
                    var docProvider = GetDocumentationProvider(peRef.FilePath);
                    if (docProvider != null)
                    {
                        var newRef = MetadataReference.CreateFromFile(peRef.FilePath, peRef.Properties, docProvider);
                        newReferences.Add(newRef);
                        modified = true;
                        continue;
                    }
                }

                newReferences.Add(reference);
            }

            if (modified)
            {
                solution = solution.WithProjectMetadataReferences(project.Id, newReferences);
            }
        }

        return solution;
    }

    private static XmlDocumentationProvider? GetDocumentationProvider(string dllPath)
    {
        try
        {
            var xmlPath = Path.ChangeExtension(dllPath, ".xml");
            if (!File.Exists(xmlPath))
            {
                var dir = Path.GetDirectoryName(dllPath);
                if (dir != null)
                {
                    var enXml = Path.Combine(dir, "en", Path.GetFileNameWithoutExtension(dllPath) + ".xml");
                    if (File.Exists(enXml))
                    {
                        xmlPath = enXml;
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            return _docProviderCache.GetOrAdd(xmlPath, p => XmlDocumentationProvider.CreateFromFile(p));
        }
        catch
        {
            return null;
        }
    }

    private static Project? FindOwningProject(Solution solution, string filePath)
    {
        var normalizedFileDir = RoslynWorkspaceSnapshot.NormalizePath(Path.GetDirectoryName(filePath) ?? "");

        return solution.Projects
            .Where(p => p.FilePath != null)
            .OrderByDescending(p =>
            {
                var pDir = RoslynWorkspaceSnapshot.NormalizePath(Path.GetDirectoryName(p.FilePath) ?? "");
                return normalizedFileDir.StartsWith(pDir, StringComparison.OrdinalIgnoreCase) ? pDir.Length : -1;
            })
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _workspace?.Dispose();
        _updateLock.Dispose();
    }
}
