using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class ExploreCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var typeOption = new Option<string?>("--type") { Description = "Fully qualified type name (e.g. MyNamespace.MyClass)" };
        var fileOption = new Option<string?>("--file") { Description = "Path to C# source file containing type definition" };
        var lineOption = new Option<int?>("--line") { Description = "1-indexed line number in source file" };
        var colOption = new Option<int?>("--column") { Description = "1-indexed column number in source file" };
        var noInheritedOption = new Option<bool>("--no-inherited") { Description = "Exclude inherited members (show only declared members)", DefaultValueFactory = _ => false };
        var accessOption = new Option<string>("--access") { Description = "Filter by minimum accessibility: public (default), internal, private, or all", DefaultValueFactory = _ => "public" };
        var limitOption = new Option<int>("--limit") { Description = "Maximum number of members to return", DefaultValueFactory = _ => NavConstants.DefaultLimit };
        var offsetOption = new Option<int>("--offset") { Description = "Offset for pagination", DefaultValueFactory = _ => 0 };

        var cmd = new Command("explore", "Inspect type members, constructors, properties, methods, and hierarchy");
        cmd.Add(typeOption);
        cmd.Add(fileOption);
        cmd.Add(lineOption);
        cmd.Add(colOption);
        cmd.Add(noInheritedOption);
        cmd.Add(accessOption);
        cmd.Add(limitOption);
        cmd.Add(offsetOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var typeName = parseResult.GetValue(typeOption);
            var file = parseResult.GetValue(fileOption);
            var line = parseResult.GetValue(lineOption);
            var col = parseResult.GetValue(colOption);
            var noInherited = parseResult.GetValue(noInheritedOption);
            var access = parseResult.GetValue(accessOption) ?? "public";
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);

            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var framework = parseResult.GetValue(globals.FrameworkOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? "markdown";

            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(file))
            {
                await Console.Error.WriteLineAsync("Error: Either --type or --file must be specified.");
                return 1;
            }

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var req = new ExploreTypeRequest(
                    Type: typeName,
                    File: file != null ? Path.GetFullPath(file) : null,
                    Line: line,
                    Column: col,
                    NoInherited: noInherited,
                    Access: access,
                    Framework: framework,
                    Limit: limit,
                    Offset: offset);

                var response = await client.PostAsync<ExploreTypeRequest, ExploreTypeResponse>(
                    solutionPath, "/api/explore", req, ct);

                var output = OutputFormatter.FormatExplore(response, format);
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
