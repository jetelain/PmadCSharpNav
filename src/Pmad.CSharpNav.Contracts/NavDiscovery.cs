using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pmad.CSharpNav.Contracts;

public sealed record DiscoveryInfo(
    int Version,
    int Pid,
    int Port,
    string Token,
    string SolutionPath,
    DateTimeOffset StartedAt);

public static class NavDiscovery
{
    public static string ComputeSolutionHash(string solutionPath)
    {
        var normalized = Path.GetFullPath(solutionPath).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string GetServersDirectory()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PmadCSharpNav");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = Path.Combine(home, ".local", "state", "PmadCSharpNav");
        }

        var serversDir = Path.Combine(baseDir, "servers");
        Directory.CreateDirectory(serversDir);
        return serversDir;
    }

    public static string GetLogsDirectory()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PmadCSharpNav");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = Path.Combine(home, ".local", "state", "PmadCSharpNav");
        }

        var logsDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logsDir);
        return logsDir;
    }

    public static string GetDiscoveryFilePath(string solutionPath)
    {
        var hash = ComputeSolutionHash(solutionPath);
        return Path.Combine(GetServersDirectory(), $"{hash}.json");
    }

    public static string GetLogFilePath(string solutionPath)
    {
        var hash = ComputeSolutionHash(solutionPath);
        return Path.Combine(GetLogsDirectory(), $"{hash}.log");
    }

    public static async Task WriteDiscoveryFileAsync(string solutionPath, int port, string token, CancellationToken ct = default)
    {
        var filePath = GetDiscoveryFilePath(solutionPath);
        var info = new DiscoveryInfo(
            Version: NavConstants.CurrentContractVersion,
            Pid: Environment.ProcessId,
            Port: port,
            Token: token,
            SolutionPath: Path.GetFullPath(solutionPath),
            StartedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(info, NavJsonSerializer.IndentedOptions);
        await File.WriteAllTextAsync(filePath, json, ct);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort permission restriction
            }
        }
    }

    public static void DeleteDiscoveryFile(string solutionPath)
    {
        try
        {
            var filePath = GetDiscoveryFilePath(solutionPath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    public static async Task<DiscoveryInfo?> ReadDiscoveryFileAsync(string solutionPath, CancellationToken ct = default)
    {
        var filePath = GetDiscoveryFilePath(solutionPath);
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<DiscoveryInfo>(json, NavJsonSerializer.Options);
        }
        catch
        {
            return null;
        }
    }
}
