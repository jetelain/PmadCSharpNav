using AwesomeAssertions;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Infrastructure.Adapters;
using Pmad.CSharpNav.Infrastructure.Watcher;
using Pmad.CSharpNav.Infrastructure.Workspace;

namespace Pmad.CSharpNav.IntegrationTests;

public sealed class EndToEndScenariosTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = null!;
    private string _csprojPath = null!;
    private RoslynSolutionWorkspace _workspace = null!;
    private RoslynReferenceFinder _referenceFinder = null!;
    private RoslynApiSearchEngine _apiSearchEngine = null!;
    private RoslynTypeExplorer _typeExplorer = null!;
    private ILSpyDecompiler _decompiler = null!;

    private string _orderServicePath = null!;
    private string _orderRepoPath = null!;
    private string _orderPath = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csnav_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _csprojPath = Path.Combine(_tempDir, "TestE2EApp.csproj");
        await File.WriteAllTextAsync(_csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);

        _orderPath = Path.Combine(_tempDir, "Order.cs");
        await File.WriteAllTextAsync(_orderPath, """
            namespace TestE2EApp;

            public class Order
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
            }
            """);

        _orderRepoPath = Path.Combine(_tempDir, "IOrderRepository.cs");
        await File.WriteAllTextAsync(_orderRepoPath, """
            namespace TestE2EApp;

            /// <summary>
            /// Data repository for managing orders.
            /// </summary>
            public interface IOrderRepository
            {
                Order? GetOrder(int id);
                void SaveOrder(Order order);
            }
            """);

        _orderServicePath = Path.Combine(_tempDir, "OrderService.cs");
        await File.WriteAllTextAsync(_orderServicePath, """
            namespace TestE2EApp;

            /// <summary>
            /// Service handling business transactions for orders.
            /// </summary>
            public class OrderService
            {
                private readonly IOrderRepository _repository;

                public OrderService(IOrderRepository repository)
                {
                    _repository = repository;
                }

                public Order? ProcessOrder(int id)
                {
                    var order = _repository.GetOrder(id);
                    if (order != null)
                    {
                        _repository.SaveOrder(order);
                    }
                    return order;
                }
            }
            """);

        _workspace = new RoslynSolutionWorkspace(_csprojPath);
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
        _workspace.Dispose();
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

    [Fact]
    public async Task Scenario1_FindReferences_Finds_Method_Usages_And_CallSites()
    {
        var handler = new FindReferencesHandler(_workspace, _referenceFinder);

        var request = new FindReferencesRequest(
            Symbol: "TestE2EApp.IOrderRepository.GetOrder",
            Limit: 10);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Target.Name.Should().Contain("GetOrder");
        response.TotalReferences.Should().Be(1);
        response.References[0].File.Should().EndWith("OrderService.cs");
        response.References[0].ContainingMember.Should().Be("ProcessOrder");
        response.References[0].Snippet.Should().Contain("_repository.GetOrder(id)");
    }

    [Fact]
    public async Task Scenario2_SearchApi_Finds_Declarations_And_DocSummaries()
    {
        var handler = new SearchApiHandler(_workspace, _apiSearchEngine);

        var request = new SearchApiRequest(
            Pattern: "*Repository*",
            Scope: "solution",
            Limit: 10);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Results.Should().ContainSingle(r => r.Name.Contains("IOrderRepository"));
        var match = response.Results.First(r => r.Name.Contains("IOrderRepository"));
        match.Kind.Should().Be("Interface");
        match.Summary.Should().Contain("Data repository for managing orders.");
    }

    [Fact]
    public async Task Scenario3_ExploreType_Inspects_Constructors_Methods_And_Hierarchy()
    {
        var handler = new ExploreTypeHandler(_workspace, _typeExplorer);

        var request = new ExploreTypeRequest(
            Type: "TestE2EApp.OrderService",
            NoInherited: true);

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Type.Name.Should().Be("OrderService");
        response.Type.Kind.Should().Be("Class");
        response.Type.Summary.Should().Contain("Service handling business transactions for orders.");

        response.Constructors.Should().NotBeNull();
        response.Constructors!.Should().ContainSingle(c => c.Signature.Contains("OrderService(IOrderRepository repository)"));

        response.Methods.Should().NotBeNull();
        response.Methods!.Should().ContainSingle(m => m.Name == "ProcessOrder");
    }

    [Fact]
    public async Task Scenario4_Decompile_Decompiles_External_BCL_Symbol()
    {
        var handler = new DecompileMemberHandler(_workspace, _decompiler);

        var request = new DecompileRequest(
            Assembly: "System.Private.CoreLib",
            Type: "System.Uri",
            Member: "IsLoopback");

        var response = await handler.HandleAsync(request);

        response.Should().NotBeNull();
        response.Type.Should().Be("System.Uri");
        response.Member.Should().Be("IsLoopback");
        response.Source.Should().NotBeNullOrWhiteSpace();
        response.Source.Should().Contain("IsLoopback");
    }

    [Fact]
    public async Task Scenario5_Search_EditCode_And_SearchAgain_Reflects_All_Changes()
    {
        var searchHandler = new SearchApiHandler(_workspace, _apiSearchEngine);
        var refHandler = new FindReferencesHandler(_workspace, _referenceFinder);
        var exploreHandler = new ExploreTypeHandler(_workspace, _typeExplorer);

        // --- Step 1: Initial search before edits ---
        // 1a. Search for PaymentProcessor -> must not exist
        var searchBefore = await searchHandler.HandleAsync(new SearchApiRequest(
            Pattern: "PaymentProcessor",
            Scope: "solution"));
        searchBefore.Results.Should().BeEmpty("PaymentProcessor has not been created yet");

        // 1b. Search for references to SaveOrder -> must be exactly 1
        var refsBefore = await refHandler.HandleAsync(new FindReferencesRequest(
            Symbol: "TestE2EApp.IOrderRepository.SaveOrder"));
        refsBefore.TotalReferences.Should().Be(1, "SaveOrder is only called once in ProcessOrder");

        // 1c. Explore OrderService -> should have exactly 1 method (ProcessOrder)
        var exploreBefore = await exploreHandler.HandleAsync(new ExploreTypeRequest(
            Type: "TestE2EApp.OrderService",
            NoInherited: true));
        exploreBefore.Methods.Should().ContainSingle(m => m.Name == "ProcessOrder");
        exploreBefore.Methods.Should().NotContain(m => m.Name == "CancelOrder");

        // --- Step 2: Edit Code (Add new file & modify existing file) ---
        // 2a. Create new file PaymentProcessor.cs
        var paymentProcessorPath = Path.Combine(_tempDir, "PaymentProcessor.cs");
        var paymentContent = """
            namespace TestE2EApp;

            /// <summary>
            /// Handles credit card payment authorization.
            /// </summary>
            public class PaymentProcessor
            {
                public bool AuthorizePayment(int orderId, decimal amount) => true;
            }
            """;
        await File.WriteAllTextAsync(paymentProcessorPath, paymentContent);

        // 2b. Modify existing OrderService.cs to add CancelOrder method calling SaveOrder
        var updatedOrderServiceContent = """
            namespace TestE2EApp;

            /// <summary>
            /// Service handling business transactions for orders.
            /// </summary>
            public class OrderService
            {
                private readonly IOrderRepository _repository;

                public OrderService(IOrderRepository repository)
                {
                    _repository = repository;
                }

                public Order? ProcessOrder(int id)
                {
                    var order = _repository.GetOrder(id);
                    if (order != null)
                    {
                        _repository.SaveOrder(order);
                    }
                    return order;
                }

                public void CancelOrder(int id)
                {
                    var order = _repository.GetOrder(id);
                    if (order != null)
                    {
                        order.Total = 0;
                        _repository.SaveOrder(order);
                    }
                }
            }
            """;
        await File.WriteAllTextAsync(_orderServicePath, updatedOrderServiceContent);

        // --- Step 3: Synchronize workspace ---
        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(paymentProcessorPath, paymentContent));
        await syncHandler.HandleAsync(new SyncRequest(_orderServicePath, updatedOrderServiceContent));

        // --- Step 4: Re-query and verify that the edited code changes are reflected ---
        // 4a. Re-search for PaymentProcessor -> NOW FOUND!
        var searchAfter = await searchHandler.HandleAsync(new SearchApiRequest(
            Pattern: "PaymentProcessor",
            Scope: "solution"));
        searchAfter.Results.Should().ContainSingle(r => r.Name.Contains("PaymentProcessor"));
        searchAfter.Results[0].Summary.Should().Contain("Handles credit card payment authorization.");

        // 4b. Re-search for new method AuthorizePayment
        var searchMethodAfter = await searchHandler.HandleAsync(new SearchApiRequest(
            Pattern: "AuthorizePayment",
            Scope: "solution"));
        searchMethodAfter.Results.Should().ContainSingle(r => r.Name.Contains("AuthorizePayment"));

        // 4c. Re-explore OrderService -> NOW contains CancelOrder as well!
        var exploreAfter = await exploreHandler.HandleAsync(new ExploreTypeRequest(
            Type: "TestE2EApp.OrderService",
            NoInherited: true));
        exploreAfter.Methods.Should().HaveCount(2);
        exploreAfter.Methods.Should().Contain(m => m.Name == "ProcessOrder");
        exploreAfter.Methods.Should().Contain(m => m.Name == "CancelOrder");

        // 4d. Re-search for references to SaveOrder -> NOW 2 REFERENCES FOUND!
        var refsAfter = await refHandler.HandleAsync(new FindReferencesRequest(
            Symbol: "TestE2EApp.IOrderRepository.SaveOrder"));
        refsAfter.TotalReferences.Should().Be(2, "SaveOrder is now called in both ProcessOrder and CancelOrder");
        refsAfter.References.Should().Contain(r => r.ContainingMember == "ProcessOrder");
        refsAfter.References.Should().Contain(r => r.ContainingMember == "CancelOrder");
    }

    [Fact]
    public async Task Scenario6_FileWatcher_Debounced_Live_Edit_Sync()
    {
        var watcher = new DebouncedFileWatcher(_tempDir, _workspace, debounceMilliseconds: 50);
        watcher.Start();

        try
        {
            var searchHandler = new SearchApiHandler(_workspace, _apiSearchEngine);

            // Initially not present
            var searchBefore = await searchHandler.HandleAsync(new SearchApiRequest(
                Pattern: "InvoiceGenerator",
                Scope: "solution"));
            searchBefore.Results.Should().BeEmpty();

            // Write new file to disk
            var invoicePath = Path.Combine(_tempDir, "InvoiceGenerator.cs");
            await File.WriteAllTextAsync(invoicePath, """
                namespace TestE2EApp;

                /// <summary>
                /// Generates customer invoices.
                /// </summary>
                public class InvoiceGenerator
                {
                    public string GeneratePdf(int id) => "invoice.pdf";
                }
                """);

            // Wait for file system event and debounced synchronization
            await Task.Delay(500);

            // New search must include the newly added file
            var searchAfter = await searchHandler.HandleAsync(new SearchApiRequest(
                Pattern: "InvoiceGenerator",
                Scope: "solution"));

            searchAfter.Results.Should().ContainSingle(r => r.Name.Contains("InvoiceGenerator"));
            searchAfter.Results[0].Summary.Should().Contain("Generates customer invoices.");
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Scenario7_Skills_Command_Installs_Skill_File()
    {
        var targetSkillDir = Path.Combine(_tempDir, "custom_skills", "csharp-nav");
        var cmd = Pmad.CSharpNav.Cli.Commands.SkillsCommand.Create();

        var exitCode = await cmd.Parse($"install --dir \"{targetSkillDir}\"").InvokeAsync();
        exitCode.Should().Be(0);

        var skillFile = Path.Combine(targetSkillDir, "SKILL.md");
        File.Exists(skillFile).Should().BeTrue();

        var content = await File.ReadAllTextAsync(skillFile);
        content.Should().Contain("name: csharp-nav");
        content.Should().Contain("csnav references");
        content.Should().Contain("csnav refs");
        content.Should().Contain("csnav search");
        content.Should().Contain("csnav api");
        content.Should().Contain("csnav inspect");
        content.Should().Contain("csnav explore");
        content.Should().Contain("csnav decompile");
    }

    [Fact]
    public async Task Scenario8_FindReferences_Constructor_And_Invocation_Classification()
    {
        var consumerPath = Path.Combine(_tempDir, "OrderConsumer.cs");
        var consumerContent = """
            namespace TestE2EApp;

            public class OrderConsumer
            {
                public void Run(IOrderRepository repo)
                {
                    var service = new OrderService(repo);
                    service.ProcessOrder(1);
                }
            }
            """;
        await File.WriteAllTextAsync(consumerPath, consumerContent);

        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(consumerPath, consumerContent));

        var refHandler = new FindReferencesHandler(_workspace, _referenceFinder);

        // Find constructor references
        var ctorRefs = await refHandler.HandleAsync(new FindReferencesRequest(
            Symbol: "TestE2EApp.OrderService..ctor"));

        ctorRefs.Should().NotBeNull();
        ctorRefs.Target.Kind.Should().Be("Constructor");
        ctorRefs.TotalReferences.Should().Be(1);
        ctorRefs.References[0].File.Should().EndWith("OrderConsumer.cs");
        ctorRefs.References[0].Kind.Should().Be("Instantiation");
        ctorRefs.References[0].Snippet.Should().Contain("new OrderService(repo)");

        // Find method references and verify Invocation classification
        var methodRefs = await refHandler.HandleAsync(new FindReferencesRequest(
            Symbol: "TestE2EApp.OrderService.ProcessOrder"));

        methodRefs.Should().NotBeNull();
        methodRefs.TotalReferences.Should().Be(1);
        methodRefs.References[0].Kind.Should().Be("Invocation");
        methodRefs.References[0].Snippet.Should().Contain("service.ProcessOrder(1)");
    }

    [Fact]
    public async Task Scenario9_ExploreType_PropertyAccessors_And_Modifiers()
    {
        var path = Path.Combine(_tempDir, "AdvancedOrder.cs");
        var content = """
            namespace TestE2EApp;

            /// <summary>
            /// Advanced order entity.
            /// </summary>
            /// <remarks>
            /// Used for complex order workflows.
            /// </remarks>
            public class AdvancedOrder
            {
                public readonly int ConstantId = 42;

                public string Title { get; private set; } = string.Empty;

                /// <summary>
                /// Calculates the discount asynchronously.
                /// </summary>
                /// <param name="percent">The discount percent.</param>
                /// <returns>The calculated discount value.</returns>
                public async Task<decimal> CalculateDiscountAsync(decimal percent)
                {
                    await Task.Yield();
                    return percent * 10m;
                }
            }
            """;
        await File.WriteAllTextAsync(path, content);

        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(path, content));

        var exploreHandler = new ExploreTypeHandler(_workspace, _typeExplorer);
        var response = await exploreHandler.HandleAsync(new ExploreTypeRequest(
            Type: "TestE2EApp.AdvancedOrder",
            NoInherited: true));

        response.Should().NotBeNull();
        response.Type.Summary.Should().Contain("Advanced order entity.");

        // Check property accessor and field modifier
        var prop = response.Properties?.FirstOrDefault(p => p.Name == "Title");
        prop.Should().NotBeNull();
        prop!.Accessors.Should().Be("get; private set;");

        var field = response.Fields?.FirstOrDefault(f => f.Name == "ConstantId");
        field.Should().NotBeNull();
        field!.Modifiers.Should().Contain("readonly");

        // Check method async modifier and summary
        var method = response.Methods?.FirstOrDefault(m => m.Name == "CalculateDiscountAsync");
        method.Should().NotBeNull();
        method!.Modifiers.Should().Contain("async");
        method.Summary.Should().Contain("Calculates the discount asynchronously.");
    }

    [Fact]
    public async Task Scenario10_SearchApi_XmlDocumentation_FullText_Search()
    {
        var path = Path.Combine(_tempDir, "AdvancedOrderDoc.cs");
        var content = """
            namespace TestE2EApp;

            /// <summary>
            /// Advanced entity with remarks.
            /// </summary>
            /// <remarks>
            /// Used for complex order workflows.
            /// </remarks>
            public class AdvancedEntity
            {
                /// <summary>
                /// Calculates discount.
                /// </summary>
                /// <param name="rate">The custom discount rate.</param>
                public void ApplyRate(decimal rate) {}
            }
            """;
        await File.WriteAllTextAsync(path, content);

        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(path, content));

        var searchHandler = new SearchApiHandler(_workspace, _apiSearchEngine);

        // Search by doc text contained in remarks
        var response = await searchHandler.HandleAsync(new SearchApiRequest(
            Doc: "complex order workflows",
            Scope: "solution"));

        response.Should().NotBeNull();
        response.Results.Should().ContainSingle(r => r.Name.Contains("AdvancedEntity"));

        // Search by doc text in parameter description
        var paramSearch = await searchHandler.HandleAsync(new SearchApiRequest(
            Doc: "custom discount rate",
            Scope: "solution"));

        paramSearch.Should().NotBeNull();
        paramSearch.Results.Should().ContainSingle(r => r.Name.Contains("ApplyRate"));
    }

    [Fact]
    public async Task Scenario11_FindReferences_Qualified_Symbol_Avoids_False_Ambiguity()
    {
        var path = Path.Combine(_tempDir, "Workers.cs");
        var content = """
            namespace TestE2EApp;

            public class WorkerA
            {
                public void Execute() {}
            }

            public class WorkerB
            {
                public void Execute() {}
            }

            public class WorkerCaller
            {
                public void Run(WorkerA a, WorkerB b)
                {
                    a.Execute();
                    b.Execute();
                }
            }
            """;
        await File.WriteAllTextAsync(path, content);

        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(path, content));

        var refHandler = new FindReferencesHandler(_workspace, _referenceFinder);

        var response = await refHandler.HandleAsync(new FindReferencesRequest(
            Symbol: "TestE2EApp.WorkerA.Execute"));

        response.Should().NotBeNull();
        response.Target.ContainingType.Should().Be("TestE2EApp.WorkerA");
        response.TotalReferences.Should().Be(1);
        response.References[0].Snippet.Should().Contain("a.Execute()");
    }

    [Fact]
    public async Task Scenario7_FindDefinition_BySymbol_And_ByLocation()
    {
        var defHandler = new FindDefinitionHandler(_workspace, _referenceFinder);

        // 1. By symbol name
        var symbolRequest = new FindDefinitionRequest(Symbol: "TestE2EApp.IOrderRepository.GetOrder");
        var symbolResponse = await defHandler.HandleAsync(symbolRequest);

        symbolResponse.Should().NotBeNull();
        symbolResponse.Target.Name.Should().Contain("GetOrder");
        symbolResponse.TotalDefinitions.Should().Be(1);
        symbolResponse.Definitions[0].File.Should().EndWith("IOrderRepository.cs");
        symbolResponse.Definitions[0].Snippet.Should().Contain("Order? GetOrder(int id);");

        // 2. By source file location in OrderService.cs line 17 col 34 (where GetOrder is called)
        var locRequest = new FindDefinitionRequest(
            File: _orderServicePath,
            Line: 17,
            Column: 34);
        var locResponse = await defHandler.HandleAsync(locRequest);

        locResponse.Should().NotBeNull();
        locResponse.Target.Name.Should().Contain("GetOrder");
        locResponse.TotalDefinitions.Should().Be(1);
        locResponse.Definitions[0].File.Should().EndWith("IOrderRepository.cs");
        locResponse.Definitions[0].Snippet.Should().Contain("Order? GetOrder(int id);");
    }

    [Fact]
    public async Task Scenario8_FindImplementations_ForInterface_And_InterfaceMethod()
    {
        var repoImplPath = Path.Combine(_tempDir, "SqlOrderRepository.cs");
        var repoImplContent = """
            namespace TestE2EApp;

            public class SqlOrderRepository : IOrderRepository
            {
                public Order? GetOrder(int id) => null;
                public void SaveOrder(Order order) { }
            }
            """;
        await File.WriteAllTextAsync(repoImplPath, repoImplContent);
        var syncHandler = new SyncDocumentHandler(_workspace);
        await syncHandler.HandleAsync(new SyncRequest(repoImplPath, repoImplContent));

        var implHandler = new FindImplementationsHandler(_workspace, _referenceFinder);

        // 1. Implementations of interface type
        var typeResponse = await implHandler.HandleAsync(new FindImplementationsRequest(
            Symbol: "TestE2EApp.IOrderRepository"));

        typeResponse.Should().NotBeNull();
        typeResponse.Target.Name.Should().Contain("IOrderRepository");
        typeResponse.TotalImplementations.Should().Be(1);
        typeResponse.Implementations[0].File.Should().EndWith("SqlOrderRepository.cs");
        typeResponse.Implementations[0].ContainingType.Should().Be("TestE2EApp.SqlOrderRepository");
        typeResponse.Implementations[0].Kind.Should().Be("Implementation");

        // 2. Implementations of interface method
        var methodResponse = await implHandler.HandleAsync(new FindImplementationsRequest(
            Symbol: "TestE2EApp.IOrderRepository.GetOrder"));

        methodResponse.Should().NotBeNull();
        methodResponse.TotalImplementations.Should().Be(1);
        methodResponse.Implementations[0].File.Should().EndWith("SqlOrderRepository.cs");
        methodResponse.Implementations[0].ContainingType.Should().Be("TestE2EApp.SqlOrderRepository");
        methodResponse.Implementations[0].ContainingMember.Should().Be("GetOrder");

        // 3. Implementations by location on the call-site in OrderService.cs:17:34
        var locResponse = await implHandler.HandleAsync(new FindImplementationsRequest(
            File: _orderServicePath,
            Line: 17,
            Column: 34));

        locResponse.Should().NotBeNull();
        locResponse.TotalImplementations.Should().Be(1);
        locResponse.Implementations[0].File.Should().EndWith("SqlOrderRepository.cs");
        locResponse.Implementations[0].ContainingType.Should().Be("TestE2EApp.SqlOrderRepository");
    }
}

