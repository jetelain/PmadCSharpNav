namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Uniquely identifies a code symbol across the solution or referenced assemblies.
/// </summary>
public sealed record SymbolIdentifier(
    string Assembly,
    string? Namespace = null,
    string? ContainingType = null,
    string? MemberSignature = null,
    string? SymbolId = null);
