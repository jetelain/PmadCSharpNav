using System.Collections.Concurrent;
using Pmad.CSharpNav.Application.Ports;

namespace Pmad.CSharpNav.Infrastructure.Watcher;

/// <summary>
/// Monitors workspace directories for source and project file modifications,
/// debounces events with a 150ms window, and synchronizes the Roslyn workspace.
/// </summary>
public sealed class DebouncedFileWatcher : IDisposable
{
    private readonly string _rootDirectory;
    private readonly ISolutionWorkspace _workspace;
    private readonly int _debounceMilliseconds;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _pendingSourceChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _timerLock = new();
    private Timer? _debounceTimer;
    private volatile bool _pendingProjectReload;
    private bool _isDisposed;

    public DebouncedFileWatcher(
        string rootDirectory,
        ISolutionWorkspace workspace,
        int debounceMilliseconds = 150)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _workspace = workspace;
        _debounceMilliseconds = debounceMilliseconds;

        _watcher = new FileSystemWatcher(_rootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemRenamed;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        HandlePath(e.FullPath);
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        HandlePath(e.OldFullPath);
        HandlePath(e.FullPath);
    }

    private void HandlePath(string fullPath)
    {
        if (ShouldIgnore(fullPath)) return;

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var fileName = Path.GetFileName(fullPath);

        if (IsProjectOrSolutionFile(fileName, ext))
        {
            _pendingProjectReload = true;
            ScheduleFlush();
        }
        else if (IsSourceFile(ext))
        {
            _pendingSourceChanges[fullPath] = 1;
            ScheduleFlush();
        }
    }

    private void ScheduleFlush()
    {
        lock (_timerLock)
        {
            if (_isDisposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, _debounceMilliseconds, Timeout.Infinite);
        }
    }

    private async void OnDebounceElapsed(object? state)
    {
        try
        {
            if (_pendingProjectReload)
            {
                _pendingProjectReload = false;
                _pendingSourceChanges.Clear();
                await _workspace.ReloadSolutionAsync(CancellationToken.None);
                return;
            }

            var filesToSync = _pendingSourceChanges.Keys.ToList();
            _pendingSourceChanges.Clear();

            foreach (var file in filesToSync)
            {
                await _workspace.SyncDocumentAsync(file, content: null, CancellationToken.None);
            }
        }
        catch
        {
            // Background sync errors are logged or swallowed to keep the daemon resilient
        }
    }

    public static bool ShouldIgnore(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');

        // Ignore bin and obj directories
        if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/obj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Ignore hidden directories like .git, .vs, .idea
        if (normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.vs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.idea/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsProjectOrSolutionFile(string fileName, string extension)
    {
        if (extension is ".csproj" or ".sln" or ".slnx") return true;

        if (fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsSourceFile(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".razor", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _watcher.Dispose();
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
        }
    }
}
