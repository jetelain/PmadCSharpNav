using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pmad.CSharpNav.Cli.Client;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessLauncher
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    public static bool TryStartViaWmi(string commandLine, string? currentDirectory = null)
        => TryStartViaWmi(commandLine, currentDirectory, out _);

    public static bool TryStartViaWmi(string commandLine, string? currentDirectory, out int processId)
    {
        processId = 0;
        try
        {
            using var processClass = new ManagementClass("Win32_Process");
            using var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = commandLine;
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
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
                if (returnCode == 0)
                {
                    if (outParams["ProcessId"] is not null)
                    {
                        processId = Convert.ToInt32(outParams["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return true;
                }
            }
        }
        catch
        {
            // Best effort: WMI may be disabled, restricted, or fail in certain environments
        }

        return false;
    }

    /// <summary>
    /// Temporarily clears HANDLE_FLAG_INHERIT on the current process's standard handles
    /// so that Process.Start (which calls CreateProcess with bInheritHandles: true) does
    /// not duplicate the CLI's redirected stdin/stdout/stderr pipe handles into the daemon.
    /// </summary>
    public static IDisposable SuppressStandardHandleInheritance()
    {
        return new StandardHandleInheritanceScope();
    }

    private sealed class StandardHandleInheritanceScope : IDisposable
    {
        private readonly List<IntPtr> _handlesToRestore = new(3);

        public StandardHandleInheritanceScope()
        {
            TryClearInherit(StdInputHandle);
            TryClearInherit(StdOutputHandle);
            TryClearInherit(StdErrorHandle);
        }

        private void TryClearInherit(int stdHandleId)
        {
            try
            {
                var handle = GetStdHandle(stdHandleId);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return;
                }

                if (GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0)
                {
                    if (SetHandleInformation(handle, HandleFlagInherit, 0))
                    {
                        _handlesToRestore.Add(handle);
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }

        public void Dispose()
        {
            foreach (var handle in _handlesToRestore)
            {
                try
                {
                    SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }
}
