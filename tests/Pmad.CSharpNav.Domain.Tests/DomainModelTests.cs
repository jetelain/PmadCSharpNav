using AwesomeAssertions;
using Pmad.CSharpNav.Domain;
using Xunit;

namespace Pmad.CSharpNav.Domain.Tests;

public class DomainModelTests
{
    [Fact]
    public void SymbolIdentifier_RecordEquality_WorksCorrectly()
    {
        var id1 = new SymbolIdentifier("MyAssembly", "MyNamespace", "MyClass", "void DoWork()", "sym-1");
        var id2 = new SymbolIdentifier("MyAssembly", "MyNamespace", "MyClass", "void DoWork()", "sym-1");
        var id3 = new SymbolIdentifier("MyAssembly", "MyNamespace", "MyClass", "int Count()", "sym-2");

        id1.Should().Be(id2);
        id1.Should().NotBe(id3);
    }

    [Fact]
    public void ReferenceLocation_Properties_AreRetained()
    {
        var location = new ReferenceLocation(
            File: "src/Service.cs",
            Line: 42,
            Column: 15,
            Snippet: "var x = new Service();",
            ContainingMember: "Controller.Action",
            Kind: ReferenceKind.Instantiation,
            IsGenerated: false);

        location.File.Should().Be("src/Service.cs");
        location.Line.Should().Be(42);
        location.Column.Should().Be(15);
        location.Kind.Should().Be(ReferenceKind.Instantiation);
        location.IsGenerated.Should().BeFalse();
    }

    [Fact]
    public void ApiDefinition_Initialization_Succeeds()
    {
        var api = new ApiDefinition(
            Name: "OrderService",
            Kind: SymbolKind.Class,
            Signature: "public class OrderService",
            ContainingType: null,
            Assembly: "MyCommerce",
            IsExternal: false,
            Summary: "Manages orders");

        api.Name.Should().Be("OrderService");
        api.Kind.Should().Be(SymbolKind.Class);
        api.IsExternal.Should().BeFalse();
        api.Summary.Should().Be("Manages orders");
    }
}
