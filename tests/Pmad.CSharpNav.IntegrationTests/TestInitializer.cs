using System.Runtime.CompilerServices;
using Pmad.CSharpNav.Infrastructure.Workspace;

namespace Pmad.CSharpNav.IntegrationTests;

public static class TestInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        RoslynLocator.EnsureRegistered();
    }
}
