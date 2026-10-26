using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class FindDefinitionHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly IDefinitionFinder _finder;

    public FindDefinitionHandler(ISolutionWorkspace workspace, IDefinitionFinder finder)
    {
        _workspace = workspace;
        _finder = finder;
    }

    public async Task<FindDefinitionResponse> HandleAsync(FindDefinitionRequest request, CancellationToken ct = default)
    {
        var snapshot = await _workspace.GetCurrentSnapshotAsync(ct);

        SymbolResolutionResult? targetResult = null;

        if (!string.IsNullOrWhiteSpace(request.File) && request.Line.HasValue)
        {
            var col = request.Column ?? 1;
            targetResult = await _finder.ResolveSymbolAtLocationAsync(
                snapshot,
                request.File,
                request.Line.Value,
                col,
                ct);

            if (targetResult == null)
            {
                throw new NavException(
                    "SYMBOL_NOT_FOUND",
                    $"No symbol found at {request.File}:{request.Line}:{col}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Symbol))
        {
            var candidates = await _finder.ResolveSymbolsByNameAsync(snapshot, request.Symbol, ct);

            if (candidates.Count == 0)
            {
                throw new NavException(
                    "SYMBOL_NOT_FOUND",
                    $"No symbol matching '{request.Symbol}' was found in the workspace.");
            }

            if (candidates.Count > 1)
            {
                var candidateDtos = candidates.Select(c => new DisambiguationCandidateDto(
                    Name: c.Target.MemberSignature ?? c.Target.ContainingType ?? c.Target.Assembly,
                    Signature: c.Target.MemberSignature ?? c.Target.ContainingType ?? "",
                    ContainingType: c.Target.ContainingType ?? "",
                    SymbolId: c.Target.SymbolId,
                    DefinedIn: c.DefinedIn,
                    Line: c.DefinedLine)).ToList();

                throw new NavException(
                    "AMBIGUOUS_SYMBOL",
                    $"Symbol '{request.Symbol}' is ambiguous. Please disambiguate using an exact symbol ID or source location.",
                    candidateDtos);
            }

            targetResult = candidates[0];
        }
        else
        {
            throw new NavException("INVALID_REQUEST", "Either Symbol or File+Line (+Column) must be provided.");
        }

        var searchOptions = new DefinitionSearchOptions(
            Project: request.Project,
            Framework: request.Framework,
            IncludeGenerated: request.IncludeGenerated);

        var allDefinitions = await _finder.FindDefinitionsAsync(snapshot, targetResult.Target, searchOptions, ct);

        var limit = Math.Clamp(request.Limit, 1, NavConstants.MaxLimit);
        var offset = Math.Max(0, request.Offset);

        var pagedDefinitions = allDefinitions
            .Skip(offset)
            .Take(limit)
            .Select(d => new DefinitionItemDto(
                File: d.File,
                Line: d.Line,
                Column: d.Column,
                Kind: d.Kind?.ToString() ?? "Definition",
                ContainingMember: d.ContainingMember,
                ContainingType: d.ContainingType,
                AssemblyName: d.AssemblyName,
                IsMetadata: d.IsMetadata,
                IsGenerated: d.IsGenerated,
                Snippet: d.Snippet,
                SymbolId: d.SymbolId))
            .ToList();

        var total = allDefinitions.Count;
        var truncated = (offset + pagedDefinitions.Count) < total;
        var nextOffset = truncated ? offset + pagedDefinitions.Count : (int?)null;

        var targetDto = new DefinitionTargetDto(
            Name: targetResult.Target.MemberSignature ?? targetResult.Target.ContainingType ?? targetResult.Target.Assembly,
            Kind: targetResult.TargetKind?.ToString() ?? "Symbol",
            ContainingType: targetResult.Target.ContainingType,
            DefinedIn: targetResult.DefinedIn,
            Line: targetResult.DefinedLine);

        return new FindDefinitionResponse(
            Target: targetDto,
            TotalDefinitions: total,
            Offset: offset,
            Limit: limit,
            Truncated: truncated,
            Definitions: pagedDefinitions,
            NextOffset: nextOffset,
            SnapshotVersion: snapshot.SnapshotVersion,
            Stale: snapshot.IsStale ? true : null);
    }
}
