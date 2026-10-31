using AwesomeAssertions;
using Pmad.CSharpNav.Contracts;
using Xunit;

namespace Pmad.CSharpNav.Domain.Tests;

public class ContractsSerializationTests
{
    [Fact]
    public void FindReferencesResponse_SerializesToCompactJson_WithoutNulls()
    {
        var response = new FindReferencesResponse(
            Target: new ReferenceTargetDto(
                Name: "PlaceOrder",
                Kind: "Method",
                ContainingType: "OrderService",
                DefinedIn: "src/OrderService.cs",
                Line: 42),
            TotalReferences: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            References: new[]
            {
                new ReferenceItemDto(
                    File: "src/Controller.cs",
                    Line: 28,
                    Column: 32,
                    Kind: "Invocation",
                    ContainingMember: "Controller.CreateOrder",
                    IsGenerated: false,
                    Snippet: "service.PlaceOrder(dto);")
            });

        var json = NavJsonSerializer.Serialize(response);

        // Verification of LLM token efficiency: camelCase, no null fields
        json.Should().NotContain("nextOffset");
        json.Should().NotContain("snapshotVersion");
        json.Should().Contain("\"name\":\"PlaceOrder\"");
        json.Should().Contain("\"kind\":\"Method\"");
        json.Should().Contain("\"totalReferences\":1");
        json.Should().Contain("\"truncated\":false");

        // Round-trip deserialization
        var roundTripped = NavJsonSerializer.Deserialize<FindReferencesResponse>(json);
        roundTripped.Should().NotBeNull();
        roundTripped!.Target.Name.Should().Be("PlaceOrder");
        roundTripped.References.Should().HaveCount(1);
        roundTripped.References[0].Kind.Should().Be("Invocation");
    }

    [Fact]
    public void SearchApiResponse_RoundTrips_Correctly()
    {
        var response = new SearchApiResponse(
            Query: "hash password",
            Scope: "all",
            TotalMatches: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            Results: new[]
            {
                new ApiSearchResultDto(
                    Name: "PasswordHasher.HashPassword",
                    Kind: "Method",
                    Signature: "public string HashPassword(TUser user, string password)",
                    ContainingType: "PasswordHasher<TUser>",
                    Assembly: "Microsoft.AspNetCore.Identity",
                    IsExternal: true,
                    Summary: "Returns a hashed representation of the supplied password.")
            });

        var json = NavJsonSerializer.Serialize(response);
        var deserialized = NavJsonSerializer.Deserialize<SearchApiResponse>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Query.Should().Be("hash password");
        deserialized.Results[0].IsExternal.Should().BeTrue();
    }

    [Fact]
    public void ServerStartResponse_Serializes_Expected_Json_Fields()
    {
        var startedAt = new DateTimeOffset(2026, 10, 31, 10, 30, 0, TimeSpan.Zero);
        var payload = new ServerStartResponse(
            Version: 1,
            Pid: 12345,
            Port: 54321,
            Token: "a1b2c3d4",
            SolutionPath: "C:\\path\\to\\Solution.sln",
            StartedAt: startedAt,
            Url: "http://127.0.0.1:54321");

        var json = NavJsonSerializer.Serialize(payload);
        json.Should().Contain("\"version\":1");
        json.Should().Contain("\"pid\":12345");
        json.Should().Contain("\"port\":54321");
        json.Should().Contain("\"token\":\"a1b2c3d4\"");
        json.Should().Contain("\"solutionPath\":\"C:\\\\path\\\\to\\\\Solution.sln\"");
        json.Should().Contain("\"url\":\"http://127.0.0.1:54321\"");

        var deserialized = NavJsonSerializer.Deserialize<ServerStartResponse>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Port.Should().Be(54321);
        deserialized.Token.Should().Be("a1b2c3d4");
        deserialized.Url.Should().Be("http://127.0.0.1:54321");
    }

    [Fact]
    public void ApiSearchResultDto_SourceLocation_And_SymbolId_Serialize_Correctly()
    {
        var dto = new ApiSearchResultDto(
            Name: "MyClass.DoSomething",
            Kind: "Method",
            Signature: "public void DoSomething()",
            ContainingType: "MyClass",
            Assembly: "MyAssembly",
            IsExternal: false,
            Summary: "Does something.",
            IsObsolete: true,
            ObsoleteMessage: "Use DoSomethingElse instead.",
            File: "src/MyClass.cs",
            Line: 42,
            EndLine: 50,
            SymbolId: "M:MyClass.DoSomething");

        dto.FullName.Should().Be("MyClass.DoSomething");

        var json = NavJsonSerializer.Serialize(dto);
        json.Should().Contain("\"file\":\"src/MyClass.cs\"");
        json.Should().Contain("\"line\":42");
        json.Should().Contain("\"endLine\":50");
        json.Should().Contain("\"symbolId\":\"M:MyClass.DoSomething\"");
        json.Should().Contain("\"isObsolete\":true");
        json.Should().Contain("\"obsoleteMessage\":\"Use DoSomethingElse instead.\"");
        json.Should().Contain("\"fullName\":\"MyClass.DoSomething\"");

        var deserialized = NavJsonSerializer.Deserialize<ApiSearchResultDto>(json);
        deserialized.Should().NotBeNull();
        deserialized!.File.Should().Be("src/MyClass.cs");
        deserialized.Line.Should().Be(42);
        deserialized.EndLine.Should().Be(50);
        deserialized.SymbolId.Should().Be("M:MyClass.DoSomething");
        deserialized.IsObsolete.Should().BeTrue();
        deserialized.ObsoleteMessage.Should().Be("Use DoSomethingElse instead.");
    }

    [Fact]
    public void DefinitionItemDto_EndLine_Serializes_Correctly()
    {
        var dto = new DefinitionItemDto(
            File: "src/Program.cs",
            Line: 10,
            Column: 5,
            Kind: "Method",
            ContainingMember: "Main",
            EndLine: 25);

        var json = NavJsonSerializer.Serialize(dto);
        json.Should().Contain("\"endLine\":25");

        var deserialized = NavJsonSerializer.Deserialize<DefinitionItemDto>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Line.Should().Be(10);
        deserialized.EndLine.Should().Be(25);
    }

    [Fact]
    public void SearchApiRequest_Patterns_Serializes_Correctly()
    {
        var req = new SearchApiRequest(
            Pattern: "Foo",
            Patterns: new[] { "Bar", "Baz" });

        var json = NavJsonSerializer.Serialize(req);
        json.Should().Contain("\"patterns\":[\"Bar\",\"Baz\"]");

        var deserialized = NavJsonSerializer.Deserialize<SearchApiRequest>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Patterns.Should().BeEquivalentTo(new[] { "Bar", "Baz" });
    }
}
