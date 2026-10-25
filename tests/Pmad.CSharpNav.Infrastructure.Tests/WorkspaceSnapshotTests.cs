using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Pmad.CSharpNav.Infrastructure.Workspace;
using Xunit;

namespace Pmad.CSharpNav.Infrastructure.Tests;

public class WorkspaceSnapshotTests
{
    [Fact]
    public void RoslynWorkspaceSnapshot_FindDocumentId_MatchesNormalizedPaths()
    {
        using var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "SampleLib",
            "SampleLib",
            LanguageNames.CSharp,
            filePath: @"C:\Repo\SampleLib\SampleLib.csproj");

        var docInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(projectInfo.Id),
            "Service.cs",
            filePath: @"C:\Repo\SampleLib\Service.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Service {}"), VersionStamp.Create())));

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(docInfo);

        var snapshot = new RoslynWorkspaceSnapshot(solution, @"C:\Repo\Solution.slnx", 1);

        // Check path resolution with both forward and backslashes
        var foundIdWindows = snapshot.FindDocumentId(@"C:\Repo\SampleLib\Service.cs");
        var foundIdUnix = snapshot.FindDocumentId("C:/Repo/SampleLib/Service.cs");

        foundIdWindows.Should().NotBeNull();
        foundIdUnix.Should().NotBeNull();
        foundIdWindows.Should().Be(docInfo.Id);
        foundIdUnix.Should().Be(docInfo.Id);

        // Unknown document returns null
        snapshot.FindDocumentId(@"C:\Repo\Other.cs").Should().BeNull();
    }

    [Fact]
    public void RoslynWorkspaceSnapshot_GetDocumentIds_ReturnsAllIdsForMultiTargeting()
    {
        using var workspace = new AdhocWorkspace();
        var proj1 = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "SampleLib_Net8",
            "SampleLib_Net8",
            LanguageNames.CSharp,
            filePath: @"C:\Repo\SampleLib\SampleLib.csproj");

        var proj2 = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "SampleLib_Net10",
            "SampleLib_Net10",
            LanguageNames.CSharp,
            filePath: @"C:\Repo\SampleLib\SampleLib.csproj");

        var doc1 = DocumentInfo.Create(
            DocumentId.CreateNewId(proj1.Id),
            "Service.cs",
            filePath: @"C:\Repo\SampleLib\Service.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Service {}"), VersionStamp.Create())));

        var doc2 = DocumentInfo.Create(
            DocumentId.CreateNewId(proj2.Id),
            "Service.cs",
            filePath: @"C:\Repo\SampleLib\Service.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Service {}"), VersionStamp.Create())));

        var solution = workspace.CurrentSolution
            .AddProject(proj1)
            .AddProject(proj2)
            .AddDocument(doc1)
            .AddDocument(doc2);

        var snapshot = new RoslynWorkspaceSnapshot(solution, @"C:\Repo\Solution.slnx", 1);

        var docIds = snapshot.GetDocumentIds(@"C:\Repo\SampleLib\Service.cs");
        docIds.Should().HaveCount(2);
        docIds.Should().Contain(doc1.Id);
        docIds.Should().Contain(doc2.Id);
    }

    [Fact]
    public async Task RoslynWorkspaceSnapshot_WithDocumentText_UpdatesSnapshotLazily()
    {
        using var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "SampleLib",
            "SampleLib",
            LanguageNames.CSharp,
            filePath: @"C:\Repo\SampleLib\SampleLib.csproj");

        var docInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(projectInfo.Id),
            "Service.cs",
            filePath: @"C:\Repo\SampleLib\Service.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Service { public void Hello() {} }"), VersionStamp.Create())));

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(docInfo);

        var snapshot1 = new RoslynWorkspaceSnapshot(solution, @"C:\Repo\Solution.slnx", 1);

        // Incremental document text swap
        var updatedText = SourceText.From("public class Service { public void World() {} }");
        var updatedSolution = snapshot1.Solution.WithDocumentText(docInfo.Id, updatedText);

        var snapshot2 = new RoslynWorkspaceSnapshot(updatedSolution, @"C:\Repo\Solution.slnx", 2);

        snapshot2.SnapshotVersion.Should().Be(2);
        snapshot2.Solution.Should().NotBeSameAs(snapshot1.Solution);
        var text = await snapshot2.Solution.GetDocument(docInfo.Id)!.GetTextAsync();
        text.ToString().Should().Contain("World()");
    }
}
