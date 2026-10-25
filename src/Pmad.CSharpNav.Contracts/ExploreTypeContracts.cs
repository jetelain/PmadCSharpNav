namespace Pmad.CSharpNav.Contracts;

public sealed record ExploreTypeRequest(
    string? Type = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    bool NoInherited = false,
    string Access = "all", // "all", "public", "internal"
    string? Framework = null,
    int Limit = NavConstants.DefaultLimit,
    int Offset = 0);

public sealed record TypeSummaryDto(
    string Name,
    string FullName,
    string Kind,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? BaseType = null,
    IReadOnlyList<string>? Interfaces = null,
    string? Summary = null);

public sealed record MemberSummaryDto(
    string Name,
    string Kind,
    string Signature,
    string? Accessors = null,
    string? ReturnType = null,
    string? Summary = null,
    IReadOnlyList<string>? Modifiers = null);

public sealed record ExploreTypeResponse(
    TypeSummaryDto Type,
    IReadOnlyList<MemberSummaryDto>? Constructors = null,
    IReadOnlyList<MemberSummaryDto>? Properties = null,
    IReadOnlyList<MemberSummaryDto>? Methods = null,
    IReadOnlyList<MemberSummaryDto>? Fields = null,
    IReadOnlyList<MemberSummaryDto>? Events = null,
    long? SnapshotVersion = null);
