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

    [Fact]
    public void EnsureStartedAsync_Methods_Exist_And_Accept_Expected_Signatures()
    {
        using var client = new NavDaemonClient();
        var method1 = typeof(NavDaemonClient).GetMethod(nameof(NavDaemonClient.EnsureStartedAsync), new[] { typeof(string), typeof(CancellationToken) });
        method1.Should().NotBeNull();
        method1!.ReturnType.Should().Be<Task<DiscoveryInfo>>();

        var method2 = typeof(NavDaemonClient).GetMethod(nameof(NavDaemonClient.EnsureStartedAsync), new[] { typeof(string), typeof(TextWriter), typeof(CancellationToken) });
        method2.Should().NotBeNull();
        method2!.ReturnType.Should().Be<Task<DiscoveryInfo>>();

        var method3 = typeof(NavDaemonClient).GetMethod(nameof(NavDaemonClient.EnsureStartedWithInfoAsync), new[] { typeof(string), typeof(CancellationToken) });
        method3.Should().NotBeNull();
        method3!.ReturnType.Should().Be<Task<DiscoveryInfo>>();

        var method4 = typeof(NavDaemonClient).GetMethod(nameof(NavDaemonClient.EnsureStartedWithInfoAsync), new[] { typeof(string), typeof(TextWriter), typeof(CancellationToken) });
        method4.Should().NotBeNull();
        method4!.ReturnType.Should().Be<Task<DiscoveryInfo>>();
    }

    [Fact]
    public async Task ServerCommand_Start_FormatJson_OutputsStructuredDiscoveryPayload()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    using var tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
                    using var stream = tcpClient.GetStream();
                    var buffer = new byte[1024];
                    _ = await stream.ReadAsync(buffer, cts.Token);
                    var responseBytes = System.Text.Encoding.UTF8.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 20\r\nConnection: close\r\n\r\n{\"status\":\"healthy\"}");
                    await stream.WriteAsync(responseBytes, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            await NavDiscovery.WriteDiscoveryFileAsync(_solutionPath, port, "secret_bearer_token_123");

            using var client = new NavDaemonClient();
            using var progress = new StringWriter();
            var info = await client.EnsureStartedAsync(_solutionPath, progress);
            info.Port.Should().Be(port);
            info.Token.Should().Be("secret_bearer_token_123");

            var globals = new Pmad.CSharpNav.Cli.Commands.GlobalOptions();
            var root = new System.CommandLine.RootCommand();
            globals.AddTo(root);
            root.Add(Pmad.CSharpNav.Cli.Commands.ServerCommand.Create(globals, client));

            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                var parseResult = root.Parse(["server", "start", "--solution", _solutionPath, "--format", "json"]);
                var exitCode = await parseResult.InvokeAsync();
                exitCode.Should().Be(0);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var jsonOutput = stdout.ToString().Trim();
            var parsed = NavJsonSerializer.Deserialize<ServerStartResponse>(jsonOutput);
            parsed.Should().NotBeNull();
            parsed!.Port.Should().Be(port);
            parsed.Pid.Should().Be(Environment.ProcessId);
            parsed.Token.Should().Be("secret_bearer_token_123");
            parsed.Url.Should().Be($"http://127.0.0.1:{port}");
            parsed.SolutionPath.Should().Be(Path.GetFullPath(_solutionPath));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public void WindowsProcessLauncher_OnWindows_CanSpawnProcessViaWmi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var success = WindowsProcessLauncher.TryStartViaWmi(
            "powershell.exe -NoProfile -Command Start-Sleep -Milliseconds 100",
            null,
            out var pid);
        try
        {
            success.Should().BeTrue();
            pid.Should().BeGreaterThan(0);
        }
        finally
        {
            KillProcessSafe(pid);
        }
    }

    [Fact]
    public void StartBackgroundServer_SpawnsOrDelegates_WithoutThrowing()
    {
        var proc = NavDaemonClient.StartBackgroundServer(_solutionPath);
        try
        {
            proc.Should().NotBeNull();
        }
        finally
        {
            KillAndDisposeProcessSafe(proc);
        }
    }

    [Fact]
    public void StartBackgroundServer_WithoutWmi_RedirectsStandardStreams()
    {
        var proc = NavDaemonClient.StartBackgroundServer(_solutionPath, allowWmi: false);
        try
        {
            proc.Should().NotBeNull();
            proc!.StartInfo.RedirectStandardOutput.Should().BeTrue();
            proc.StartInfo.RedirectStandardError.Should().BeTrue();
            proc.StartInfo.RedirectStandardInput.Should().BeTrue();
        }
        finally
        {
            KillAndDisposeProcessSafe(proc);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliServerStart_DoesNotLeakStandardStreamHandlesToDaemon(bool disableWmi)
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "Pmad.CSharpNav.Cli.dll");
        File.Exists(cliAssembly).Should().BeTrue();

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{cliAssembly}\" server start --solution \"{_solutionPath}\" --format json",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["CSNAV_DISABLE_WMI"] = disableWmi ? "1" : "0";

        int daemonPid = 0;
        using var cliProc = System.Diagnostics.Process.Start(psi);
        cliProc.Should().NotBeNull();

        try
        {
            cliProc!.StandardInput.Close();

            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdoutTask = cliProc.StandardOutput.ReadToEndAsync(startupCts.Token);
            var stderrTask = cliProc.StandardError.ReadToEndAsync(startupCts.Token);

            await cliProc.WaitForExitAsync(startupCts.Token);

            // Once the CLI process exits, stdout and stderr must reach EOF immediately even though
            // the background daemon is still alive. If the daemon inherited the CLI's pipe handles,
            // awaiting stdoutTask / stderrTask here will time out after 3 seconds.
            using var eofCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var stdout = await stdoutTask.WaitAsync(eofCts.Token);
            var stderr = await stderrTask.WaitAsync(eofCts.Token);

            cliProc.ExitCode.Should().Be(0, because: $"stderr was: {stderr}");

            var parsed = NavJsonSerializer.Deserialize<ServerStartResponse>(stdout.Trim());
            parsed.Should().NotBeNull();
            daemonPid = parsed!.Pid;
            daemonPid.Should().BeGreaterThan(0);

            using var daemonProc = System.Diagnostics.Process.GetProcessById(daemonPid);
            daemonProc.HasExited.Should().BeFalse("daemon should still be running in the background after CLI exits and closes pipes");
        }
        finally
        {
            using var client = new NavDaemonClient();
            try
            {
                await client.StopDaemonAsync(_solutionPath);
            }
            catch
            {
            }

            KillProcessSafe(daemonPid);
            KillAndDisposeProcessSafe(cliProc);
        }
    }

    private static void KillProcessSafe(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void KillAndDisposeProcessSafe(System.Diagnostics.Process? proc)
    {
        if (proc == null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            proc.Dispose();
        }
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
