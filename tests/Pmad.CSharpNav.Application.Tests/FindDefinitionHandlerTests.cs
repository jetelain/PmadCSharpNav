using AwesomeAssertions;
using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Application.Tests.Fakes;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Domain;
using Xunit;

namespace Pmad.CSharpNav.Application.Tests;

public class FindDefinitionHandlerTests
{
    private readonly FakeSolutionWorkspace _workspace = new();
    private readonly FakeDefinitionFinder _finder = new();

    [Fact]
    public async Task HandleAsync_WhenSymbolIsAmbiguous_ThrowsNavExceptionWithCandidates()
    {
        var handler = new FindDefinitionHandler(_workspace, _finder);

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm1", "Ns", "Type1", "void Process()", "sym-1"),
            "src/Type1.cs",
            10));

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm2", "Ns", "Type2", "void Process()", "sym-2"),
            "src/Type2.cs",
            20));

        var act = () => handler.HandleAsync(new FindDefinitionRequest(Symbol: "Process"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("AMBIGUOUS_SYMBOL");
        ex.Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_WhenSymbolNotFound_ThrowsNavException()
    {
        var handler = new FindDefinitionHandler(_workspace, _finder);

        var act = () => handler.HandleAsync(new FindDefinitionRequest(Symbol: "UnknownSymbol"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task HandleAsync_BySourceLocation_ReturnsDefinitions()
    {
        var handler = new FindDefinitionHandler(_workspace, _finder);

        var targetSym = new SymbolIdentifier("Asm", "Ns", "OrderService", "void ProcessOrder()", "sym-1");
        _finder.LocationResolutionResult = new SymbolResolutionResult(targetSym, "src/OrderService.cs", 42, SymbolKind.Method);

        _finder.DefinitionsToReturn.Add(new DefinitionLocation(
            File: "src/OrderService.cs",
            Line: 42,
            Column: 14,
            Snippet: "public void ProcessOrder()",
            ContainingMember: "ProcessOrder",
            ContainingType: "Ns.OrderService",
            Kind: SymbolKind.Method));

        var response = await handler.HandleAsync(new FindDefinitionRequest(
            File: "src/Caller.cs",
            Line: 10,
            Column: 5));

        response.Should().NotBeNull();
        response.Target.Name.Should().Be("void ProcessOrder()");
        response.TotalDefinitions.Should().Be(1);
        response.Definitions[0].File.Should().Be("src/OrderService.cs");
        response.Definitions[0].Line.Should().Be(42);
        response.Definitions[0].Column.Should().Be(14);
        response.Definitions[0].Snippet.Should().Contain("ProcessOrder");
    }

    [Fact]
    public async Task HandleAsync_WhenMetadataDefinition_ReturnsMetadataFlag()
    {
        var handler = new FindDefinitionHandler(_workspace, _finder);

        var targetSym = new SymbolIdentifier("System.Runtime", "System", "String", null, "T:System.String");
        _finder.NameResolutionResults.Add(new SymbolResolutionResult(targetSym, null, null, SymbolKind.Class));

        _finder.DefinitionsToReturn.Add(new DefinitionLocation(
            File: null,
            Line: null,
            Column: null,
            AssemblyName: "System.Private.CoreLib",
            SymbolId: "T:System.String",
            Kind: SymbolKind.Class,
            IsMetadata: true));

        var response = await handler.HandleAsync(new FindDefinitionRequest(Symbol: "System.String"));

        response.TotalDefinitions.Should().Be(1);
        response.Definitions[0].IsMetadata.Should().BeTrue();
        response.Definitions[0].AssemblyName.Should().Be("System.Private.CoreLib");
        response.Definitions[0].SymbolId.Should().Be("T:System.String");
    }
}
