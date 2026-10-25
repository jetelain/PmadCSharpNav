using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Build.Locator;

namespace Pmad.CSharpNav.Infrastructure.Workspace;

/// <summary>
/// Ensures MSBuildLocator registers the .NET SDK before any Roslyn MSBuild types are JIT-compiled.
/// </summary>
public static class RoslynLocator
{
    private static int _registered;
    private static string? _resolvedSdkPath;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "RoslynLocator must register MSBuildLocator before any MSBuild types are referenced in the library.")]
    public static void Init()
    {
        EnsureRegistered();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            RegisterCore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterCore()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                if (instances.Length > 0)
                {
                    // Prefer matching the running .NET runtime major version (e.g. 10), then highest
                    var target = instances.FirstOrDefault(i => i.Version.Major == Environment.Version.Major)
                                 ?? instances.OrderByDescending(i => i.Version).First();
                    MSBuildLocator.RegisterInstance(target);
                    _resolvedSdkPath = target.MSBuildPath;
                }
                else
                {
                    var instance = MSBuildLocator.RegisterDefaults();
                    _resolvedSdkPath = instance?.MSBuildPath;
                }
            }
            catch
            {
                // Fallback: manually locate .NET SDK directory containing Microsoft.Build.Framework.dll
                var fallbackSdkPath = FindDotNetSdkPath();
                if (!string.IsNullOrEmpty(fallbackSdkPath))
                {
                    try
                    {
                        MSBuildLocator.RegisterMSBuildPath(fallbackSdkPath);
                        _resolvedSdkPath = fallbackSdkPath;
                    }
                    catch
                    {
                        _resolvedSdkPath = fallbackSdkPath;
                    }
                }
            }
        }

        _resolvedSdkPath ??= FindDotNetSdkPath();

        // Additional safety net: In environments where MSBuildLocator's internal resolver misses or
        // before MSBuildLocator hook, ensure AssemblyLoadContext can load Microsoft.Build.* from resolved SDK path.
        if (!string.IsNullOrEmpty(_resolvedSdkPath))
        {
            var sdkDir = _resolvedSdkPath;
            AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                if (assemblyName.Name != null &&
                    (assemblyName.Name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) ||
                     assemblyName.Name.StartsWith("Microsoft.CodeAnalysis.Workspaces.MSBuild", StringComparison.OrdinalIgnoreCase)))
                {
                    var candidate = Path.Combine(sdkDir, assemblyName.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            return context.LoadFromAssemblyPath(candidate);
                        }
                        catch
                        {
                            // Ignore and continue normal probing
                        }
                    }
                }
                return null;
            };
        }
    }

    private static string? FindDotNetSdkPath()
    {
        var candidates = new List<string?>();

        candidates.Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        candidates.Add(Environment.GetEnvironmentVariable("DOTNET_INSTALL_DIR"));
        candidates.Add(Environment.GetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"));

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(processDir))
            {
                candidates.Add(processDir);
                candidates.Add(Path.GetDirectoryName(processDir));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"C:\Program Files\dotnet");
            candidates.Add(@"C:\Program Files (x86)\dotnet");
        }
        else
        {
            candidates.Add("/usr/share/dotnet");
            candidates.Add("/usr/local/share/dotnet");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                candidates.Add(Path.Combine(home, ".dotnet"));
            }
        }

        foreach (var root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var sdkDir = Path.Combine(root, "sdk");
            if (!Directory.Exists(sdkDir))
                continue;

            try
            {
                var versionDirs = Directory.GetDirectories(sdkDir)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

                foreach (var dir in versionDirs)
                {
                    if (File.Exists(Path.Combine(dir, "Microsoft.Build.Framework.dll")))
                    {
                        return dir;
                    }
                }
            }
            catch
            {
                // Skip if directory access fails
            }
        }

        return null;
    }
}
