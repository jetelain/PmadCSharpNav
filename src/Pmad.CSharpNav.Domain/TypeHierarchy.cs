namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Represents inheritance and interface relationships for a type.
/// </summary>
public sealed record TypeHierarchy(
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> DerivedTypes);
