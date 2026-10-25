using AwesomeAssertions;
using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Application.Tests.Fakes;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Domain;
using Xunit;

namespace Pmad.CSharpNav.Application.Tests;

public class SearchApiHandlerTests
{
    private readonly FakeSolutionWorkspace _workspace = new();
    private readonly FakeApiSearchEngine _searchEngine = new();

    [Fact]
    public async Task HandleAsync_ReturnsPagedResults_AndTruncationFlag()
    {
        var handler = new SearchApiHandler(_workspace, _searchEngine);

        _searchEngine.SearchResults.AddRange(new[]
        {
            new ApiDefinition("HashPassword", SymbolKind.Method, "string HashPassword(string pwd)", "Hasher", "AuthAsm", false),
            new ApiDefinition("ValidatePassword", SymbolKind.Method, "bool ValidatePassword(string pwd)", "Hasher", "AuthAsm", false),
            new ApiDefinition("GenerateSalt", SymbolKind.Method, "byte[] GenerateSalt()", "Hasher", "AuthAsm", false)
        });

        var response = await handler.HandleAsync(new SearchApiRequest(
            Pattern: "Hash*",
            Limit: 2,
            Offset: 0));

        response.TotalMatches.Should().Be(3);
        response.Results.Should().HaveCount(2);
        response.Truncated.Should().BeTrue();
        response.NextOffset.Should().Be(2);
    }
}

public class ExploreTypeHandlerTests
{
    private readonly FakeSolutionWorkspace _workspace = new();
    private readonly FakeTypeExplorer _explorer = new();

    [Fact]
    public async Task HandleAsync_GroupsMembersByKind()
    {
        var handler = new ExploreTypeHandler(_workspace, _explorer);

        _explorer.ExplorationResult = new TypeExplorationResult(
            Name: "OrderService",
            FullName: "Commerce.OrderService",
            Kind: SymbolKind.Class,
            Accessibility: Accessibility.Public,
            Modifiers: new[] { "sealed" },
            BaseType: "System.Object",
            Interfaces: new[] { "IOrderService" },
            Summary: "Handles orders.",
            Members: new[]
            {
                new TypeMemberInfo(".ctor", SymbolKind.Constructor, "public OrderService()", Accessibility.Public, Array.Empty<string>()),
                new TypeMemberInfo("PlaceOrder", SymbolKind.Method, "public void PlaceOrder()", Accessibility.Public, Array.Empty<string>(), ReturnType: "void"),
                new TypeMemberInfo("IsActive", SymbolKind.Property, "public bool IsActive { get; }", Accessibility.Public, Array.Empty<string>(), ReturnType: "bool")
            });

        var response = await handler.HandleAsync(new ExploreTypeRequest(Type: "OrderService"));

        response.Type.Name.Should().Be("OrderService");
        response.Constructors.Should().HaveCount(1);
        response.Methods.Should().HaveCount(1);
        response.Properties.Should().HaveCount(1);
        response.Fields.Should().BeNull();
        response.Events.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenTypeNotFound_ThrowsNavException()
    {
        var handler = new ExploreTypeHandler(_workspace, _explorer);
        _explorer.ExplorationResult = null;

        var act = () => handler.HandleAsync(new ExploreTypeRequest(Type: "NonExistent"));

        var ex = await act.Should().ThrowAsync<NavException>();
        ex.Which.Code.Should().Be("TYPE_NOT_FOUND");
    }
}
