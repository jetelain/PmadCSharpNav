namespace Pmad.CSharpNav.Contracts;

public sealed record FindDefinitionRequest(
    string? Symbol = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Project = null,
    string? Framework = null,
    bool IncludeGenerated = false,
    int Limit = NavConstants.DefaultLimit,
    int Offset = 0);

public sealed record DefinitionTargetDto(
    string Name,
    string Kind,
    string? ContainingType = null,
    string? DefinedIn = null,
    int? Line = null);

public sealed record DefinitionItemDto(
    string? File,
    int? Line,
    int? Column,
    string Kind,
    string? ContainingMember = null,
    string? ContainingType = null,
    string? AssemblyName = null,
    bool IsMetadata = false,
    bool IsGenerated = false,
    string? Snippet = null,
    string? SymbolId = null,
    int? EndLine = null);

public sealed record FindDefinitionResponse(
    DefinitionTargetDto Target,
    int TotalDefinitions,
    int Offset,
    int Limit,
    bool Truncated,
    IReadOnlyList<DefinitionItemDto> Definitions,
    int? NextOffset = null,
    long? SnapshotVersion = null,
    bool? Stale = null);
