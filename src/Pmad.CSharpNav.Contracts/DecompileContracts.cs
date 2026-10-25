namespace Pmad.CSharpNav.Contracts;

public sealed record DecompileRequest(
    string? Assembly = null,
    string? Type = null,
    string? Member = null,
    string? Symbol = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Framework = null);

public sealed record DecompileResponse(
    string Assembly,
    string Type,
    string? Member,
    string LanguageVersion,
    string Source,
    bool IsReferenceAssemblyOnly = false,
    long? SnapshotVersion = null,
    bool? Stale = null);
