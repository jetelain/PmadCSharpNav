using System.CommandLine;
using AwesomeAssertions;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Commands;

namespace Pmad.CSharpNav.IntegrationTests;

public sealed class CliCommandVerbTests
{
    private static RootCommand CreateTestRoot()
    {
        var root = new RootCommand("Test csnav CLI");
        var globals = new GlobalOptions();
        globals.AddTo(root);

        using var client = new NavDaemonClient();
        root.Add(ReferencesCommand.Create(globals, client));
        root.Add(SearchCommand.Create(globals, client));
        root.Add(InspectCommand.Create(globals, client));
        root.Add(DecompileCommand.Create(globals, client));
        return root;
    }

    [Theory]
    [InlineData("references --symbol MyClass")]
    [InlineData("refs --symbol MyClass")]
    [InlineData("find-refs --symbol MyClass")]
    public void References_Command_And_Aliases_Parse_Successfully(string commandLine)
    {
        var root = CreateTestRoot();
        var parseResult = root.Parse(commandLine);

        parseResult.Errors.Should().BeEmpty();
        parseResult.CommandResult.Command.Name.Should().Be("references");
    }

    [Theory]
    [InlineData("search MySymbol")]
    [InlineData("api MySymbol")]
    [InlineData("find MySymbol")]
    public void Search_Command_And_Aliases_Parse_Successfully(string commandLine)
    {
        var root = CreateTestRoot();
        var parseResult = root.Parse(commandLine);

        parseResult.Errors.Should().BeEmpty();
        parseResult.CommandResult.Command.Name.Should().Be("search");
    }

    [Theory]
    [InlineData("inspect --type MyNamespace.MyClass")]
    [InlineData("explore --type MyNamespace.MyClass")]
    [InlineData("type --type MyNamespace.MyClass")]
    public void Inspect_Command_And_Aliases_Parse_Successfully(string commandLine)
    {
        var root = CreateTestRoot();
        var parseResult = root.Parse(commandLine);

        parseResult.Errors.Should().BeEmpty();
        parseResult.CommandResult.Command.Name.Should().Be("inspect");
    }

#pragma warning disable CS0618 // Type or member is obsolete
    [Fact]
    public void Obsolete_Command_Factories_Remain_Functional()
    {
        var globals = new GlobalOptions();
        using var client = new NavDaemonClient();

        var refs = RefsCommand.Create(globals, client);
        refs.Name.Should().Be("references");
        refs.Aliases.Should().Contain("refs");

        var api = ApiCommand.Create(globals, client);
        api.Name.Should().Be("search");
        api.Aliases.Should().Contain("api");

        var explore = ExploreCommand.Create(globals, client);
        explore.Name.Should().Be("inspect");
        explore.Aliases.Should().Contain("explore");
    }
#pragma warning restore CS0618
}
