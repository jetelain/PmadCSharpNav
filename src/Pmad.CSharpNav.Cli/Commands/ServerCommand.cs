using System.CommandLine;
using System.Globalization;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class ServerCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var serverCmd = new Command("server", "Manage the background csnav daemon");

        var statusCmd = new Command("status", "Check the status of the background daemon");
        statusCmd.SetAction(async (parseResult, ct) =>
        {
            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? "markdown";

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var running = await client.GetRunningDiscoveryAsync(solutionPath, ct);

                if (running == null)
                {
                    if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("{\"status\":\"stopped\"}");
                    }
                    else if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Server is not running for: `{0}`", solutionPath));
                    }
                    else
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Server is not running for: {0}", solutionPath));
                    }
                    return 0;
                }

                var status = await client.GetAsync<WorkspaceStatusResponse>(solutionPath, "/api/status", ct);

                if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(NavJsonSerializer.Serialize(status));
                }
                else if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "### Server Status: Running (PID: {0}, Port: {1})", running.Pid, running.Port));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "- **Solution:** `{0}`", status.SolutionPath));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "- **Projects:** {0}", status.ProjectCount));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "- **Memory:** {0:F1} MB", status.MemoryUsageBytes / (1024.0 * 1024.0)));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "- **State:** {0}", status.State));
                }
                else
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Server Status: Running (PID: {0}, Port: {1})", running.Pid, running.Port));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Solution:    {0}", status.SolutionPath));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Projects:    {0}", status.ProjectCount));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Memory:      {0:F1} MB", status.MemoryUsageBytes / (1024.0 * 1024.0)));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "State:       {0}", status.State));
                }

                return 0;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(CultureInfo.InvariantCulture, "Error: {0}", ex.Message));
                return 1;
            }
        });

        var startCmd = new Command("start", "Start the background daemon if not already running");
        startCmd.SetAction(async (parseResult, ct) =>
        {
            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var info = await client.EnsureDaemonRunningAsync(solutionPath, ct);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Server is running on port {0} (PID: {1})", info.Port, info.Pid));
                return 0;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(CultureInfo.InvariantCulture, "Error: {0}", ex.Message));
                return 1;
            }
        });

        var stopCmd = new Command("stop", "Stop the background daemon gracefully");
        stopCmd.SetAction(async (parseResult, ct) =>
        {
            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var stopped = await client.StopDaemonAsync(solutionPath, ct);
                if (stopped)
                {
                    Console.WriteLine("Server stopped.");
                }
                else
                {
                    Console.WriteLine("Server was not running.");
                }
                return 0;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(CultureInfo.InvariantCulture, "Error: {0}", ex.Message));
                return 1;
            }
        });

        var reloadCmd = new Command("reload", "Trigger manual workspace reload for the active daemon");
        reloadCmd.SetAction(async (parseResult, ct) =>
        {
            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                await client.ReloadSolutionAsync(solutionPath, ct);
                Console.WriteLine("Solution workspace reloaded successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(CultureInfo.InvariantCulture, "Error: {0}", ex.Message));
                return 1;
            }
        });

        serverCmd.Add(statusCmd);
        serverCmd.Add(startCmd);
        serverCmd.Add(stopCmd);
        serverCmd.Add(reloadCmd);

        return serverCmd;
    }
}
