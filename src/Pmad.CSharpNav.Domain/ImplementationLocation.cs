namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Represents a location where a type or member implementation resides.
/// </summary>
public sealed record ImplementationLocation(
    string File,
    int Line,
    int Column,
    string? Snippet = null,
    string? ContainingMember = null,
    string? ContainingType = null,
    string Kind = "Implementation",
    bool IsGenerated = false);
