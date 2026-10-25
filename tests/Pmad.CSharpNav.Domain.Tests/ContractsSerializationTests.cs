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
}
