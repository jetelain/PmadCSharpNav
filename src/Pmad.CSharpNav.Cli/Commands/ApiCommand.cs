using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Commands;

public static class ApiCommand
{
    public static Command Create(GlobalOptions globals, NavDaemonClient client)
    {
        var patternArg = new Argument<string?>("pattern") { Description = "Symbol name or search pattern (wildcards * supported, e.g. *Repository)", Arity = ArgumentArity.ZeroOrOne };
        var docOption = new Option<string?>("--doc") { Description = "Full-text search across XML documentation comments (summary, param, returns)" };
        var scopeOption = new Option<string>("--scope") { Description = "Search scope: all (default), source, dependencies, or assembly name", DefaultValueFactory = _ => "all" };
        var kindOption = new Option<string>("--kind") { Description = "Filter by symbol kind: type, method, property, field, event, or all", DefaultValueFactory = _ => "all" };
        var accessOption = new Option<string>("--access") { Description = "Filter by accessibility: public (default), internal, or all", DefaultValueFactory = _ => "public" };
        var limitOption = new Option<int>("--limit") { Description = "Maximum number of results to return", DefaultValueFactory = _ => 50 };
        var offsetOption = new Option<int>("--offset") { Description = "Offset for pagination", DefaultValueFactory = _ => 0 };

        var cmd = new Command("api", "Search public API signatures and symbols");
        cmd.Add(patternArg);
        cmd.Add(docOption);
        cmd.Add(scopeOption);
        cmd.Add(kindOption);
        cmd.Add(accessOption);
        cmd.Add(limitOption);
        cmd.Add(offsetOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var pattern = parseResult.GetValue(patternArg);
            var doc = parseResult.GetValue(docOption);
            var scope = parseResult.GetValue(scopeOption) ?? "all";
            var kind = parseResult.GetValue(kindOption) ?? "all";
            var access = parseResult.GetValue(accessOption) ?? "public";
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);

            var specifiedSolution = parseResult.GetValue(globals.SolutionOption);
            var framework = parseResult.GetValue(globals.FrameworkOption);
            var format = parseResult.GetValue(globals.FormatOption) ?? "json";

            if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(doc))
            {
                await Console.Error.WriteLineAsync("Error: Either search pattern or --doc must be specified.");
                return 1;
            }

            try
            {
                var solutionPath = NavDaemonClient.ResolveSolutionPath(specifiedSolution);
                var req = new SearchApiRequest(
                    Pattern: pattern,
                    Doc: doc,
                    Scope: scope,
                    Kind: kind.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : kind,
                    Access: access,
                    Framework: framework,
                    Limit: limit,
                    Offset: offset);

                var response = await client.PostAsync<SearchApiRequest, SearchApiResponse>(
                    solutionPath, "/api/api-search", req, ct);

                var output = OutputFormatter.FormatApiSearch(response, format);
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
