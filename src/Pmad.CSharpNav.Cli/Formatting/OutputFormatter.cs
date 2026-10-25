using System.Globalization;
using System.Text;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Formatting;

public static class OutputFormatter
{
    public static string FormatReferences(FindReferencesResponse res, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### References for `{res.Target.Name}` ({res.Target.Kind})");
            if (res.Target.ContainingType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"- **Type:** `{res.Target.ContainingType}`");
            if (res.Target.DefinedIn != null) sb.AppendLine(CultureInfo.InvariantCulture, $"- **Defined In:** `{res.Target.DefinedIn}:{res.Target.Line}`");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Total References:** {res.TotalReferences}");
            sb.AppendLine();
            sb.AppendLine("| Location | Kind | Containing Member | Snippet |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var r in res.References)
            {
                var loc = $"{r.File}:{r.Line}:{r.Column}";
                var snip = r.Snippet?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{loc}` | {r.Kind} | {r.ContainingMember ?? "-"} | `{snip}` |");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Target: {res.Target.Name} ({res.Target.Kind})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"References ({res.References.Count} of {res.TotalReferences}):");
            foreach (var r in res.References)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.File}:{r.Line}:{r.Column} [{r.Kind}] {(r.ContainingMember != null ? "in " + r.ContainingMember : "")}");
                if (!string.IsNullOrWhiteSpace(r.Snippet))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {r.Snippet.Trim()}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatApiSearch(SearchApiResponse res, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### API Search Results for `{res.Query}` (Total: {res.TotalMatches})");
            sb.AppendLine();
            sb.AppendLine("| Symbol | Kind | Signature | Assembly | Summary |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var r in res.Results)
            {
                var summary = r.Summary?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{r.Name}` | {r.Kind} | `{r.Signature}` | {r.Assembly} | {summary} |");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Search: '{res.Query}' ({res.Results.Count} of {res.TotalMatches} matches):");
            foreach (var r in res.Results)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{r.Kind}] {r.Name} ({r.Assembly})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Signature: {r.Signature}");
                if (!string.IsNullOrWhiteSpace(r.Summary))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Summary:   {r.Summary}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatExplore(ExploreTypeResponse res, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## `{res.Type.FullName}` ({res.Type.Kind})");
            if (res.Type.BaseType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"- **Base Type:** `{res.Type.BaseType}`");
            if (res.Type.Interfaces != null && res.Type.Interfaces.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **Interfaces:** {string.Join(", ", res.Type.Interfaces.Select(i => $"`{i}`"))}");
            }
            if (!string.IsNullOrWhiteSpace(res.Type.Summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **Summary:** {res.Type.Summary}");
            }
            sb.AppendLine();

            AppendMemberSectionMarkdown(sb, "Constructors", res.Constructors);
            AppendMemberSectionMarkdown(sb, "Properties", res.Properties);
            AppendMemberSectionMarkdown(sb, "Methods", res.Methods);
            AppendMemberSectionMarkdown(sb, "Fields", res.Fields);
            AppendMemberSectionMarkdown(sb, "Events", res.Events);
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Type: {res.Type.FullName} ({res.Type.Kind})");
            if (res.Type.BaseType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"  Base: {res.Type.BaseType}");
            if (!string.IsNullOrWhiteSpace(res.Type.Summary)) sb.AppendLine(CultureInfo.InvariantCulture, $"  Summary: {res.Type.Summary}");

            AppendMemberSectionText(sb, "Constructors", res.Constructors);
            AppendMemberSectionText(sb, "Properties", res.Properties);
            AppendMemberSectionText(sb, "Methods", res.Methods);
            AppendMemberSectionText(sb, "Fields", res.Fields);
            AppendMemberSectionText(sb, "Events", res.Events);
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatDecompile(DecompileResponse res, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"// Decompiled from: {res.Assembly}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"// Target: {res.Type}{(res.Member != null ? "." + res.Member : "")}");
            if (res.IsReferenceAssemblyOnly) sb.AppendLine("// Warning: Reference assembly only (no method bodies)");
            sb.AppendLine("```csharp");
            sb.AppendLine(res.Source);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"// Decompiled from: {res.Assembly} ({res.Type})");
            sb.AppendLine(res.Source);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMemberSectionMarkdown(StringBuilder sb, string title, IReadOnlyList<MemberSummaryDto>? members)
    {
        if (members == null || members.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        sb.AppendLine("| Member | Signature | Summary |");
        sb.AppendLine("| :--- | :--- | :--- |");
        foreach (var m in members)
        {
            var summary = m.Summary?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{m.Name}` | `{m.Signature}` | {summary} |");
        }
        sb.AppendLine();
    }

    private static void AppendMemberSectionText(StringBuilder sb, string title, IReadOnlyList<MemberSummaryDto>? members)
    {
        if (members == null || members.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"  {title}:");
        foreach (var m in members)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {m.Signature}");
            if (!string.IsNullOrWhiteSpace(m.Summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      // {m.Summary}");
            }
        }
    }
}
