namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Specifies the semantic nature of a symbol reference usage.
/// </summary>
public enum ReferenceKind
{
    Invocation,
    Instantiation,
    Read,
    Write,
    TypeReference,
    Override,
    Implementation
}
