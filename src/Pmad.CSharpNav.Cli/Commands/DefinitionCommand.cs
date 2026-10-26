using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class DefinitionCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var symbolOption = new Option<string?>("--symbol") { Description = "Symbol name or documentation comment ID (e.g. M:MyNamespace.MyClass.MyMethod)" };
        var fileOption = new Option<string?>("--file") { Description = "Path to C# source file" };
        var lineOption = new Option<int?>("--line") { Description = "1-indexed line number in source file" };
        var colOption = new Option<int?>("--column") { Description = "1-indexed column number in source file" };
        var projectOption = new Option<string?>("--project") { Description = "Filter definitions to a specific project name" };
        var includeGenOption = new Option<bool>("--include-generated") { Description = "Include definitions in generated files", DefaultValueFactory = _ => false };
        var limitOption = new Option<int>("--limit") { Description = "Maximum number of definitions to return", DefaultValueFactory = _ => NavConstants.DefaultLimit };
        var offsetOption = new Option<int>("--offset") { Description = "Offset for pagination", DefaultValueFactory = _ => 0 };

        var cmd = new Command("definition", "Go to definition of a symbol or source location");
        cmd.Aliases.Add("definitions");
        cmd.Aliases.Add("def");
        cmd.Aliases.Add("goto-def");
        cmd.Aliases.Add("find-def");

        cmd.Add(symbolOption);
        cmd.Add(fileOption);
        cmd.Add(lineOption);
        cmd.Add(colOption);
        cmd.Add(projectOption);
        cmd.Add(includeGenOption);
        cmd.Add(limitOption);
        cmd.Add(offsetOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var symbol = parseResult.GetValue(symbolOption);
            var file = parseResult.GetValue(fileOption);
            var line = parseResult.GetValue(lineOption);
            var col = parseResult.GetValue(colOption);
            var project = parseResult.GetValue(projectOption);
            var includeGen = parseResult.GetValue(includeGenOption);
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);

            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var framework = parseResult.GetValue(globals.FrameworkOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? GlobalOptions.GetDefaultFormat();
            var noColor = parseResult.GetValue(globals.NoColorOption);
            var useColor = ConsoleColorizer.ShouldUseColor(noColor);
            var theme = useColor ? ColorTheme.Default : ColorTheme.None;

            if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(file))
            {
                await Console.Error.WriteLineAsync($"{theme.Error("Error:")} Either --symbol or --file (with optional --line and --column) must be specified.");
                return 1;
            }

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var req = new FindDefinitionRequest(
                    Symbol: symbol,
                    File: file != null ? Path.GetFullPath(file) : null,
                    Line: line,
                    Column: col,
                    Project: project,
                    Framework: framework,
                    IncludeGenerated: includeGen,
                    Limit: limit,
                    Offset: offset);

                var response = await client.PostAsync<FindDefinitionRequest, FindDefinitionResponse>(
                    solutionPath, "/api/definition", req, ct);

                var output = OutputFormatter.FormatDefinition(response, format, baseDirectory: null, useColor: useColor);
                Console.WriteLine(output);
                return 0;
            }
            catch (NavClientException ex)
            {
                if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    var relCandidates = ex.Candidates?.Select(c => c with { DefinedIn = PathHelper.MakeRelative(c.DefinedIn) }).ToList();
                    var errResponse = new ErrorResponse(Error: ex.Code, Message: ex.Message, Code: ex.Code, Candidates: relCandidates);
                    Console.WriteLine(NavJsonSerializer.Serialize(errResponse));
                }
                else
                {
                    await Console.Error.WriteLineAsync($"{theme.Error($"Error [{ex.Code}]:")} {ex.Message}");
                    if (ex.Candidates != null && ex.Candidates.Count > 0)
                    {
                        await Console.Error.WriteLineAsync(theme.Bold("Matching candidates:"));
                        foreach (var c in ex.Candidates)
                        {
                            var relDef = PathHelper.MakeRelative(c.DefinedIn);
                            var loc = relDef != null ? $" ({theme.Location(relDef)}:{c.Line})" : "";
                            var id = c.SymbolId != null ? $" [ID: {theme.Dim(c.SymbolId)}]" : "";
                            await Console.Error.WriteLineAsync($"  - {theme.Symbol(c.Signature)}{loc}{id}");
                        }
                    }
                }
                return 1;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{theme.Error("Error:")} {ex.Message}");
                return 1;
            }
        });

        return cmd;
    }
}
