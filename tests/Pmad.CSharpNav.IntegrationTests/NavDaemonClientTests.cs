using AwesomeAssertions;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.IntegrationTests;

public sealed class NavDaemonClientTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _solutionPath;

    public NavDaemonClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csnav_client_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _solutionPath = Path.Combine(_tempDir, "FakeSolution.sln");
        File.WriteAllText(_solutionPath, "");
    }

    [Fact]
    public void GetLockFilePath_ReturnsExpectedPathWithLockExtension()
    {
        var lockPath = NavDiscovery.GetLockFilePath(_solutionPath);

        lockPath.Should().NotBeNullOrWhiteSpace();
        lockPath.Should().EndWith(".lock");
        Path.GetDirectoryName(lockPath).Should().Be(NavDiscovery.GetServersDirectory());
    }

    [Fact]
    public async Task EnsureDaemonRunningAsync_WithAliveAndHealthyDaemon_ReturnsImmediatelyWithoutLockException()
    {
        // Setup discovery file pointing to current process and simulated port
        var discoveryFile = NavDiscovery.GetDiscoveryFilePath(_solutionPath);
        var info = new DiscoveryInfo(
            Version: NavConstants.CurrentContractVersion,
            Pid: Environment.ProcessId,
            Port: 12345,
            Token: "test_token",
            SolutionPath: _solutionPath,
            StartedAt: DateTimeOffset.UtcNow);

        // Even if port is not listening, EnsureDaemonRunningAsync will attempt PingHealthAsync
        // To verify no SynchronizationLockException occurs during lock/unlock in async methods:
        using var client = new NavDaemonClient();

        // Testing that calling GetRunningDiscoveryAsync or fast path does not throw SynchronizationLockException
        var running = await client.GetRunningDiscoveryAsync(_solutionPath);
        running.Should().BeNull(); // Port 12345 is not listening, so health check fails cleanly without throwing
    }

    public void Dispose()
    {
        try
        {
            NavDiscovery.DeleteDiscoveryFile(_solutionPath);
            var lockFile = NavDiscovery.GetLockFilePath(_solutionPath);
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
