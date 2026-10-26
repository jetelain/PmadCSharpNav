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

    [Fact]
    public void PathHelper_MakeRelative_Behaviors()
    {
        var baseDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var subFile = Path.Combine(baseDir, "subdir", "file.cs");
        var expectedRel = Path.Combine("subdir", "file.cs");

        PathHelper.MakeRelative(subFile, baseDir).Should().Be(expectedRel);
        PathHelper.MakeRelative("already/relative.cs", baseDir).Should().Be("already/relative.cs");
        PathHelper.MakeRelative(null, baseDir).Should().BeNull();
        PathHelper.MakeRelative("", baseDir).Should().Be("");
        PathHelper.MakeRelative("   ", baseDir).Should().Be("   ");
    }

    [Fact]
    public void FormatReferences_WithBaseDirectory_RelativizesPaths()
    {
        var baseDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var defFile = Path.Combine(baseDir, "MyApp", "MyClass.cs");
        var refFile = Path.Combine(baseDir, "MyApp", "Consumer.cs");
        var relDef = Path.Combine("MyApp", "MyClass.cs");
        var relRef = Path.Combine("MyApp", "Consumer.cs");

        var response = new FindReferencesResponse(
            Target: new ReferenceTargetDto(
                Name: "MyMethod",
                Kind: "Method",
                ContainingType: "MyApp.MyClass",
                DefinedIn: defFile,
                Line: 10),
            TotalReferences: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            References:
            [
                new ReferenceItemDto(
                    File: refFile,
                    Line: 20,
                    Column: 12,
                    Kind: "Invocation",
                    ContainingMember: "Run",
                    IsGenerated: false,
                    Snippet: "myClass.MyMethod();")
            ]);

        // Markdown format
        var mdOutput = OutputFormatter.FormatReferences(response, "markdown", baseDirectory: baseDir);
        mdOutput.Should().Contain($"- **Defined In:** `{relDef}:10`");
        mdOutput.Should().Contain($"| `{relRef}:20:12` | Invocation | Run |");

        // Text format
        var textOutput = OutputFormatter.FormatReferences(response, "text", baseDirectory: baseDir);
        textOutput.Should().Contain($"Defined In: {relDef}:10");
        textOutput.Should().Contain($"{relRef}:20:12 [Invocation]");

        // JSON format
        var jsonOutput = OutputFormatter.FormatReferences(response, "json", baseDirectory: baseDir);
        var escapedRef = relRef.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedDef = relDef.Replace("\\", "\\\\", StringComparison.Ordinal);
        jsonOutput.Should().Contain($"\"file\":\"{escapedRef}\"");
        jsonOutput.Should().Contain($"\"definedIn\":\"{escapedDef}\"");
    }

    [Fact]
    public void GlobalOptions_FormatOption_ReadsEnvironmentVariables()
    {
        var originalCsnavFormat = Environment.GetEnvironmentVariable("CSNAV_FORMAT");
        var originalDefaultFormat = Environment.GetEnvironmentVariable("CSNAV_DEFAULT_FORMAT");

        try
        {
            // Test CSNAV_FORMAT
            Environment.SetEnvironmentVariable("CSNAV_DEFAULT_FORMAT", null);
            Environment.SetEnvironmentVariable("CSNAV_FORMAT", "json");

            var globals = new GlobalOptions();
            var root = new RootCommand();
            globals.AddTo(root);

            var parseResult = root.Parse("");
            parseResult.GetValue(globals.FormatOption).Should().Be("json");

            // Explicit CLI argument overrides environment variable
            var overrideResult = root.Parse("--format text");
            overrideResult.GetValue(globals.FormatOption).Should().Be("text");

            // Test CSNAV_DEFAULT_FORMAT fallback
            Environment.SetEnvironmentVariable("CSNAV_FORMAT", null);
            Environment.SetEnvironmentVariable("CSNAV_DEFAULT_FORMAT", "text");

            var globals2 = new GlobalOptions();
            var root2 = new RootCommand();
            globals2.AddTo(root2);

            var parseResult2 = root2.Parse("");
            parseResult2.GetValue(globals2.FormatOption).Should().Be("text");

            // Test invalid environment variable value falls back to markdown
            Environment.SetEnvironmentVariable("CSNAV_FORMAT", "invalid_format");
            GlobalOptions.GetDefaultFormat().Should().Be("markdown");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSNAV_FORMAT", originalCsnavFormat);
            Environment.SetEnvironmentVariable("CSNAV_DEFAULT_FORMAT", originalDefaultFormat);
        }
    }

    [Fact]
    public void OutputFormatter_ColorsInTextAndMarkdown()
    {
        var response = new FindReferencesResponse(
            Target: new ReferenceTargetDto("MyMethod", "Method"),
            TotalReferences: 1,
            Offset: 0,
            Limit: 50,
            Truncated: false,
            References:
            [
                new ReferenceItemDto(
                    File: "Consumer.cs",
                    Line: 20,
                    Column: 12,
                    Kind: "Invocation",
                    ContainingMember: "Run",
                    Snippet: "myClass.MyMethod();")
            ]);

        // Text format with color
        var textColored = OutputFormatter.FormatReferences(response, "text", useColor: true);
        textColored.Should().Contain("\x1b[");

        // Text format without color
        var textUncolored = OutputFormatter.FormatReferences(response, "text", useColor: false);
        textUncolored.Should().NotContain("\x1b[");

        // Markdown format with color
        var mdColored = OutputFormatter.FormatReferences(response, "markdown", useColor: true);
        mdColored.Should().Contain("\x1b[");

        // Markdown format without color
        var mdUncolored = OutputFormatter.FormatReferences(response, "markdown", useColor: false);
        mdUncolored.Should().NotContain("\x1b[");
    }

    [Fact]
    public void ConsoleColorizer_RespectsNoColorOptionAndEnvVar()
    {
        var originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            // When noColorOption is true, ShouldUseColor should be false
            ConsoleColorizer.ShouldUseColor(noColorOption: true).Should().BeFalse();

            // When NO_COLOR environment variable is set, ShouldUseColor should be false
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            ConsoleColorizer.ShouldUseColor(noColorOption: false).Should().BeFalse();

            Environment.SetEnvironmentVariable("NO_COLOR", "");
            // In unit tests, if console is redirected, ShouldUseColor is false; otherwise true
            // The important thing is it didn't throw and respected noColorOption
            ConsoleColorizer.ShouldUseColor(noColorOption: true).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);
        }
    }
}
