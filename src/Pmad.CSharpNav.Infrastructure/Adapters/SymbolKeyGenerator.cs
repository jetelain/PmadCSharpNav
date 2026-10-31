using Microsoft.CodeAnalysis;

namespace Pmad.CSharpNav.Infrastructure.Adapters;

public static class SymbolKeyGenerator
{
    public static string? GetId(ISymbol symbol)
    {
        return symbol.GetDocumentationCommentId();
    }
}

