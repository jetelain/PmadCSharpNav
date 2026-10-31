using AwesomeAssertions;
using Pmad.CSharpNav.Infrastructure.Adapters;

namespace Pmad.CSharpNav.Infrastructure.Tests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Repo\Solution.sln", "C:/Repo")]
    [InlineData("C:/Repo/Solution.sln", "C:/Repo")]
    [InlineData("/home/user/Repo/Solution.sln", "/home/user/Repo")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void GetDirectoryName_Extracts_Directory_Across_Platforms(string? input, string expected)
    {
        var result = PathNormalizer.GetDirectoryName(input);
        result.Replace('\\', '/').Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Repo", @"C:\Repo\src\MyProject\UserService.cs")]
    [InlineData("C:/Repo", "C:/Repo/src/MyProject/UserService.cs")]
    [InlineData(@"C:\Repo", "C:/Repo/src/MyProject/UserService.cs")]
    [InlineData("C:/Repo", @"C:\Repo\src\MyProject\UserService.cs")]
    [InlineData("/home/user/Repo", "/home/user/Repo/src/MyProject/UserService.cs")]
    public void GetRelativePath_Computes_Relative_Path_Across_Platforms(string solutionDir, string path)
    {
        var rel = PathNormalizer.GetRelativePath(solutionDir, path);
        rel.Should().NotContain("C:");
        rel.Should().NotContain("Repo");
        rel.Replace('\\', '/').Should().Be("src/MyProject/UserService.cs");
    }

    [Fact]
    public void GetRelativePath_Returns_Original_When_Unrelated()
    {
        var rel = PathNormalizer.GetRelativePath(@"C:\Repo", @"D:\Other\File.cs");
        rel.Should().Contain("File.cs");
    }
}

