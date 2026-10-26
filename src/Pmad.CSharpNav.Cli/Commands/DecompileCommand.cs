using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class DecompileCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var assemblyOption = new Option<string?>("--assembly") { Description = "Name of external assembly (e.g. System.Text.Json)" };
        var typeOption = new Option<string?>("--type") { Description = "Type name to decompile (e.g. System.Text.Json.JsonSerializer)" };
        var memberOption = new Option<string?>("--member") { Description = "Specific member name to decompile" };
        var symbolOption = new Option<string?>("--symbol") { Description = "Documentation comment ID or symbol name to decompile" };
        var fileOption = new Option<string?>("--file") { Description = "Path to source file containing symbol reference" };
        var lineOption = new Option<int?>("--line") { Description = "1-indexed line in source file" };
        var colOption = new Option<int?>("--column") { Description = "1-indexed column in source file" };

        var cmd = new Command("decompile", "Decompile metadata / external library symbol to readable C# source");
        cmd.Add(assemblyOption);
        cmd.Add(typeOption);
        cmd.Add(memberOption);
        cmd.Add(symbolOption);
        cmd.Add(fileOption);
        cmd.Add(lineOption);
        cmd.Add(colOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var assembly = parseResult.GetValue(assemblyOption);
            var type = parseResult.GetValue(typeOption);
            var member = parseResult.GetValue(memberOption);
            var symbol = parseResult.GetValue(symbolOption);
            var file = parseResult.GetValue(fileOption);
            var line = parseResult.GetValue(lineOption);
            var col = parseResult.GetValue(colOption);

            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var framework = parseResult.GetValue(globals.FrameworkOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? GlobalOptions.GetDefaultFormat();
            var noColor = parseResult.GetValue(globals.NoColorOption);
            var useColor = ConsoleColorizer.ShouldUseColor(noColor);
            var theme = useColor ? ColorTheme.Default : ColorTheme.None;

            if (string.IsNullOrWhiteSpace(assembly) && string.IsNullOrWhiteSpace(type) &&
                string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(file))
            {
                await Console.Error.WriteLineAsync($"{theme.Error("Error:")} Target must be specified via --type/--assembly, --symbol, or --file.");
                return 1;
            }

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var req = new DecompileRequest(
                    Assembly: assembly,
                    Type: type,
                    Member: member,
                    Symbol: symbol,
                    File: file != null ? Path.GetFullPath(file) : null,
                    Line: line,
                    Column: col,
                    Framework: framework);

                var response = await client.PostAsync<DecompileRequest, DecompileResponse>(
                    solutionPath, "/api/decompile", req, ct);

                var output = OutputFormatter.FormatDecompile(response, format, useColor: useColor);
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
                    await Console.Error.WriteLineAsync($"{theme.Error($"Error [{ex.Code}]:")} {ex.Message}");
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
