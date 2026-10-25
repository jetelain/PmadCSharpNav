using System.CommandLine;
using AwesomeAssertions;
using Pmad.CSharpNav.Cli.Commands;
using Pmad.CSharpNav.Cli.Formatting;
using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.IntegrationTests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void GlobalOptions_FormatOption_DefaultsToMarkdown()
    {
        var globals = new GlobalOptions();
        var root = new RootCommand();
        globals.AddTo(root);

        var parseResult = root.Parse("");
        var format = parseResult.GetValue(globals.FormatOption);

        format.Should().Be("markdown");
    }

    [Fact]
    public void FormatReferences_DefaultsToMarkdown()
    {
        var response = new FindReferencesResponse(
            Target: new ReferenceTargetDto(
                Name: "MyMethod",
                Kind: "Method",
                ContainingType: "MyApp.MyClass",
                DefinedIn: "C:\\MyApp\\MyClass.cs",
                Line: 10),
            TotalReferences: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            References:
            [
                new ReferenceItemDto(
                    File: "C:\\MyApp\\Consumer.cs",
                    Line: 20,
                    Column: 12,
                    Kind: "Invocation",
                    ContainingMember: "Run",
                    IsGenerated: false,
                    Snippet: "myClass.MyMethod();")
            ]);

        // Default when format is null/omitted
        var defaultOutput = OutputFormatter.FormatReferences(response);
        defaultOutput.Should().StartWith("### References for `MyMethod` (Method)");
        defaultOutput.Should().Contain("| Location | Kind | Containing Member | Snippet |");
        defaultOutput.Should().Contain("| `C:\\MyApp\\Consumer.cs:20:12` | Invocation | Run | `myClass.MyMethod();` |");

        // Explicit markdown
        var markdownOutput = OutputFormatter.FormatReferences(response, "markdown");
        markdownOutput.Should().Be(defaultOutput);

        // JSON format
        var jsonOutput = OutputFormatter.FormatReferences(response, "json");
        jsonOutput.Should().StartWith("{").And.Contain("\"totalReferences\":1");

        // Text format
        var textOutput = OutputFormatter.FormatReferences(response, "text");
        textOutput.Should().StartWith("Target: MyMethod (Method)");
        textOutput.Should().NotContain("| Location |");
    }

    [Fact]
    public void FormatApiSearch_DefaultsToMarkdown()
    {
        var response = new SearchApiResponse(
            Query: "*Repository",
            Scope: "all",
            TotalMatches: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            Results:
            [
                new ApiSearchResultDto(
                    Name: "IOrderRepository",
                    Kind: "Interface",
                    Signature: "interface IOrderRepository",
                    Assembly: "MyApp.Core",
                    Summary: "Order repository contract.")
            ]);

        // Default when format is null/omitted
        var defaultOutput = OutputFormatter.FormatApiSearch(response);
        defaultOutput.Should().StartWith("### API Search Results for `*Repository` (Total: 1)");
        defaultOutput.Should().Contain("| Symbol | Kind | Signature | Assembly | Summary |");
        defaultOutput.Should().Contain("| `IOrderRepository` | Interface | `interface IOrderRepository` | MyApp.Core | Order repository contract. |");

        // Explicit markdown
        var markdownOutput = OutputFormatter.FormatApiSearch(response, "markdown");
        markdownOutput.Should().Be(defaultOutput);

        // JSON format
        var jsonOutput = OutputFormatter.FormatApiSearch(response, "json");
        jsonOutput.Should().StartWith("{").And.Contain("\"totalMatches\":1");

        // Text format
        var textOutput = OutputFormatter.FormatApiSearch(response, "text");
        textOutput.Should().StartWith("Search: '*Repository'");
        textOutput.Should().NotContain("| Symbol |");
    }

    [Fact]
    public void FormatExplore_DefaultsToMarkdown()
    {
        var response = new ExploreTypeResponse(
            Type: new TypeSummaryDto(
                Name: "OrderService",
                FullName: "MyApp.OrderService",
                Kind: "Class",
                Accessibility: "Public",
                Modifiers: ["public"],
                BaseType: "System.Object",
                Interfaces: ["MyApp.IOrderService"],
                Summary: "Handles orders."),
            Constructors:
            [
                new MemberSummaryDto(
                    Name: "OrderService",
                    Kind: "Constructor",
                    Signature: "OrderService()")
            ],
            Methods:
            [
                new MemberSummaryDto(
                    Name: "ProcessOrder",
                    Kind: "Method",
                    Signature: "void ProcessOrder(int id)",
                    Summary: "Processes an order.")
            ],
            Properties: [],
            Fields: [],
            Events: []);

        // Default when format is null/omitted
        var defaultOutput = OutputFormatter.FormatExplore(response);
        defaultOutput.Should().StartWith("## `MyApp.OrderService` (Class)");
        defaultOutput.Should().Contain("- **Base Type:** `System.Object`");
        defaultOutput.Should().Contain("- **Interfaces:** `MyApp.IOrderService`");
        defaultOutput.Should().Contain("### Methods");
        defaultOutput.Should().Contain("| `ProcessOrder` | `void ProcessOrder(int id)` | Processes an order. |");

        // Explicit markdown
        var markdownOutput = OutputFormatter.FormatExplore(response, "markdown");
        markdownOutput.Should().Be(defaultOutput);

        // JSON format
        var jsonOutput = OutputFormatter.FormatExplore(response, "json");
        jsonOutput.Should().StartWith("{").And.Contain("\"fullName\":\"MyApp.OrderService\"");

        // Text format
        var textOutput = OutputFormatter.FormatExplore(response, "text");
        textOutput.Should().StartWith("Type: MyApp.OrderService (Class)");
        textOutput.Should().NotContain("## `MyApp.OrderService`");
    }

    [Fact]
    public void FormatDecompile_DefaultsToMarkdown()
    {
        var response = new DecompileResponse(
            Assembly: "System.Private.CoreLib",
            Type: "System.Uri",
            Member: "IsLoopback",
            LanguageVersion: "CSharp12",
            Source: "public bool IsLoopback => true;",
            IsReferenceAssemblyOnly: false);

        // Default when format is null/omitted
        var defaultOutput = OutputFormatter.FormatDecompile(response);
        defaultOutput.Should().Contain("// Decompiled from: System.Private.CoreLib");
        defaultOutput.Should().Contain("```csharp");
        defaultOutput.Should().Contain("public bool IsLoopback => true;");
        defaultOutput.Should().Contain("```");

        // Explicit markdown
        var markdownOutput = OutputFormatter.FormatDecompile(response, "markdown");
        markdownOutput.Should().Be(defaultOutput);

        // JSON format
        var jsonOutput = OutputFormatter.FormatDecompile(response, "json");
        jsonOutput.Should().StartWith("{").And.Contain("\"assembly\":\"System.Private.CoreLib\"");

        // Text format
        var textOutput = OutputFormatter.FormatDecompile(response, "text");
        textOutput.Should().NotContain("```csharp");
        textOutput.Should().Contain("public bool IsLoopback => true;");
    }
}
