using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Client;

public sealed class NavDaemonClient : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private bool _isDisposed;

    public static string ResolveSolutionPath(string? specifiedPath)
    {
        if (!string.IsNullOrWhiteSpace(specifiedPath))
        {
            var fullPath = Path.GetFullPath(specifiedPath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                if (Directory.Exists(fullPath))
                {
                    var found = FindSolutionInDir(fullPath);
                    if (found != null) return found;
                }
                return fullPath;
            }
            throw new FileNotFoundException($"Specified solution or project file not found: {specifiedPath}");
        }

        // Walk up from current working directory
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var found = FindSolutionInDir(current.FullName);
            if (found != null) return found;
            current = current.Parent;
        }

        throw new FileNotFoundException("No .slnx, .sln, or .csproj found in current or parent directories.");
    }

    private static string? FindSolutionInDir(string dir)
    {
        return Directory.GetFiles(dir, "*.slnx").FirstOrDefault()
            ?? Directory.GetFiles(dir, "*.sln").FirstOrDefault()
            ?? Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
    }

    public async Task<DiscoveryInfo> EnsureDaemonRunningAsync(string solutionPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);

        // Fast path: if daemon is already running and healthy, avoid taking the lock
        var existing = await NavDiscovery.ReadDiscoveryFileAsync(fullPath, ct);
        if (existing != null &&
            existing.Version == NavConstants.CurrentContractVersion &&
            IsProcessAlive(existing.Pid) &&
            await PingHealthAsync(existing, ct))
        {
            return existing;
        }

        // Slow path: acquire cross-process solution lock
        using var fileLock = await AcquireSolutionLockAsync(fullPath, TimeSpan.FromSeconds(15), ct);

        // Re-check after acquiring the lock in case another process already started the daemon
        var info = await NavDiscovery.ReadDiscoveryFileAsync(fullPath, ct);
        if (info != null)
        {
            if (info.Version != NavConstants.CurrentContractVersion)
            {
                await Console.Error.WriteLineAsync($"[csnav] Daemon contract version mismatch (expected {NavConstants.CurrentContractVersion}, found {info.Version}). Restarting daemon...");
                await StopDaemonAsync(fullPath, ct);
                info = null;
            }
            else if (IsProcessAlive(info.Pid) && await PingHealthAsync(info, ct))
            {
                return info;
            }
        }

        // Stale or dead daemon, clean up and start a new one
        NavDiscovery.DeleteDiscoveryFile(fullPath);

        await Console.Error.WriteLineAsync($"[csnav] Starting background daemon for {Path.GetFileName(fullPath)}...");
        StartBackgroundServer(fullPath);

        // Poll health endpoint
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(250, ct);

            info = await NavDiscovery.ReadDiscoveryFileAsync(fullPath, ct);
            if (info != null && await PingHealthAsync(info, ct))
            {
                await Console.Error.WriteLineAsync($"[csnav] Daemon ready on port {info.Port}.");
                return info;
            }
        }

        throw new TimeoutException("Failed to communicate with csnav daemon within 20 seconds.");
    }

    private static async Task<IDisposable> AcquireSolutionLockAsync(string solutionPath, TimeSpan timeout, CancellationToken ct)
    {
        var lockFilePath = NavDiscovery.GetLockFilePath(solutionPath);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                // File is locked by another process
            }
            catch (UnauthorizedAccessException)
            {
                // File sharing conflict or lock transition
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Failed to acquire daemon startup lock for {solutionPath} within {timeout.TotalSeconds} seconds.");
            }

            await Task.Delay(50, ct);
        }
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string solutionPath,
        string endpoint,
        TRequest request,
        CancellationToken ct = default)
    {
        var discovery = await EnsureDaemonRunningAsync(solutionPath, ct);
        var url = $"http://127.0.0.1:{discovery.Port}{endpoint}";

        var requestJson = NavJsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = NavJsonSerializer.Deserialize<ErrorResponse>(responseContent);
            if (err != null)
            {
                throw new NavClientException(err.Code, err.Message, err.Candidates);
            }
            throw new HttpRequestException($"Server responded with {(int)response.StatusCode}: {responseContent}");
        }

        var result = NavJsonSerializer.Deserialize<TResponse>(responseContent);
        if (result == null)
        {
            throw new InvalidOperationException("Failed to deserialize server response.");
        }

        return result;
    }

    public async Task<TResponse> GetAsync<TResponse>(
        string solutionPath,
        string endpoint,
        CancellationToken ct = default)
    {
        var discovery = await EnsureDaemonRunningAsync(solutionPath, ct);
        var url = $"http://127.0.0.1:{discovery.Port}{endpoint}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = NavJsonSerializer.Deserialize<ErrorResponse>(responseContent);
            if (err != null)
            {
                throw new NavClientException(err.Code, err.Message, err.Candidates);
            }
            throw new HttpRequestException($"Server responded with {(int)response.StatusCode}: {responseContent}");
        }

        var result = NavJsonSerializer.Deserialize<TResponse>(responseContent);
        if (result == null)
        {
            throw new InvalidOperationException("Failed to deserialize server response.");
        }

        return result;
    }

    public async Task<DiscoveryInfo?> GetRunningDiscoveryAsync(string solutionPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var info = await NavDiscovery.ReadDiscoveryFileAsync(fullPath, ct);
        if (info != null && IsProcessAlive(info.Pid) && await PingHealthAsync(info, ct))
        {
            return info;
        }
        return null;
    }

    public async Task<bool> StopDaemonAsync(string solutionPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var info = await GetRunningDiscoveryAsync(fullPath, ct);
        if (info == null)
        {
            NavDiscovery.DeleteDiscoveryFile(fullPath);
            return false;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{info.Port}/api/shutdown");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", info.Token);
            await _httpClient.SendAsync(httpRequest, ct);
        }
        catch
        {
            // Ignore error if process shuts down abruptly
        }

        NavDiscovery.DeleteDiscoveryFile(fullPath);
        return true;
    }

    public async Task ReloadSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        var discovery = await EnsureDaemonRunningAsync(solutionPath, ct);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{discovery.Port}/api/reload");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);
        using var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> PingHealthAsync(DiscoveryInfo info, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{info.Port}/api/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", info.Token);

            using var res = await _httpClient.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void StartBackgroundServer(string solutionPath)
    {
        var serverAssembly = FindServerAssembly();
        var dotnetHost = FindDotNetHost();
        var arguments = $"\"{serverAssembly}\" --solution \"{solutionPath}\"";

        if (OperatingSystem.IsWindows())
        {
            var commandLine = $"\"{dotnetHost}\" {arguments}";
            var workingDir = Path.GetDirectoryName(solutionPath);
            if (WindowsProcessLauncher.TryStartViaWmi(commandLine, workingDir))
            {
                return;
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = dotnetHost,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        Process.Start(psi);
    }

    private static string FindDotNetHost()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var defaultLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(defaultLocation)) return defaultLocation;
        }

        return "dotnet";
    }

    private static string FindServerAssembly()
    {
        var baseDir = AppContext.BaseDirectory;

        // Check adjacent server assembly in output folder
        var candidate1 = Path.Combine(baseDir, "Pmad.CSharpNav.Server.dll");
        if (File.Exists(candidate1)) return candidate1;

        // Check relative build directory
        var candidate2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Pmad.CSharpNav.Server", "bin", "Debug", "net10.0", "Pmad.CSharpNav.Server.dll"));
        if (File.Exists(candidate2)) return candidate2;

        return candidate1;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _httpClient.Dispose();
    }
}

public sealed class NavClientException : Exception
{
    public string Code { get; }
    public IReadOnlyList<DisambiguationCandidateDto>? Candidates { get; }

    public NavClientException(string code, string message, IReadOnlyList<DisambiguationCandidateDto>? candidates = null)
        : base(message)
    {
        Code = code;
        Candidates = candidates;
    }
}
