using System.Management;
using System.Runtime.Versioning;

namespace Pmad.CSharpNav.Cli.Client;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessLauncher
{
    public static bool TryStartViaWmi(string commandLine, string? currentDirectory = null)
    {
        try
        {
            using var processClass = new ManagementClass("Win32_Process");
            using var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = commandLine;
            if (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                inParams["CurrentDirectory"] = currentDirectory;
            }

            using var startupClass = new ManagementClass("Win32_ProcessStartup");
            using var startupInstance = startupClass.CreateInstance();
            if (startupInstance != null)
            {
                startupInstance["ShowWindow"] = 0; // SW_HIDE
                inParams["ProcessStartupInformation"] = startupInstance;
            }

            using var outParams = processClass.InvokeMethod("Create", inParams, null);
            if (outParams != null)
            {
                var returnCode = Convert.ToUInt32(outParams["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
                return returnCode == 0;
            }
        }
        catch
        {
            // Best effort: WMI may be disabled, restricted, or fail in certain environments
        }

        return false;
    }
}
