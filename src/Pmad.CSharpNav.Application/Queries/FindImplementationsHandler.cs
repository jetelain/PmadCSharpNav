using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class FindImplementationsHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly IImplementationFinder _finder;

    public FindImplementationsHandler(ISolutionWorkspace workspace, IImplementationFinder finder)
    {
        _workspace = workspace;
        _finder = finder;
    }

    public async Task<FindImplementationsResponse> HandleAsync(FindImplementationsRequest request, CancellationToken ct = default)
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

        var searchOptions = new ImplementationSearchOptions(
            ExcludeTests: request.ExcludeTests,
            Project: request.Project,
            Framework: request.Framework,
            IncludeGenerated: request.IncludeGenerated);

        var allImplementations = await _finder.FindImplementationsAsync(snapshot, targetResult.Target, searchOptions, ct);

        var limit = Math.Clamp(request.Limit, 1, NavConstants.MaxLimit);
        var offset = Math.Max(0, request.Offset);

        var pagedImplementations = allImplementations
            .Skip(offset)
            .Take(limit)
            .Select(i => new ImplementationItemDto(
                File: i.File,
                Line: i.Line,
                Column: i.Column,
                Kind: i.Kind,
                ContainingMember: i.ContainingMember,
                ContainingType: i.ContainingType,
                IsGenerated: i.IsGenerated,
                Snippet: i.Snippet))
            .ToList();

        var total = allImplementations.Count;
        var truncated = (offset + pagedImplementations.Count) < total;
        var nextOffset = truncated ? offset + pagedImplementations.Count : (int?)null;

        var targetDto = new ImplementationTargetDto(
            Name: targetResult.Target.MemberSignature ?? targetResult.Target.ContainingType ?? targetResult.Target.Assembly,
            Kind: targetResult.TargetKind?.ToString() ?? "Symbol",
            ContainingType: targetResult.Target.ContainingType,
            DefinedIn: targetResult.DefinedIn,
            Line: targetResult.DefinedLine);

        return new FindImplementationsResponse(
            Target: targetDto,
            TotalImplementations: total,
            Offset: offset,
            Limit: limit,
            Truncated: truncated,
            Implementations: pagedImplementations,
            NextOffset: nextOffset,
            SnapshotVersion: snapshot.SnapshotVersion,
            Stale: snapshot.IsStale ? true : null);
    }
}
