using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class DecompileMemberHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly IAssemblyDecompiler _decompiler;

    public DecompileMemberHandler(ISolutionWorkspace workspace, IAssemblyDecompiler decompiler)
    {
        _workspace = workspace;
        _decompiler = decompiler;
    }

    public async Task<DecompileResponse> HandleAsync(DecompileRequest request, CancellationToken ct = default)
    {
        var snapshot = await _workspace.GetCurrentSnapshotAsync(ct);

        var criteria = new DecompileCriteria(
            Assembly: request.Assembly,
            Type: request.Type,
            Member: request.Member,
            Symbol: request.Symbol,
            File: request.File,
            Line: request.Line,
            Column: request.Column,
            Framework: request.Framework);

        var decompiled = await _decompiler.DecompileAsync(snapshot, criteria, ct);

        if (decompiled == null)
        {
            throw new NavException("DECOMPILATION_FAILED", "Failed to resolve or decompile the requested symbol or assembly.");
        }

        return new DecompileResponse(
            Assembly: decompiled.Assembly,
            Type: decompiled.Type,
            Member: decompiled.Member,
            LanguageVersion: decompiled.LanguageVersion,
            Source: decompiled.Source,
            IsReferenceAssemblyOnly: decompiled.IsReferenceAssemblyOnly,
            SnapshotVersion: snapshot.SnapshotVersion,
            Stale: snapshot.IsStale ? true : null);
    }
}
