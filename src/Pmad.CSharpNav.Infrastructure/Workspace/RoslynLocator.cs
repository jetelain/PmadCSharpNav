using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Pmad.CSharpNav.Infrastructure.Workspace;

/// <summary>
/// Ensures MSBuildLocator registers the .NET SDK before any Roslyn MSBuild types are JIT-compiled.
/// </summary>
public static class RoslynLocator
{
    private static int _registered;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
