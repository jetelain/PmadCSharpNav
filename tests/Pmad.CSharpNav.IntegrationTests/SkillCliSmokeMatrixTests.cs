using System.CommandLine;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Commands;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Infrastructure.Adapters;
using Pmad.CSharpNav.Infrastructure.Workspace;

namespace Pmad.CSharpNav.IntegrationTests;

/// <summary>
/// Smoke-test matrix verifying that every command, alias, flag, Doc-ID prefix,
/// JSON pagination field, and recovery workflow documented in SKILL.md stays
/// synchronized with the CLI and daemon implementation.
/// </summary>
public sealed class SkillCliSmokeMatrixTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = null!;
    private string _slnPath = null!;
    private string _appCsprojPath = null!;
    private string _testCsprojPath = null!;
    private string _orderServicePath = null!;
    private RoslynSolutionWorkspace _workspace = null!;
    private RoslynReferenceFinder _referenceFinder = null!;
    private RoslynApiSearchEngine _apiSearchEngine = null!;
    private RoslynTypeExplorer _typeExplorer = null!;
    private ILSpyDecompiler _decompiler = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csnav_smoke_" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(_tempDir, "SmokeApp");
        var testDir = Path.Combine(_tempDir, "SmokeApp.Tests");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);

        _slnPath = Path.Combine(_tempDir, "SmokeSolution.slnx");
        await File.WriteAllTextAsync(_slnPath, """
            <Solution>
              <Project Path="SmokeApp/SmokeApp.csproj" />
              <Project Path="SmokeApp.Tests/SmokeApp.Tests.csproj" />
            </Solution>
            """);

        _appCsprojPath = Path.Combine(appDir, "SmokeApp.csproj");
        await File.WriteAllTextAsync(_appCsprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);

        _testCsprojPath = Path.Combine(testDir, "SmokeApp.Tests.csproj");
        await File.WriteAllTextAsync(_testCsprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../SmokeApp/SmokeApp.csproj" />
              </ItemGroup>
            </Project>
            """);

        _orderServicePath = Path.Combine(appDir, "DomainModels.cs");
        await File.WriteAllTextAsync(_orderServicePath, """
            using System;

            namespace SmokeApp;

            public enum OrderStatus
            {
                Pending,
                Completed
            }

            public readonly struct Money
            {
                public decimal Amount { get; init; }
            }

            public record OrderRecord(int Id, OrderStatus Status);

            public delegate void OrderAuditHandler(int orderId);

            /// <summary>
            /// Repository contract for sha256 verified order storage.
            /// </summary>
            public interface IOrderRepository
            {
                OrderRecord? GetOrder(int id);
                void SaveOrder(OrderRecord order);
            }

            public class SqlOrderRepository : IOrderRepository
            {
                public OrderRecord? GetOrder(int id) => new(id, OrderStatus.Pending);
                public void SaveOrder(OrderRecord order) { }
            }

            public class OrderService
            {
                public readonly IOrderRepository _repository;
                private int _secretCounter;

                public event EventHandler? OrderProcessed;

                public int ProcessedCount { get; private set; }

                public OrderService(IOrderRepository repository)
                {
                    _repository = repository;
                }

                /// <summary>
                /// Processes an order and calculates sha256 checksum.
                /// </summary>
                public OrderRecord? ProcessOrder(int id)
                {
                    _secretCounter++;
                    ProcessedCount++;
                    var order = _repository.GetOrder(id);
                    if (order != null)
                    {
                        _repository.SaveOrder(order);
                        OrderProcessed?.Invoke(this, EventArgs.Empty);
                    }
                    return order;
                }

                internal void InternalMaintenance() { }
            }
            """);

        var generatedFilePath = Path.Combine(appDir, "GeneratedHelper.g.cs");
        await File.WriteAllTextAsync(generatedFilePath, """
            // <auto-generated/>
            namespace SmokeApp;

            public interface IGeneratedContract
            {
                void ExecuteGenerated();
            }

            public class GeneratedService : IGeneratedContract
            {
                public void ExecuteGenerated()
                {
                    var repo = new SqlOrderRepository();
                    _ = repo.GetOrder(42);
                }
            }
            """);

        var testFilePath = Path.Combine(testDir, "FakeOrderRepository.cs");
        await File.WriteAllTextAsync(testFilePath, """
            namespace SmokeApp.Tests;

            public class FakeOrderRepository : SmokeApp.IOrderRepository
            {
                public SmokeApp.OrderRecord? GetOrder(int id) => null;
                public void SaveOrder(SmokeApp.OrderRecord order)
                {
                    var svc = new SmokeApp.OrderService(this);
                    _ = svc.ProcessOrder(id);
                }
            }
            """);

        _workspace = new RoslynSolutionWorkspace(_slnPath);
        await _workspace.InitializeAsync();

        _referenceFinder = new RoslynReferenceFinder();
        _apiSearchEngine = new RoslynApiSearchEngine();
        _typeExplorer = new RoslynTypeExplorer();
        _decompiler = new ILSpyDecompiler();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static RootCommand CreateCliRoot(NavDaemonClient client)
    {
        var root = new RootCommand("csnav test root");
        var globals = new GlobalOptions();
        globals.AddTo(root);
        root.Add(ReferencesCommand.Create(globals, client));
        root.Add(DefinitionCommand.Create(globals, client));
        root.Add(ImplementationsCommand.Create(globals, client));
        root.Add(SearchCommand.Create(globals, client));
        root.Add(InspectCommand.Create(globals, client));
        root.Add(DecompileCommand.Create(globals, client));
        root.Add(ServerCommand.Create(globals, client));
        root.Add(SkillsCommand.Create());
        return root;
    }

    [Fact]
    public void SkillMarkdown_AllDocumentedCliExamples_ParseWithoutErrors_AndSyncIsNotExposed()
    {
        var skillContent = SkillsCommand.SkillMarkdownContent;
        skillContent.Should().NotBeNullOrWhiteSpace();

        // Ensure key sections from agent feedback exist in SKILL.md
        skillContent.Should().Contain("Daemon Failure & Recovery");
        skillContent.Should().Contain("Pagination & JSON Output Contract");
        skillContent.Should().Contain("Stream Hygiene");
        skillContent.Should().Contain("Common Pitfalls & Gotchas");

        using var client = new NavDaemonClient();
        var root = CreateCliRoot(client);

        // Verify 'sync' subcommand does not exist
        var syncParse = root.Parse("sync");
        syncParse.Errors.Should().NotBeEmpty("sync subcommand was removed and must not be exposed");

        // Extract every positive command example line starting with 'csnav ' inside ```bash code blocks
        var bashBlocks = Regex.Matches(skillContent, @"```bash\r?\n(.*?)```", RegexOptions.Singleline);
        bashBlocks.Should().NotBeEmpty();

        var parsedCount = 0;
        foreach (Match block in bashBlocks)
        {
            var lines = block.Groups[1].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith('#') || !line.StartsWith("csnav ", StringComparison.Ordinal))
                {
                    continue;
                }

                var argsAfterCsnav = line["csnav ".Length..].Trim();
                var parseResult = root.Parse(argsAfterCsnav);
                parseResult.Errors.Should().BeEmpty($"documented command `{line}` in SKILL.md must parse cleanly");
                parsedCount++;
            }
        }

        parsedCount.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Search_SupportsAllDocumentedKinds_CaseInsensitivity_CommaSeparated_Doc_AndPagination()
    {
        var handler = new SearchApiHandler(_workspace, _apiSearchEngine);

        // 1. Verify every documented --kind value works and is case-insensitive
        var kindExpectations = new (string KindFilter, string ExpectedSymbolSubstring)[]
        {
            ("all", "OrderService"),
            ("type", "OrderService"),
            ("member", "ProcessOrder"),
            ("class", "OrderService"),
            ("interface", "IOrderRepository"),
            ("Interface", "IOrderRepository"), // Case-insensitive check
            ("struct", "Money"),
            ("record", "OrderRecord"),
            ("enum", "OrderStatus"),
            ("delegate", "OrderAuditHandler"),
            ("method", "ProcessOrder"),
            ("constructor", "OrderService"),
            ("property", "ProcessedCount"),
            ("field", "_repository"),
            ("event", "OrderProcessed"),
            ("class,interface", "IOrderRepository"),
            ("Struct,Enum", "Money")
        };

        foreach (var (kindFilter, expectedSymbol) in kindExpectations)
        {
            var res = await handler.HandleAsync(new SearchApiRequest(
                Pattern: "*",
                Scope: "solution",
                Kind: kindFilter,
                Limit: 50));

            res.Results.Should().Contain(
                r => r.Name.Contains(expectedSymbol, StringComparison.OrdinalIgnoreCase),
                $"--kind '{kindFilter}' should match symbol containing '{expectedSymbol}'");
        }

        // 2. Verify pagination contract: totalMatches, offset, limit, truncated, nextOffset, indexState, snapshotVersion
        var page1 = await handler.HandleAsync(new SearchApiRequest(
            Pattern: "*",
            Scope: "solution",
            Limit: 3,
            Offset: 0));

        page1.TotalMatches.Should().BeGreaterThan(3);
        page1.Offset.Should().Be(0);
        page1.Limit.Should().Be(3);
        page1.Truncated.Should().BeTrue();
        page1.NextOffset.Should().Be(3);
        page1.IndexState.Should().Be("ready");
        page1.SnapshotVersion.Should().BeGreaterThan(0);

        // Deterministic paging loop using NextOffset
        var collected = new List<ApiSearchResultDto>(page1.Results);
        var currentPage = page1;
        while (currentPage.Truncated && currentPage.NextOffset.HasValue)
        {
            currentPage = await handler.HandleAsync(new SearchApiRequest(
                Pattern: "*",
                Scope: "solution",
                Limit: 3,
                Offset: currentPage.NextOffset.Value));
            collected.AddRange(currentPage.Results);
        }

        currentPage.Truncated.Should().BeFalse();
        currentPage.NextOffset.Should().BeNull();
        collected.Count.Should().Be(page1.TotalMatches);

        // 3. Verify --doc search
        var docRes = await handler.HandleAsync(new SearchApiRequest(
            Doc: "calculates sha256",
            Scope: "solution"));
        docRes.Results.Should().ContainSingle(r => r.Name.Contains("ProcessOrder"));

        // 4. Verify --access internal / all
        var internalRes = await handler.HandleAsync(new SearchApiRequest(
            Pattern: "InternalMaintenance",
            Scope: "solution",
            Access: "all"));
        internalRes.Results.Should().ContainSingle(r => r.Name.Contains("InternalMaintenance"));
    }

    [Fact]
    public async Task DocIdPrefixes_T_M_P_F_E_WorkAcross_Definition_References_Implementations_Inspect_And_Decompile()
    {
        var defHandler = new FindDefinitionHandler(_workspace, _referenceFinder);
        var refHandler = new FindReferencesHandler(_workspace, _referenceFinder);
        var implHandler = new FindImplementationsHandler(_workspace, _referenceFinder);
        var exploreHandler = new ExploreTypeHandler(_workspace, _typeExplorer);
        var decompileHandler = new DecompileMemberHandler(_workspace, _decompiler);

        // 1. Type Doc-ID (T:)
        var typeDef = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "T:SmokeApp.IOrderRepository"));
        typeDef.TotalDefinitions.Should().Be(1);
        typeDef.Target.DefinedIn.Should().EndWith("DomainModels.cs");

        var typeImpls = await implHandler.HandleAsync(new FindImplementationsRequest(Symbol: "T:SmokeApp.IOrderRepository", ExcludeTests: true));
        typeImpls.TotalImplementations.Should().Be(1);
        typeImpls.Implementations[0].ContainingType.Should().Be("SmokeApp.SqlOrderRepository");

        var typeRefs = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "T:SmokeApp.OrderRecord", ExcludeTests: true));
        typeRefs.TotalReferences.Should().BeGreaterThan(0);

        var typeInspect = await exploreHandler.HandleAsync(new ExploreTypeRequest(Type: "T:SmokeApp.OrderService", NoInherited: true));
        typeInspect.Type.FullName.Should().Be("SmokeApp.OrderService");

        // 2. Method Doc-ID (M:)
        var methodDef = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "M:SmokeApp.OrderService.ProcessOrder(System.Int32)"));
        methodDef.TotalDefinitions.Should().Be(1);
        methodDef.Definitions[0].SymbolId.Should().Be("M:SmokeApp.OrderService.ProcessOrder(System.Int32)");

        var methodRefs = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "M:SmokeApp.IOrderRepository.GetOrder(System.Int32)", ExcludeTests: true));
        methodRefs.TotalReferences.Should().Be(2); // 1 implementation in SqlOrderRepository + 1 call site in OrderService

        // 3. Property Doc-ID (P:)
        var propDef = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "P:SmokeApp.OrderService.ProcessedCount"));
        propDef.TotalDefinitions.Should().Be(1);

        var propRefs = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "P:SmokeApp.OrderService.ProcessedCount"));
        propRefs.TotalReferences.Should().Be(1);

        // 4. Field Doc-ID (F:)
        var fieldDef = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "F:SmokeApp.OrderService._repository"));
        fieldDef.TotalDefinitions.Should().Be(1);

        var fieldRefs = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "F:SmokeApp.OrderService._repository"));
        fieldRefs.TotalReferences.Should().BeGreaterThanOrEqualTo(3);

        // 5. Event Doc-ID (E:)
        var eventDef = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "E:SmokeApp.OrderService.OrderProcessed"));
        eventDef.TotalDefinitions.Should().Be(1);

        var eventRefs = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "E:SmokeApp.OrderService.OrderProcessed"));
        eventRefs.TotalReferences.Should().Be(1);

        // 6. Decompile with T: and M: Doc-IDs
        var decompileType = await decompileHandler.HandleAsync(new DecompileRequest(Symbol: "T:System.Uri"));
        decompileType.Source.Should().Contain("class Uri");

        var decompileMember = await decompileHandler.HandleAsync(new DecompileRequest(Symbol: "P:System.Uri.IsLoopback"));
        decompileMember.Source.Should().Contain("IsLoopback");
    }

    [Fact]
    public async Task ExcludeTests_And_IncludeGenerated_And_AccessAll_And_ChainingWorkflow_WorkAsDocumented()
    {
        var searchHandler = new SearchApiHandler(_workspace, _apiSearchEngine);
        var defHandler = new FindDefinitionHandler(_workspace, _referenceFinder);
        var implHandler = new FindImplementationsHandler(_workspace, _referenceFinder);
        var refHandler = new FindReferencesHandler(_workspace, _referenceFinder);
        var exploreHandler = new ExploreTypeHandler(_workspace, _typeExplorer);

        // 1. Chaining workflow: search -> definition (via symbolId) -> inspect (via file + line) -> references
        var searchRes = await searchHandler.HandleAsync(new SearchApiRequest(Pattern: "OrderService", Scope: "solution", Kind: "class"));
        var foundService = searchRes.Results.Should().ContainSingle(r => r.Name == "SmokeApp.OrderService").Subject;
        foundService.SymbolId.Should().Be("T:SmokeApp.OrderService");
        foundService.File.Should().NotBeNullOrWhiteSpace();
        foundService.Line.Should().BeGreaterThan(0);

        var defRes = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: foundService.SymbolId));
        defRes.Definitions.Should().ContainSingle();
        var defItem = defRes.Definitions[0];
        defItem.File.Should().NotBeNullOrWhiteSpace();
        defItem.Line.Should().BeGreaterThan(0);

        var inspectAtLoc = await exploreHandler.HandleAsync(new ExploreTypeRequest(
            File: defItem.File,
            Line: defItem.Line,
            Column: defItem.Column ?? 1,
            NoInherited: true,
            Access: "all"));
        inspectAtLoc.Type.FullName.Should().Be("SmokeApp.OrderService");
        inspectAtLoc.Fields.Should().Contain(f => f.Name == "_secretCounter");
        inspectAtLoc.Methods.Should().Contain(m => m.Name == "InternalMaintenance");

        // 2. --exclude-tests on implementations and references
        var implAll = await implHandler.HandleAsync(new FindImplementationsRequest(Symbol: "T:SmokeApp.IOrderRepository", ExcludeTests: false));
        implAll.Implementations.Should().Contain(i => i.ContainingType == "SmokeApp.SqlOrderRepository");
        implAll.Implementations.Should().Contain(i => i.ContainingType == "SmokeApp.Tests.FakeOrderRepository");

        var implNoTests = await implHandler.HandleAsync(new FindImplementationsRequest(Symbol: "T:SmokeApp.IOrderRepository", ExcludeTests: true));
        implNoTests.Implementations.Should().ContainSingle(i => i.ContainingType == "SmokeApp.SqlOrderRepository");

        var refsAll = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "M:SmokeApp.OrderService.ProcessOrder(System.Int32)", ExcludeTests: false));
        refsAll.TotalReferences.Should().Be(1);

        var refsNoTests = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "M:SmokeApp.OrderService.ProcessOrder(System.Int32)", ExcludeTests: true));
        refsNoTests.TotalReferences.Should().Be(0);

        // 3. --include-generated on definition, implementations, and references
        var genDefExcluded = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "GeneratedService", IncludeGenerated: false));
        genDefExcluded.TotalDefinitions.Should().Be(0);

        var genDefIncluded = await defHandler.HandleAsync(new FindDefinitionRequest(Symbol: "GeneratedService", IncludeGenerated: true));
        genDefIncluded.TotalDefinitions.Should().Be(1);
        genDefIncluded.Definitions[0].IsGenerated.Should().BeTrue();

        var genImplExcluded = await implHandler.HandleAsync(new FindImplementationsRequest(Symbol: "IGeneratedContract", IncludeGenerated: false));
        genImplExcluded.TotalImplementations.Should().Be(0);

        var genImplIncluded = await implHandler.HandleAsync(new FindImplementationsRequest(Symbol: "IGeneratedContract", IncludeGenerated: true));
        genImplIncluded.TotalImplementations.Should().Be(1);
        genImplIncluded.Implementations[0].IsGenerated.Should().BeTrue();

        var refsWithoutGen = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "M:SmokeApp.SqlOrderRepository.GetOrder(System.Int32)", IncludeGenerated: false));
        refsWithoutGen.References.Should().NotContain(r => r.IsGenerated);

        var refsWithGen = await refHandler.HandleAsync(new FindReferencesRequest(Symbol: "M:SmokeApp.SqlOrderRepository.GetOrder(System.Int32)", IncludeGenerated: true));
        refsWithGen.References.Should().Contain(r => r.IsGenerated);
    }

    [Fact]
    public async Task UnhealthyWorkspace_ReportsUnhealthyState_And_DecompileStillWorksStandalone()
    {
        var brokenDir = Path.Combine(_tempDir, "broken");
        Directory.CreateDirectory(brokenDir);
        var brokenFile = Path.Combine(brokenDir, "Unsupported.txt");
        await File.WriteAllTextAsync(brokenFile, "invalid");

        using var unhealthyWorkspace = new RoslynSolutionWorkspace(brokenFile);
        var act = () => unhealthyWorkspace.InitializeAsync();
        await act.Should().ThrowAsync<NotSupportedException>();

        var status = await unhealthyWorkspace.GetStatusAsync();
        status.State.Should().Be("unhealthy");
        status.ProjectCount.Should().Be(0);

        // Verify decompile still works standalone even when workspace is unhealthy and has 0 projects
        var decompileHandler = new DecompileMemberHandler(unhealthyWorkspace, _decompiler);
        var decompiled = await decompileHandler.HandleAsync(new DecompileRequest(
            Type: "System.Uri",
            Member: "IsLoopback"));

        decompiled.Should().NotBeNull();
        decompiled.Type.Should().Be("System.Uri");
        decompiled.Source.Should().Contain("IsLoopback");
    }

    [Fact]
    public async Task EndToEnd_ServerLifecycle_Status_Reload_Stop_And_StreamHygiene()
    {
        using var client = new NavDaemonClient();
        var root = CreateCliRoot(client);

        async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(string[] args)
        {
            var origOut = Console.Out;
            var origErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = await root.Parse(args).InvokeAsync();
                return (exitCode, outWriter.ToString().Trim(), errWriter.ToString().Trim());
            }
            finally
            {
                Console.SetOut(origOut);
                Console.SetError(origErr);
            }
        }

        try
        {
            // 1. First query cold-starts daemon: stdout must be pure JSON (lifecycle logs on stderr only)
            var (searchExit, searchStdout, searchStderr) = await RunCliAsync(
                ["search", "*Repository", "--solution", _slnPath, "--scope", "solution", "--format", "json"]);

            searchExit.Should().Be(0, $"stderr was: {searchStderr}");
            searchStdout.Should().StartWith("{").And.EndWith("}");
            searchStdout.Should().NotContain("[csnav]");
            var parsedSearch = NavJsonSerializer.Deserialize<SearchApiResponse>(searchStdout);
            parsedSearch.Should().NotBeNull();
            parsedSearch!.Results.Should().Contain(r => r.Name.Contains("IOrderRepository"));

            // 2. server status (--format json)
            var (statusExit, statusStdout, _) = await RunCliAsync(
                ["server", "status", "--solution", _slnPath, "--format", "json"]);
            statusExit.Should().Be(0);
            var parsedStatus = NavJsonSerializer.Deserialize<WorkspaceStatusResponse>(statusStdout);
            parsedStatus.Should().NotBeNull();
            parsedStatus!.State.Should().Be("ready");
            parsedStatus.ProjectCount.Should().Be(2);

            // 3. server reload
            var (reloadExit, reloadStdout, _) = await RunCliAsync(
                ["server", "reload", "--solution", _slnPath]);
            reloadExit.Should().Be(0);
            reloadStdout.Should().Contain("reloaded successfully");

            // 4. server stop
            var (stopExit, stopStdout, _) = await RunCliAsync(
                ["server", "stop", "--solution", _slnPath]);
            stopExit.Should().Be(0);
            stopStdout.Should().Contain("Server stopped");

            // 5. Verify status after stop reports stopped
            var (stoppedStatusExit, stoppedStatusStdout, _) = await RunCliAsync(
                ["server", "status", "--solution", _slnPath, "--format", "json"]);
            stoppedStatusExit.Should().Be(0);
            stoppedStatusStdout.Should().Contain("\"status\":\"stopped\"");
        }
        finally
        {
            await client.StopDaemonAsync(_slnPath);
        }
    }
}
