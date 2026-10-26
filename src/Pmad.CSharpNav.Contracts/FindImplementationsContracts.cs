namespace Pmad.CSharpNav.Contracts;

public sealed record FindImplementationsRequest(
    string? Symbol = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    bool ExcludeTests = false,
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false,
    int Limit = NavConstants.DefaultLimit,
    int Offset = 0);

public sealed record ImplementationTargetDto(
    string Name,
    string Kind,
    string? ContainingType = null,
    string? DefinedIn = null,
    int? Line = null);

public sealed record ImplementationItemDto(
    string File,
    int Line,
    int Column,
    string Kind,
    string? ContainingMember = null,
    string? ContainingType = null,
    bool IsGenerated = false,
    string? Snippet = null);

public sealed record FindImplementationsResponse(
    ImplementationTargetDto Target,
    int TotalImplementations,
    int Offset,
    int Limit,
    bool Truncated,
    IReadOnlyList<ImplementationItemDto> Implementations,
    int? NextOffset = null,
    long? SnapshotVersion = null,
    bool? Stale = null);
