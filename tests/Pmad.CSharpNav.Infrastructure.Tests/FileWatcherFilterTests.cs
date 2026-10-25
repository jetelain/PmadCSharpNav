using AwesomeAssertions;
using Pmad.CSharpNav.Infrastructure.Watcher;
using Xunit;

namespace Pmad.CSharpNav.Infrastructure.Tests;

public class FileWatcherFilterTests
{
    [Theory]
    [InlineData(@"C:\Project\bin\Debug\net10.0\App.dll", true)]
    [InlineData(@"C:/Project/obj/Debug/net10.0/App.dll", true)]
    [InlineData(@"C:\Project\.git\objects\1234", true)]
    [InlineData(@"C:\Project\.vs\PmadCSharpNav\v17\.suo", true)]
    [InlineData(@"C:\Project\src\Services\OrderService.cs", false)]
    [InlineData(@"C:\Project\src\Pmad.App.csproj", false)]
    [InlineData(@"C:\Project\Directory.Build.props", false)]
    public void ShouldIgnore_CorrectlyIdentifies_IgnoredPaths(string path, bool expectedIgnore)
    {
        DebouncedFileWatcher.ShouldIgnore(path).Should().Be(expectedIgnore);
    }

    [Theory]
    [InlineData("App.csproj", ".csproj", true)]
    [InlineData("MySolution.sln", ".sln", true)]
    [InlineData("MySolution.slnx", ".slnx", true)]
    [InlineData("Directory.Build.props", ".props", true)]
    [InlineData("Directory.Build.targets", ".targets", true)]
    [InlineData("Directory.Packages.props", ".props", true)]
    [InlineData("global.json", ".json", true)]
    [InlineData(".editorconfig", "", true)]
    [InlineData("OrderService.cs", ".cs", false)]
    [InlineData("README.md", ".md", false)]
    public void IsProjectOrSolutionFile_Identifies_ConfigChanges(string fileName, string ext, bool expectedResult)
    {
        DebouncedFileWatcher.IsProjectOrSolutionFile(fileName, ext).Should().Be(expectedResult);
    }
}
