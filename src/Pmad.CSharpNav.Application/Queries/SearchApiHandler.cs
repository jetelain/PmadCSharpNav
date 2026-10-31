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
            Framework: request.Framework,
            Project: request.Project,
            Namespace: request.Namespace,
            Assembly: request.Assembly,
            IncludeObsolete: request.IncludeObsolete,
            InheritsFrom: request.InheritsFrom,
            ReturnsType: request.ReturnsType,
            Patterns: request.Patterns);

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
                Summary: m.Summary,
                IsObsolete: m.IsObsolete,
                ObsoleteMessage: m.ObsoleteMessage,
                File: m.File,
                Line: m.Line,
                EndLine: m.EndLine,
                SymbolId: m.SymbolId))
            .ToList();

        var total = allMatches.Count;
        var truncated = (offset + pagedResults.Count) < total;
        var nextOffset = truncated ? offset + pagedResults.Count : (int?)null;

        var queryTerms = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Pattern))
        {
            queryTerms.Add(request.Pattern);
        }
        if (request.Patterns != null)
        {
            foreach (var p in request.Patterns)
            {
                if (!string.IsNullOrWhiteSpace(p) && !queryTerms.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    queryTerms.Add(p);
                }
            }
        }

        var queryDescription = queryTerms.Count > 0
            ? string.Join(", ", queryTerms)
            : (!string.IsNullOrWhiteSpace(request.Doc) ? $"doc:{request.Doc}" : "*");

        var effectiveScope = !string.IsNullOrWhiteSpace(request.Project)
            ? request.Project
            : request.Scope;

        return new SearchApiResponse(
            Query: queryDescription,
            Scope: effectiveScope,
            TotalMatches: total,
            Offset: offset,
            Limit: limit,
            Truncated: truncated,
            Results: pagedResults,
            NextOffset: nextOffset,
            IndexState: _searchEngine.GetIndexState(),
            SnapshotVersion: snapshot.SnapshotVersion,
            Stale: snapshot.IsStale ? true : null);
    }
}
