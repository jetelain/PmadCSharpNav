using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record ExploreOptions(
    bool NoInherited = false,
    string Access = "all", // "all", "public", "internal"
    string? Framework = null);

public sealed record TypeExplorationResult(
    string Name,
    string FullName,
    SymbolKind Kind,
    Accessibility Accessibility,
    IReadOnlyList<string> Modifiers,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    string? Summary,
    IReadOnlyList<TypeMemberInfo> Members);

/// <summary>
/// Port for inspecting types, members, hierarchies, and evaluating variable types.
/// </summary>
public interface ITypeExplorer
{
    Task<TypeExplorationResult?> ExploreTypeByNameAsync(
        IWorkspaceSnapshot snapshot,
        string typeName,
        ExploreOptions options,
        CancellationToken ct = default);

    Task<TypeExplorationResult?> ExploreTypeAtLocationAsync(
        IWorkspaceSnapshot snapshot,
        string file,
        int line,
        int column,
        ExploreOptions options,
        CancellationToken ct = default);
}
