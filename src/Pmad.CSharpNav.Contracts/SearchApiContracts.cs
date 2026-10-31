namespace Pmad.CSharpNav.Contracts;

public sealed record SearchApiRequest(
    string? Pattern = null,
    string? Doc = null,
    string Scope = "all", // "all", "source", "dependencies", or assembly name
    string? Kind = null,  // "class", "interface", "method", "property", "enum", "record", etc.
    string Access = "all", // "all", "public", "internal"
    string? Framework = null,
    int Limit = NavConstants.DefaultLimit,
    int Offset = 0,
    string? Project = null);

public sealed record ApiSearchResultDto(
    string Name,
    string Kind,
    string Signature,
    string? ContainingType = null,
    string? Assembly = null,
    bool IsExternal = false,
    string? Summary = null);

public sealed record SearchApiResponse(
    string Query,
    string Scope,
    int TotalMatches,
    int Offset,
    int Limit,
    bool Truncated,
    IReadOnlyList<ApiSearchResultDto> Results,
    int? NextOffset = null,
    string IndexState = "ready", // "partial" | "ready"
    long? SnapshotVersion = null,
    bool? Stale = null);
