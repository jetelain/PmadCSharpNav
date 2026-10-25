namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Details about a member (method, property, constructor, field, event) of a type.
/// </summary>
public sealed record TypeMemberInfo(
    string Name,
    SymbolKind Kind,
    string Signature,
    Accessibility Accessibility,
    IReadOnlyList<string> Modifiers,
    string? ReturnType = null,
    string? Summary = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string? Accessors = null);
