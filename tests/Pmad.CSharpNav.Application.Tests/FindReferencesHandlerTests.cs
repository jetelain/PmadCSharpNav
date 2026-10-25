using AwesomeAssertions;
using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Application.Tests.Fakes;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Domain;
using Xunit;

namespace Pmad.CSharpNav.Application.Tests;

public class FindReferencesHandlerTests
{
    private readonly FakeSolutionWorkspace _workspace = new();
    private readonly FakeReferenceFinder _finder = new();

    [Fact]
    public async Task HandleAsync_WhenSymbolIsAmbiguous_ThrowsNavExceptionWithCandidates()
    {
        var handler = new FindReferencesHandler(_workspace, _finder);

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm1", "Ns", "Type1", "void Process()", "sym-1"),
            "src/Type1.cs",
            10));

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm2", "Ns", "Type2", "void Process()", "sym-2"),
            "src/Type2.cs",
            20));

        var act = () => handler.HandleAsync(new FindReferencesRequest(Symbol: "Process"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("AMBIGUOUS_SYMBOL");
        ex.Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_WhenSymbolNotFound_ThrowsNavException()
    {
        var handler = new FindReferencesHandler(_workspace, _finder);

        var act = () => handler.HandleAsync(new FindReferencesRequest(Symbol: "UnknownSymbol"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task HandleAsync_WithPagination_AppliesLimitAndOffset()
    {
        var handler = new FindReferencesHandler(_workspace, _finder);

        var targetSym = new SymbolIdentifier("Asm", "Ns", "OrderService", "void PlaceOrder()", "sym-1");
        _finder.NameResolutionResults.Add(new SymbolResolutionResult(targetSym, "src/OrderService.cs", 42));

        for (int i = 1; i <= 10; i++)
        {
            _finder.ReferencesToReturn.Add(new ReferenceLocation(
                File: $"src/File{i}.cs",
                Line: i * 10,
                Column: 5,
                Snippet: $"call{i}();",
                ContainingMember: $"Member{i}",
                Kind: ReferenceKind.Invocation));
        }

        var response = await handler.HandleAsync(new FindReferencesRequest(
            Symbol: "OrderService.PlaceOrder",
            Offset: 3,
            Limit: 4));

        response.TotalReferences.Should().Be(10);
        response.Offset.Should().Be(3);
        response.Limit.Should().Be(4);
        response.Truncated.Should().BeTrue();
        response.NextOffset.Should().Be(7);
        response.References.Should().HaveCount(4);
        response.References[0].File.Should().Be("src/File4.cs");
    }
}
