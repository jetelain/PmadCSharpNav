namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Represents a concrete reference/usage location of a symbol in source code.
/// </summary>
public sealed record ReferenceLocation(
    string File,
    int Line,
    int Column,
    string Snippet,
    string? ContainingMember,
    ReferenceKind Kind,
    bool IsGenerated = false);
