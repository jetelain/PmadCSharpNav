using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class FindReferencesHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly IReferenceFinder _finder;

    public FindReferencesHandler(ISolutionWorkspace workspace, IReferenceFinder finder)
    {
        _workspace = workspace;
        _finder = finder;
    }

    public async Task<FindReferencesResponse> HandleAsync(FindReferencesRequest request, CancellationToken ct = default)
    {
        var snapshot = await _workspace.GetCurrentSnapshotAsync(ct);

        SymbolResolutionResult? targetResult = null;

        if (!string.IsNullOrWhiteSpace(request.File) && request.Line.HasValue && request.Column.HasValue)
        {
            targetResult = await _finder.ResolveSymbolAtLocationAsync(
                snapshot,
                request.File,
                request.Line.Value,
                request.Column.Value,
                ct);

            if (targetResult == null)
            {
                throw new NavException(
                    "SYMBOL_NOT_FOUND",
                    $"No symbol found at {request.File}:{request.Line}:{request.Column}");
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
            throw new NavException("INVALID_REQUEST", "Either Symbol or File+Line+Column must be provided.");
        }

        var searchOptions = new ReferenceSearchOptions(
            ExcludeTests: request.ExcludeTests,
            Project: request.Project,
            Framework: request.Framework,
            IncludeGenerated: request.IncludeGenerated,
            IncludeOverrides: request.IncludeOverrides,
            IncludeImplementations: request.IncludeImplementations);

        var allReferences = await _finder.FindReferencesAsync(snapshot, targetResult.Target, searchOptions, ct);

        var limit = Math.Clamp(request.Limit, 1, NavConstants.MaxLimit);
        var offset = Math.Max(0, request.Offset);

        var pagedReferences = allReferences
            .Skip(offset)
            .Take(limit)
            .Select(r => new ReferenceItemDto(
                File: r.File,
                Line: r.Line,
                Column: r.Column,
                Kind: r.Kind.ToString(),
                ContainingMember: r.ContainingMember,
                IsGenerated: r.IsGenerated,
                Snippet: r.Snippet))
            .ToList();

        var total = allReferences.Count;
        var truncated = (offset + pagedReferences.Count) < total;
        var nextOffset = truncated ? offset + pagedReferences.Count : (int?)null;

        var targetDto = new ReferenceTargetDto(
            Name: targetResult.Target.MemberSignature ?? targetResult.Target.ContainingType ?? targetResult.Target.Assembly,
            Kind: targetResult.TargetKind?.ToString() ?? "Symbol",
            ContainingType: targetResult.Target.ContainingType,
            DefinedIn: targetResult.DefinedIn,
            Line: targetResult.DefinedLine);

        return new FindReferencesResponse(
            Target: targetDto,
            TotalReferences: total,
            Offset: offset,
            Limit: limit,
            Truncated: truncated,
            References: pagedReferences,
            NextOffset: nextOffset,
            SnapshotVersion: snapshot.SnapshotVersion,
            Stale: snapshot.IsStale ? true : null);
    }
}
