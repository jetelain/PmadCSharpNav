namespace Pmad.CSharpNav.Contracts;

public sealed record DisambiguationCandidateDto(
    string Name,
    string Signature,
    string ContainingType,
    string? SymbolId = null,
    string? DefinedIn = null,
    int? Line = null);

public sealed record ErrorResponse(
    string Error,
    string Message,
    string Code,
    string? Details = null,
    IReadOnlyList<DisambiguationCandidateDto>? Candidates = null);
