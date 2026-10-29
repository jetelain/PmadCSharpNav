using AwesomeAssertions;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Infrastructure.Adapters;
using Pmad.CSharpNav.Infrastructure.Workspace;

namespace Pmad.CSharpNav.IntegrationTests;

public sealed class WorkspaceIntegrationTests : IAsyncLifetime, IDisposable
{
    private RoslynSolutionWorkspace _workspace = null!;
    private string _solutionPath = null!;

    public async Task InitializeAsync()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "PmadCSharpNav.slnx")))
        {
            currentDir = currentDir.Parent;
        }

        currentDir.Should().NotBeNull("Solution file PmadCSharpNav.slnx should exist in ancestors");
        _solutionPath = Path.Combine(currentDir!.FullName, "PmadCSharpNav.slnx");

        _workspace = new RoslynSolutionWorkspace(_solutionPath);
        await _workspace.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    [Fact]
    public async Task Workspace_Loads_Projects_And_Provides_Status()
    {
        var status = await _workspace.GetStatusAsync();

        status.Should().NotBeNull();
        status.ProjectCount.Should().BeGreaterThan(0);
        status.State.Should().Be("ready");
    }

    [Fact]
    public async Task FindReferences_Finds_Known_Symbol_References()
    {
        var finder = new RoslynReferenceFinder();
        var handler = new FindReferencesHandler(_workspace, finder);

        var request = new FindReferencesRequest(
            Symbol: "NavConstants",
            ExcludeTests: true,
            Limit: 50);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Target.Name.Should().Be("NavConstants");
        response.References.Should().NotBeEmpty();
        response.TotalReferences.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchApi_Returns_Matching_Symbols_In_Solution()
    {
        var searchEngine = new RoslynApiSearchEngine();
        var handler = new SearchApiHandler(_workspace, searchEngine);

        var request = new SearchApiRequest(
            Pattern: "NavJsonSerializer",
            Scope: "solution",
            Limit: 10);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Results.Should().Contain(r => r.Name.Contains("NavJsonSerializer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchApi_Finds_External_Dependency_Referenced_In_Non_First_Project()
    {
        var searchEngine = new RoslynApiSearchEngine();
        var handler = new SearchApiHandler(_workspace, searchEngine);

        var request = new SearchApiRequest(
            Pattern: "DecompilerException",
            Scope: "all",
            Kind: "class",
            Limit: 10);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Results.Should().Contain(r =>
            r.Name.Contains("DecompilerException", StringComparison.OrdinalIgnoreCase) &&
            r.Assembly == "ICSharpCode.Decompiler" &&
            r.IsExternal &&
            !string.IsNullOrWhiteSpace(r.Summary));
    }

    [Fact]
    public async Task ExploreType_Loads_Xml_Documentation_For_External_Type()
    {
        var explorer = new RoslynTypeExplorer();
        var handler = new ExploreTypeHandler(_workspace, explorer);

        var request = new ExploreTypeRequest(
            Type: "DecompilerException",
            NoInherited: true);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Type.Name.Should().Be("DecompilerException");
        response.Type.Summary.Should().NotBeNullOrWhiteSpace();
        response.Type.Summary.Should().Contain("DecompilerException");
    }

    [Fact]
    public async Task SearchApi_Finds_External_Dependency_With_Assembly_Scope()
    {
        var searchEngine = new RoslynApiSearchEngine();
        var handler = new SearchApiHandler(_workspace, searchEngine);

        var request = new SearchApiRequest(
            Pattern: "DecompilerException",
            Scope: "ICSharpCode.Decompiler",
            Kind: "class",
            Limit: 10);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Results.Should().Contain(r =>
            r.Name.Contains("DecompilerException", StringComparison.OrdinalIgnoreCase) &&
            r.Assembly == "ICSharpCode.Decompiler");
    }

    [Fact]
    public async Task ExploreType_Inspects_Members_And_Structure()
    {
        var explorer = new RoslynTypeExplorer();
        var handler = new ExploreTypeHandler(_workspace, explorer);

        var request = new ExploreTypeRequest(
            Type: "Pmad.CSharpNav.Contracts.FindReferencesRequest",
            NoInherited: true);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Type.Name.Should().Be("FindReferencesRequest");
        response.Properties.Should().NotBeNull();
        response.Properties!.Should().Contain(p => p.Name == "Symbol");
    }

    [Fact]
    public async Task Decompile_Decompiles_System_Uri()
    {
        var decompiler = new ILSpyDecompiler();
        var handler = new DecompileMemberHandler(_workspace, decompiler);

        var request = new DecompileRequest(
            Assembly: "System.Private.CoreLib",
            Type: "System.Uri");

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Type.Should().Be("System.Uri");
        response.Source.Should().NotBeNullOrWhiteSpace();
        response.Source.Should().Contain("class Uri");
    }

    [Fact]
    public async Task SyncDocument_Increments_Snapshot_Version()
    {
        var snapshotBefore = (RoslynWorkspaceSnapshot)await _workspace.GetCurrentSnapshotAsync();

        var anyDoc = snapshotBefore.Solution.Projects.SelectMany(p => p.Documents).First();
        var syncHandler = new SyncDocumentHandler(_workspace);

        var syncResponse = await syncHandler.HandleAsync(new SyncRequest(
            FilePath: anyDoc.FilePath!,
            Content: "// updated for test\npublic class SyncTestClass { }"));

        syncResponse.SnapshotVersion.Should().BeGreaterThan(snapshotBefore.SnapshotVersion);

        var snapshotAfter = await _workspace.GetCurrentSnapshotAsync();
        snapshotAfter.SnapshotVersion.Should().Be(syncResponse.SnapshotVersion);
    }

    [Fact]
    public async Task SyncDocument_WithRazorFile_AddsAndUpdatesAdditionalDocument()
    {
        var dummyRazor = Path.Combine(Path.GetDirectoryName(_solutionPath)!, "src", "Pmad.CSharpNav.Infrastructure", "TestComponent.razor");
        var syncHandler = new SyncDocumentHandler(_workspace);

        var syncResponse1 = await syncHandler.HandleAsync(new SyncRequest(
            FilePath: dummyRazor,
            Content: "<h1>Hello Razor</h1>"));

        syncResponse1.SyncedFiles.Should().Contain(dummyRazor);

        var snapshot1 = (RoslynWorkspaceSnapshot)await _workspace.GetCurrentSnapshotAsync();
        var addDocId = snapshot1.FindAdditionalDocumentId(dummyRazor);
        addDocId.Should().NotBeNull();

        var syncResponse2 = await syncHandler.HandleAsync(new SyncRequest(
            FilePath: dummyRazor,
            Content: "<h1>Updated Razor</h1>"));

        syncResponse2.SnapshotVersion.Should().BeGreaterThan(syncResponse1.SnapshotVersion);
        var snapshot2 = (RoslynWorkspaceSnapshot)await _workspace.GetCurrentSnapshotAsync();
        var doc = snapshot2.Solution.GetAdditionalDocument(addDocId!);
        doc.Should().NotBeNull();
        var text = await doc!.GetTextAsync();
        text.ToString().Should().Be("<h1>Updated Razor</h1>");

        // Sync with null removes the additional document
        var syncResponse3 = await syncHandler.HandleAsync(new SyncRequest(
            FilePath: dummyRazor,
            Content: null));

        var snapshot3 = (RoslynWorkspaceSnapshot)await _workspace.GetCurrentSnapshotAsync();
        snapshot3.FindAdditionalDocumentId(dummyRazor).Should().BeNull();
    }
}
