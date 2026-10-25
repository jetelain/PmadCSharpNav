using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class SearchApiHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly IApiSearchEngine _searchEngine;

    public SearchApiHandler(ISolutionWorkspace workspace, IApiSearchEngine searchEngine)
    {
        _workspace = workspace;
        _searchEngine = searchEngine;
    }

    public async Task<SearchApiResponse> HandleAsync(SearchApiRequest request, CancellationToken ct = default)
    {
        var snapshot = await _workspace.GetCurrentSnapshotAsync(ct);

        var criteria = new ApiSearchCriteria(
            Pattern: request.Pattern,
            DocText: request.Doc,
            Scope: request.Scope,
            Kind: request.Kind,
            Access: request.Access,
            Framework: request.Framework);

        var allMatches = await _searchEngine.SearchAsync(snapshot, criteria, ct);

        var limit = Math.Clamp(request.Limit, 1, NavConstants.MaxLimit);
        var offset = Math.Max(0, request.Offset);

        var pagedResults = allMatches
            .Skip(offset)
            .Take(limit)
            .Select(m => new ApiSearchResultDto(
                Name: m.Name,
                Kind: m.Kind.ToString(),
                Signature: m.Signature,
                ContainingType: m.ContainingType,
                Assembly: m.Assembly,
                IsExternal: m.IsExternal,
                Summary: m.Summary))
            .ToList();

        var total = allMatches.Count;
        var truncated = (offset + pagedResults.Count) < total;
        var nextOffset = truncated ? offset + pagedResults.Count : (int?)null;

        var queryDescription = !string.IsNullOrWhiteSpace(request.Pattern)
            ? request.Pattern
            : (!string.IsNullOrWhiteSpace(request.Doc) ? $"doc:{request.Doc}" : "*");

        return new SearchApiResponse(
            Query: queryDescription,
            Scope: request.Scope,
            TotalMatches: total,
            Offset: offset,
            Limit: limit,
            Truncated: truncated,
            Results: pagedResults,
            NextOffset: nextOffset,
            IndexState: _searchEngine.GetIndexState(),
            SnapshotVersion: snapshot.SnapshotVersion);
    }
}
