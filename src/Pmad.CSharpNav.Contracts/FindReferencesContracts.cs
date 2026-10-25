namespace Pmad.CSharpNav.Contracts;

public sealed record FindReferencesRequest(
    string? Symbol = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    bool ExcludeTests = false,
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false,
    bool IncludeOverrides = true,
    bool IncludeImplementations = true,
    int Limit = NavConstants.DefaultLimit,
    int Offset = 0);

public sealed record ReferenceTargetDto(
    string Name,
    string Kind,
    string? ContainingType = null,
    string? DefinedIn = null,
    int? Line = null);

public sealed record ReferenceItemDto(
    string File,
    int Line,
    int Column,
    string Kind,
    string? ContainingMember = null,
    bool IsGenerated = false,
    string? Snippet = null);

public sealed record FindReferencesResponse(
    ReferenceTargetDto Target,
    int TotalReferences,
    int Offset,
    int Limit,
    bool Truncated,
    IReadOnlyList<ReferenceItemDto> References,
    int? NextOffset = null,
    long? SnapshotVersion = null,
    bool? Stale = null);
