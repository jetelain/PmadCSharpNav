using AwesomeAssertions;
using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Application.Tests.Fakes;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Domain;
using Xunit;

namespace Pmad.CSharpNav.Application.Tests;

public class FindImplementationsHandlerTests
{
    private readonly FakeSolutionWorkspace _workspace = new();
    private readonly FakeImplementationFinder _finder = new();

    [Fact]
    public async Task HandleAsync_WhenSymbolIsAmbiguous_ThrowsNavExceptionWithCandidates()
    {
        var handler = new FindImplementationsHandler(_workspace, _finder);

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm1", "Ns", "IService", null, "sym-1"),
            "src/IService1.cs",
            10));

        _finder.NameResolutionResults.Add(new SymbolResolutionResult(
            new SymbolIdentifier("Asm2", "Ns", "IService", null, "sym-2"),
            "src/IService2.cs",
            20));

        var act = () => handler.HandleAsync(new FindImplementationsRequest(Symbol: "IService"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("AMBIGUOUS_SYMBOL");
        ex.Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_WhenSymbolNotFound_ThrowsNavException()
    {
        var handler = new FindImplementationsHandler(_workspace, _finder);

        var act = () => handler.HandleAsync(new FindImplementationsRequest(Symbol: "UnknownInterface"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task HandleAsync_ReturnsImplementationsAndOverrides()
    {
        var handler = new FindImplementationsHandler(_workspace, _finder);

        var targetSym = new SymbolIdentifier("Asm", "Ns", "IOrderRepository", null, "sym-1");
        _finder.NameResolutionResults.Add(new SymbolResolutionResult(targetSym, "src/IOrderRepository.cs", 5, SymbolKind.Interface));

        _finder.ImplementationsToReturn.Add(new ImplementationLocation(
            File: "src/SqlOrderRepository.cs",
            Line: 10,
            Column: 14,
            Snippet: "public class SqlOrderRepository : IOrderRepository",
            ContainingType: "Ns.SqlOrderRepository",
            Kind: "Implementation"));

        _finder.ImplementationsToReturn.Add(new ImplementationLocation(
            File: "src/InMemoryOrderRepository.cs",
            Line: 12,
            Column: 14,
            Snippet: "public class InMemoryOrderRepository : IOrderRepository",
            ContainingType: "Ns.InMemoryOrderRepository",
            Kind: "Implementation"));

        var response = await handler.HandleAsync(new FindImplementationsRequest(Symbol: "IOrderRepository"));

        response.Should().NotBeNull();
        response.Target.Kind.Should().Be("Interface");
        response.TotalImplementations.Should().Be(2);
        response.Implementations.Should().HaveCount(2);
        response.Implementations[0].ContainingType.Should().Be("Ns.SqlOrderRepository");
        response.Implementations[1].ContainingType.Should().Be("Ns.InMemoryOrderRepository");
    }

    [Fact]
    public async Task HandleAsync_WithPagination_AppliesLimitAndOffset()
    {
        var handler = new FindImplementationsHandler(_workspace, _finder);

        var targetSym = new SymbolIdentifier("Asm", "Ns", "IPlugin", null, "sym-1");
        _finder.NameResolutionResults.Add(new SymbolResolutionResult(targetSym, "src/IPlugin.cs", 5, SymbolKind.Interface));

        for (int i = 1; i <= 5; i++)
        {
            _finder.ImplementationsToReturn.Add(new ImplementationLocation(
                File: $"src/Plugin{i}.cs",
                Line: 10,
                Column: 14,
                Snippet: $"public class Plugin{i} : IPlugin",
                ContainingType: $"Ns.Plugin{i}"));
        }

        var response = await handler.HandleAsync(new FindImplementationsRequest(
            Symbol: "IPlugin",
            Offset: 2,
            Limit: 2));

        response.TotalImplementations.Should().Be(5);
        response.Offset.Should().Be(2);
        response.Limit.Should().Be(2);
        response.Truncated.Should().BeTrue();
        response.NextOffset.Should().Be(4);
        response.Implementations.Should().HaveCount(2);
        response.Implementations[0].File.Should().Be("src/Plugin3.cs");
    }
}
