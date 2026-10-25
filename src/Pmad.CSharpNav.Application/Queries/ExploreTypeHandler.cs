using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Domain;

namespace Pmad.CSharpNav.Application.Queries;

public sealed class ExploreTypeHandler
{
    private readonly ISolutionWorkspace _workspace;
    private readonly ITypeExplorer _explorer;

    public ExploreTypeHandler(ISolutionWorkspace workspace, ITypeExplorer explorer)
    {
        _workspace = workspace;
        _explorer = explorer;
    }

    public async Task<ExploreTypeResponse> HandleAsync(ExploreTypeRequest request, CancellationToken ct = default)
    {
        var snapshot = await _workspace.GetCurrentSnapshotAsync(ct);

        var options = new ExploreOptions(
            NoInherited: request.NoInherited,
            Access: request.Access,
            Framework: request.Framework);

        TypeExplorationResult? result;

        if (!string.IsNullOrWhiteSpace(request.File) && request.Line.HasValue && request.Column.HasValue)
        {
            result = await _explorer.ExploreTypeAtLocationAsync(
                snapshot,
                request.File,
                request.Line.Value,
                request.Column.Value,
                options,
                ct);

            if (result == null)
            {
                throw new NavException(
                    "TYPE_NOT_FOUND",
                    $"No type expression could be evaluated at {request.File}:{request.Line}:{request.Column}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Type))
        {
            result = await _explorer.ExploreTypeByNameAsync(snapshot, request.Type, options, ct);

            if (result == null)
            {
                throw new NavException(
                    "TYPE_NOT_FOUND",
                    $"No type matching '{request.Type}' was found.");
            }
        }
        else
        {
            throw new NavException("INVALID_REQUEST", "Either Type or File+Line+Column must be provided.");
        }

        var typeSummary = new TypeSummaryDto(
            Name: result.Name,
            FullName: result.FullName,
            Kind: result.Kind.ToString(),
            Accessibility: result.Accessibility.ToString(),
            Modifiers: result.Modifiers,
            BaseType: result.BaseType,
            Interfaces: result.Interfaces,
            Summary: result.Summary);

        // Group members by kind
        var constructors = result.Members
            .Where(m => m.Kind == SymbolKind.Constructor)
            .Select(MapMember)
            .ToList();

        var properties = result.Members
            .Where(m => m.Kind == SymbolKind.Property)
            .Select(MapMember)
            .ToList();

        var methods = result.Members
            .Where(m => m.Kind == SymbolKind.Method)
            .Select(MapMember)
            .ToList();

        var fields = result.Members
            .Where(m => m.Kind == SymbolKind.Field)
            .Select(MapMember)
            .ToList();

        var events = result.Members
            .Where(m => m.Kind == SymbolKind.Event)
            .Select(MapMember)
            .ToList();

        return new ExploreTypeResponse(
            Type: typeSummary,
            Constructors: constructors.Count > 0 ? constructors : null,
            Properties: properties.Count > 0 ? properties : null,
            Methods: methods.Count > 0 ? methods : null,
            Fields: fields.Count > 0 ? fields : null,
            Events: events.Count > 0 ? events : null,
            SnapshotVersion: snapshot.SnapshotVersion);
    }

    private static MemberSummaryDto MapMember(TypeMemberInfo m) =>
        new(
            Name: m.Name,
            Kind: m.Kind.ToString(),
            Signature: m.Signature,
            ReturnType: m.ReturnType,
            Summary: m.Summary,
            Modifiers: m.Modifiers.Count > 0 ? m.Modifiers : null);
}
