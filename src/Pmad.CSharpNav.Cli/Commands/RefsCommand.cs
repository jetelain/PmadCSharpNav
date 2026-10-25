using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class RefsCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var symbolOption = new Option<string?>("--symbol") { Description = "Symbol name or documentation comment ID (e.g. M:MyNamespace.MyClass.MyMethod)" };
        var fileOption = new Option<string?>("--file") { Description = "Path to C# source file" };
        var lineOption = new Option<int?>("--line") { Description = "1-indexed line number in source file" };
        var colOption = new Option<int?>("--column") { Description = "1-indexed column number in source file" };
        var excludeTestsOption = new Option<bool>("--exclude-tests") { Description = "Filter out references residing in test projects", DefaultValueFactory = _ => false };
        var projectOption = new Option<string?>("--project") { Description = "Filter references to a specific project name" };
        var includeGenOption = new Option<bool>("--include-generated") { Description = "Include references in generated files", DefaultValueFactory = _ => false };
        var includeOverridesOption = new Option<bool>("--include-overrides") { Description = "Include virtual method overrides", DefaultValueFactory = _ => true };
        var includeImplementationsOption = new Option<bool>("--include-implementations") { Description = "Include interface implementations", DefaultValueFactory = _ => true };
        var limitOption = new Option<int>("--limit") { Description = "Maximum number of references to return", DefaultValueFactory = _ => NavConstants.DefaultLimit };
        var offsetOption = new Option<int>("--offset") { Description = "Offset for pagination", DefaultValueFactory = _ => 0 };

        var cmd = new Command("refs", "Find references to a symbol or source location");
        cmd.Add(symbolOption);
        cmd.Add(fileOption);
        cmd.Add(lineOption);
        cmd.Add(colOption);
        cmd.Add(excludeTestsOption);
        cmd.Add(projectOption);
        cmd.Add(includeGenOption);
        cmd.Add(includeOverridesOption);
        cmd.Add(includeImplementationsOption);
        cmd.Add(limitOption);
        cmd.Add(offsetOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var symbol = parseResult.GetValue(symbolOption);
            var file = parseResult.GetValue(fileOption);
            var line = parseResult.GetValue(lineOption);
            var col = parseResult.GetValue(colOption);
            var excludeTests = parseResult.GetValue(excludeTestsOption);
            var project = parseResult.GetValue(projectOption);
            var includeGen = parseResult.GetValue(includeGenOption);
            var includeOverrides = parseResult.GetValue(includeOverridesOption);
            var includeImplementations = parseResult.GetValue(includeImplementationsOption);
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);

            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var framework = parseResult.GetValue(globals.FrameworkOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? "json";

            if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(file))
            {
                await Console.Error.WriteLineAsync("Error: Either --symbol or --file (with optional --line and --column) must be specified.");
                return 1;
            }

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var req = new FindReferencesRequest(
                    Symbol: symbol,
                    File: file != null ? Path.GetFullPath(file) : null,
                    Line: line,
                    Column: col,
                    ExcludeTests: excludeTests,
                    Project: project,
                    Framework: framework,
                    IncludeGenerated: includeGen,
                    IncludeOverrides: includeOverrides,
                    IncludeImplementations: includeImplementations,
                    Limit: limit,
                    Offset: offset);

                var response = await client.PostAsync<FindReferencesRequest, FindReferencesResponse>(
                    solutionPath, "/api/references", req, ct);

                var output = OutputFormatter.FormatReferences(response, format);
                Console.WriteLine(output);
                return 0;
            }
            catch (NavClientException ex)
            {
                if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    var errResponse = new ErrorResponse(Error: ex.Code, Message: ex.Message, Code: ex.Code, Candidates: ex.Candidates);
                    Console.WriteLine(NavJsonSerializer.Serialize(errResponse));
                }
                else
                {
                    await Console.Error.WriteLineAsync($"Error [{ex.Code}]: {ex.Message}");
                    if (ex.Candidates != null && ex.Candidates.Count > 0)
                    {
                        await Console.Error.WriteLineAsync("Matching candidates:");
                        foreach (var c in ex.Candidates)
                        {
                            var loc = c.DefinedIn != null ? $" ({c.DefinedIn}:{c.Line})" : "";
                            var id = c.SymbolId != null ? $" [ID: {c.SymbolId}]" : "";
                            await Console.Error.WriteLineAsync($"  - {c.Signature}{loc}{id}");
                        }
                    }
                }
                return 1;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Error: {ex.Message}");
                return 1;
            }
        });

        return cmd;
    }
}
