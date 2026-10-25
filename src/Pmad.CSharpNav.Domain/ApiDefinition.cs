namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Represents an API declaration definition found in source code or referenced libraries.
/// </summary>
public sealed record ApiDefinition(
    string Name,
    SymbolKind Kind,
    string Signature,
    string? ContainingType,
    string Assembly,
    bool IsExternal,
    string? Summary = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string? Returns = null,
    string? Remarks = null);
