namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Represents a location where a symbol is defined (source or metadata).
/// </summary>
public sealed record DefinitionLocation(
    string? File,
    int? Line,
    int? Column,
    string? Snippet = null,
    string? ContainingMember = null,
    string? ContainingType = null,
    string? AssemblyName = null,
    SymbolKind? Kind = null,
    bool IsMetadata = false,
    bool IsGenerated = false,
    string? SymbolId = null,
    int? EndLine = null);
