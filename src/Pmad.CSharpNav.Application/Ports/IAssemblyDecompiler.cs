using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Ports;

public sealed record DecompileCriteria(
    string? Assembly = null,
    string? Type = null,
    string? Member = null,
    string? Symbol = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Framework = null);

/// <summary>
/// Port for resolving referenced assemblies and decompiling types or individual members.
/// </summary>
public interface IAssemblyDecompiler
{
    Task<DecompiledSource?> DecompileAsync(
        IWorkspaceSnapshot snapshot,
        DecompileCriteria criteria,
        CancellationToken ct = default);
}
