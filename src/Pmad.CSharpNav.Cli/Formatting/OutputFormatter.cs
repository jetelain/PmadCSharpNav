using System.Globalization;
using System.Text;
using Pmad.CSharpNav.Cli.Commands;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Formatting;

public static class OutputFormatter
{
    public static string FormatReferences(FindReferencesResponse res, string? format = null, string? baseDirectory = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        baseDirectory ??= Directory.GetCurrentDirectory();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var relRes = res.ToRelativePaths(baseDirectory);
            return NavJsonSerializer.Serialize(relRes);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        var relTargetDef = PathHelper.MakeRelative(res.Target.DefinedIn, baseDirectory);

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("###")} {theme.Bold("References for")} `{theme.Symbol(res.Target.Name)}` ({theme.Kind(res.Target.Kind)})");
            if (res.Target.ContainingType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Type:**")} `{theme.Symbol(res.Target.ContainingType)}`");
            if (relTargetDef != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Defined In:**")} `{theme.Location(relTargetDef)}:{res.Target.Line}`");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Total References:**")} {res.TotalReferences}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} {theme.TableHeader("Location")} {theme.TableBorder("|")} {theme.TableHeader("Kind")} {theme.TableBorder("|")} {theme.TableHeader("Containing Member")} {theme.TableBorder("|")} {theme.TableHeader("Snippet")} {theme.TableBorder("|")}");
            sb.AppendLine(theme.TableBorder("| :--- | :--- | :--- | :--- | :--- |"));
            foreach (var r in res.References)
            {
                var relFile = PathHelper.MakeRelative(r.File, baseDirectory) ?? r.File;
                var loc = $"{relFile}:{r.Line}:{r.Column}";
                var snip = r.Snippet?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} `{theme.Location(loc)}` {theme.TableBorder("|")} {theme.Kind(r.Kind)} {theme.TableBorder("|")} {(r.ContainingMember != null ? theme.Member(r.ContainingMember) : "-")} {theme.TableBorder("|")} `{theme.Snippet(snip)}` {theme.TableBorder("|")}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Target:")} {theme.Symbol(res.Target.Name)} ({theme.Kind(res.Target.Kind)})");
            if (relTargetDef != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Dim("Defined In:")} {theme.Location(relTargetDef)}:{res.Target.Line}");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("References")} ({res.References.Count} of {res.TotalReferences}):");
            foreach (var r in res.References)
            {
                var relFile = PathHelper.MakeRelative(r.File, baseDirectory) ?? r.File;
                var loc = $"{relFile}:{r.Line}:{r.Column}";
                var memberStr = r.ContainingMember != null ? $" in {theme.Member(r.ContainingMember)}" : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Location(loc)} [{theme.Kind(r.Kind)}]{memberStr}");
                if (!string.IsNullOrWhiteSpace(r.Snippet))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Snippet(r.Snippet.Trim())}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatDefinition(FindDefinitionResponse res, string? format = null, string? baseDirectory = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        baseDirectory ??= Directory.GetCurrentDirectory();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var relRes = res.ToRelativePaths(baseDirectory);
            return NavJsonSerializer.Serialize(relRes);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        var relTargetDef = PathHelper.MakeRelative(res.Target.DefinedIn, baseDirectory);

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("###")} {theme.Bold("Definition for")} `{theme.Symbol(res.Target.Name)}` ({theme.Kind(res.Target.Kind)})");
            if (res.Target.ContainingType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Type:**")} `{theme.Symbol(res.Target.ContainingType)}`");
            if (relTargetDef != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Defined In:**")} `{theme.Location(relTargetDef)}:{res.Target.Line}`");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Total Definitions:**")} {res.TotalDefinitions}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} {theme.TableHeader("Location")} {theme.TableBorder("|")} {theme.TableHeader("Kind")} {theme.TableBorder("|")} {theme.TableHeader("Containing Type")} {theme.TableBorder("|")} {theme.TableHeader("Snippet")} {theme.TableBorder("|")}");
            sb.AppendLine(theme.TableBorder("| :--- | :--- | :--- | :--- | :--- |"));
            foreach (var d in res.Definitions)
            {
                string loc;
                if (d.IsMetadata)
                {
                    loc = $"[Metadata] {d.AssemblyName ?? "External"}";
                }
                else
                {
                    var relFile = PathHelper.MakeRelative(d.File, baseDirectory) ?? d.File;
                    loc = $"{relFile}:{d.Line}:{d.Column}";
                }
                var snip = d.Snippet?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? (d.SymbolId ?? "");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} `{theme.Location(loc)}` {theme.TableBorder("|")} {theme.Kind(d.Kind)} {theme.TableBorder("|")} {(d.ContainingType != null ? theme.Member(d.ContainingType) : "-")} {theme.TableBorder("|")} `{theme.Snippet(snip)}` {theme.TableBorder("|")}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Target:")} {theme.Symbol(res.Target.Name)} ({theme.Kind(res.Target.Kind)})");
            if (relTargetDef != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Dim("Defined In:")} {theme.Location(relTargetDef)}:{res.Target.Line}");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Definitions")} ({res.Definitions.Count} of {res.TotalDefinitions}):");
            foreach (var d in res.Definitions)
            {
                if (d.IsMetadata)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  [{theme.Kind("Metadata")}] {theme.Assembly(d.AssemblyName ?? "External")} {theme.Dim(d.SymbolId ?? "")}");
                }
                else
                {
                    var relFile = PathHelper.MakeRelative(d.File, baseDirectory) ?? d.File;
                    var loc = $"{relFile}:{d.Line}:{d.Column}";
                    var typeStr = d.ContainingType != null ? $" in {theme.Member(d.ContainingType)}" : "";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Location(loc)} [{theme.Kind(d.Kind)}]{typeStr}");
                    if (!string.IsNullOrWhiteSpace(d.Snippet))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Snippet(d.Snippet.Trim())}");
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatImplementations(FindImplementationsResponse res, string? format = null, string? baseDirectory = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        baseDirectory ??= Directory.GetCurrentDirectory();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var relRes = res.ToRelativePaths(baseDirectory);
            return NavJsonSerializer.Serialize(relRes);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        var relTargetDef = PathHelper.MakeRelative(res.Target.DefinedIn, baseDirectory);

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("###")} {theme.Bold("Implementations for")} `{theme.Symbol(res.Target.Name)}` ({theme.Kind(res.Target.Kind)})");
            if (res.Target.ContainingType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Type:**")} `{theme.Symbol(res.Target.ContainingType)}`");
            if (relTargetDef != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Defined In:**")} `{theme.Location(relTargetDef)}:{res.Target.Line}`");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Total Implementations:**")} {res.TotalImplementations}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} {theme.TableHeader("Location")} {theme.TableBorder("|")} {theme.TableHeader("Kind")} {theme.TableBorder("|")} {theme.TableHeader("Containing Type")} {theme.TableBorder("|")} {theme.TableHeader("Snippet")} {theme.TableBorder("|")}");
            sb.AppendLine(theme.TableBorder("| :--- | :--- | :--- | :--- | :--- |"));
            foreach (var i in res.Implementations)
            {
                var relFile = PathHelper.MakeRelative(i.File, baseDirectory) ?? i.File;
                var loc = $"{relFile}:{i.Line}:{i.Column}";
                var snip = i.Snippet?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} `{theme.Location(loc)}` {theme.TableBorder("|")} {theme.Kind(i.Kind)} {theme.TableBorder("|")} {(i.ContainingType != null ? theme.Member(i.ContainingType) : "-")} {theme.TableBorder("|")} `{theme.Snippet(snip)}` {theme.TableBorder("|")}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Target:")} {theme.Symbol(res.Target.Name)} ({theme.Kind(res.Target.Kind)})");
            if (relTargetDef != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Dim("Defined In:")} {theme.Location(relTargetDef)}:{res.Target.Line}");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Implementations")} ({res.Implementations.Count} of {res.TotalImplementations}):");
            foreach (var i in res.Implementations)
            {
                var relFile = PathHelper.MakeRelative(i.File, baseDirectory) ?? i.File;
                var loc = $"{relFile}:{i.Line}:{i.Column}";
                var typeStr = i.ContainingType != null ? $" in {theme.Member(i.ContainingType)}" : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Location(loc)} [{theme.Kind(i.Kind)}]{typeStr}");
                if (!string.IsNullOrWhiteSpace(i.Snippet))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Snippet(i.Snippet.Trim())}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatApiSearch(SearchApiResponse res, string? format = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("###")} {theme.Bold("API Search Results for")} `{theme.Symbol(res.Query)}` {theme.Dim($"(Total: {res.TotalMatches})")}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} {theme.TableHeader("Symbol")} {theme.TableBorder("|")} {theme.TableHeader("Kind")} {theme.TableBorder("|")} {theme.TableHeader("Signature")} {theme.TableBorder("|")} {theme.TableHeader("Assembly")} {theme.TableBorder("|")} {theme.TableHeader("Summary")} {theme.TableBorder("|")}");
            sb.AppendLine(theme.TableBorder("| :--- | :--- | :--- | :--- | :--- |"));
            foreach (var r in res.Results)
            {
                var summary = r.Summary?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} `{theme.Symbol(r.Name)}` {theme.TableBorder("|")} {theme.Kind(r.Kind)} {theme.TableBorder("|")} `{theme.Member(r.Signature)}` {theme.TableBorder("|")} {theme.Assembly(r.Assembly ?? "")} {theme.TableBorder("|")} {summary} {theme.TableBorder("|")}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Search:")} '{theme.Symbol(res.Query)}' ({res.Results.Count} of {res.TotalMatches} matches):");
            foreach (var r in res.Results)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{theme.Kind(r.Kind)}] {theme.Symbol(r.Name)} ({theme.Assembly(r.Assembly ?? "")})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Dim("Signature:")} {theme.Member(r.Signature)}");
                if (!string.IsNullOrWhiteSpace(r.Summary))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Dim("Summary:")}   {r.Summary}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatExplore(ExploreTypeResponse res, string? format = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("##")} `{theme.Symbol(res.Type.FullName)}` ({theme.Kind(res.Type.Kind)})");
            if (res.Type.BaseType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Base Type:**")} `{theme.Symbol(res.Type.BaseType)}`");
            if (res.Type.Interfaces != null && res.Type.Interfaces.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Interfaces:**")} {string.Join(", ", res.Type.Interfaces.Select(i => $"`{theme.Symbol(i)}`"))}");
            }
            if (!string.IsNullOrWhiteSpace(res.Type.Summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("-")} {theme.Bold("**Summary:**")} {res.Type.Summary}");
            }
            sb.AppendLine();

            AppendMemberSectionMarkdown(sb, "Constructors", res.Constructors, theme);
            AppendMemberSectionMarkdown(sb, "Properties", res.Properties, theme);
            AppendMemberSectionMarkdown(sb, "Methods", res.Methods, theme);
            AppendMemberSectionMarkdown(sb, "Fields", res.Fields, theme);
            AppendMemberSectionMarkdown(sb, "Events", res.Events, theme);
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Bold("Type:")} {theme.Symbol(res.Type.FullName)} ({theme.Kind(res.Type.Kind)})");
            if (res.Type.BaseType != null) sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Dim("Base:")} {theme.Symbol(res.Type.BaseType)}");
            if (!string.IsNullOrWhiteSpace(res.Type.Summary)) sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Dim("Summary:")} {res.Type.Summary}");

            AppendMemberSectionText(sb, "Constructors", res.Constructors, theme);
            AppendMemberSectionText(sb, "Properties", res.Properties, theme);
            AppendMemberSectionText(sb, "Methods", res.Methods, theme);
            AppendMemberSectionText(sb, "Fields", res.Fields, theme);
            AppendMemberSectionText(sb, "Events", res.Events, theme);
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatDecompile(DecompileResponse res, string? format = null, bool useColor = false)
    {
        if (string.IsNullOrWhiteSpace(format)) format = GlobalOptions.GetDefaultFormat();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return NavJsonSerializer.Serialize(res);
        }

        var theme = useColor ? ColorTheme.Default : ColorTheme.None;
        var sb = new StringBuilder();
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(theme.Dim($"// Decompiled from: {res.Assembly}"));
            sb.AppendLine(theme.Dim($"// Target: {res.Type}{(res.Member != null ? "." + res.Member : "")}"));
            if (res.IsReferenceAssemblyOnly) sb.AppendLine(theme.Warning("// Warning: Reference assembly only (no method bodies)"));
            sb.AppendLine(theme.Dim("```csharp"));
            sb.AppendLine(res.Source);
            sb.AppendLine(theme.Dim("```"));
        }
        else
        {
            sb.AppendLine(theme.Dim($"// Decompiled from: {res.Assembly} ({res.Type})"));
            sb.AppendLine(res.Source);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMemberSectionMarkdown(StringBuilder sb, string title, IReadOnlyList<MemberSummaryDto>? members, ColorTheme theme)
    {
        if (members == null || members.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.Dim("###")} {theme.Bold(title)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} {theme.TableHeader("Member")} {theme.TableBorder("|")} {theme.TableHeader("Signature")} {theme.TableBorder("|")} {theme.TableHeader("Summary")} {theme.TableBorder("|")}");
        sb.AppendLine(theme.TableBorder("| :--- | :--- | :--- |"));
        foreach (var m in members)
        {
            var summary = m.Summary?.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) ?? "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{theme.TableBorder("|")} `{theme.Symbol(m.Name)}` {theme.TableBorder("|")} `{theme.Member(m.Signature)}` {theme.TableBorder("|")} {summary} {theme.TableBorder("|")}");
        }
        sb.AppendLine();
    }

    private static void AppendMemberSectionText(StringBuilder sb, string title, IReadOnlyList<MemberSummaryDto>? members, ColorTheme theme)
    {
        if (members == null || members.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"  {theme.Bold(title)}:");
        foreach (var m in members)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {theme.Member(m.Signature)}");
            if (!string.IsNullOrWhiteSpace(m.Summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {theme.Dim("// " + m.Summary)}");
            }
        }
    }
}
