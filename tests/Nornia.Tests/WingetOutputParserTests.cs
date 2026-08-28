using Nornia.Core.Models;
using Nornia.Package.Parsing;
using Nornia.Package.Providers;

namespace Nornia.Tests;

/// <summary>Parser-level tests over golden fixtures and synthetic variants of winget's tabular and
/// structured output. See <c>Fixtures/Winget</c> for real captured samples (Simplified Chinese
/// system locale, including spinner/progress-bar noise lines before the table).</summary>
public sealed class WingetOutputParserTests
{
    private static readonly WingetTableParser Table = new();
    private static readonly WingetJsonParser Json = new();

    [Fact]
    public void Table_ParsesEnglishHeaderPreservingDoubleSpacesInName()
    {
        int[] widths = [30, 18, 9, 12, 8];
        var output = FixWidthTable(widths,
            ["Name", "Id", "Version", "Available", "Source"],
            [["Hello  World  Package", "Example.Hello", "1.2.3", "", "winget"]]);

        var result = Table.Parse(output, isInstalled: true);

        var package = Assert.Single(result.Packages);
        Assert.Equal("Example.Hello", package.Id);
        Assert.Equal("Hello  World  Package", package.Name);
        Assert.Equal("1.2.3", package.Version);
        Assert.Null(package.AvailableVersion);
        Assert.Equal("winget", package.Provider);
        Assert.Equal("en", result.HeaderLanguage);
        Assert.Equal(1, result.ParsedRowCount);
        Assert.Equal(0, result.SkippedRowCount);
    }

    [Fact]
    public void Table_ParsesRealChineseFixtureWithNoiseLines()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Winget", "list_zh_4col.txt"));

        var result = Table.Parse(output, isInstalled: true);

        Assert.Equal("zh-Hans", result.HeaderLanguage);
        Assert.True(result.ParsedRowCount > 100, $"expected >100 rows, parsed {result.ParsedRowCount}");
        Assert.Equal(0, result.SkippedRowCount);
        Assert.Contains(result.Packages, package => package.Id == "Bandisoft.Bandizip" && package.Name == "Bandizip" && package.Version == "7.46");
        Assert.Contains(result.Packages, package => package.Id == "Git.Git" && package.Version == "2.55.0.3");
    }

    [Fact]
    public void Table_EmptyAvailableFieldBecomesNullAvailableVersion()
    {
        int[] widths = [30, 18, 9, 12, 8];
        var output = FixWidthTable(widths,
            ["Name", "Id", "Version", "Available", "Source"],
            [["Git", "Git.Git", "2.50.0", "", "winget"]]);

        var package = Assert.Single(Table.Parse(output, isInstalled: true).Packages);

        Assert.Null(package.AvailableVersion);
        Assert.Equal("2.50.0", package.Version);
    }

    [Fact]
    public void Table_IgnoresCrSpinnerNoiseBeforeSeparator()
    {
        int[] widths = [30, 18, 9, 12, 8];
        var table = FixWidthTable(widths,
            ["Name", "Id", "Version", "Available", "Source"],
            [["Git", "Git.Git", "2.50.0", "2.51.0", "winget"]]);
        var output = "   - \r   \\ \r   | \r   / \r\n" + table;

        var result = Table.Parse(output, isInstalled: true);

        var package = Assert.Single(result.Packages);
        Assert.Equal("2.51.0", package.AvailableVersion);
    }

    [Fact]
    public void Table_ArpRowWithoutSourceIsLabeledLocal()
    {
        // 来源列为空的 ARP 条目(本机安装,不在 winget/msstore 目录)不得默认标为 "winget"。
        int[] widths = [30, 34, 12, 10, 8];
        var output = FixWidthTable(widths,
            ["名称", "ID", "版本", "可用", "源"],
            [["Firewall App", "ARP\\Machine\\X64\\Firewall", "2.0.0", "", ""]]);

        var package = Assert.Single(Table.Parse(output, isInstalled: true).Packages);

        Assert.Equal("Firewall App", package.Name);
        Assert.Equal("本机", package.Provider);
    }

    [Fact]
    public void Table_UnknownHeaderLanguageFallsBackToColumnOrder()
    {
        const string output = """
            名前  ID  バージョン  ソース
            ---------------------------------
            TestApp  com.example.Test  1.0.0  winget
            """;

        var result = Table.Parse(output, isInstalled: false);

        Assert.Null(result.HeaderLanguage);
        var package = Assert.Single(result.Packages);
        Assert.Equal("TestApp", package.Name);
        Assert.Equal("com.example.Test", package.Id);
        Assert.Equal("1.0.0", package.Version);
        Assert.Null(package.AvailableVersion);
        Assert.Equal("winget", package.Provider);
    }

    [Fact]
    public void Table_UnknownFourColumnInstalledRowDoesNotTreatSourceAsAvailable()
    {
        const string output = """
            Nom  ID  Version  Source
            -------------------------
            Tool  com.example.Tool  2.0.0  winget
            """;

        var package = Assert.Single(Table.Parse(output, isInstalled: true).Packages);

        Assert.Null(package.AvailableVersion);
        Assert.Equal("winget", package.Provider);
    }

    [Fact]
    public void Table_OutputWithoutSeparatorReturnsEmpty()
    {
        var result = Table.Parse("some stray output\nwithout a table", isInstalled: false);

        Assert.Empty(result.Packages);
        Assert.Equal(0, result.SkippedRowCount);
    }

    [Fact]
    public void Json_ParsesWingetExportShape()
    {
        const string json = """
            {
              "Sources": [
                {
                  "Name": "winget",
                  "Type": "Microsoft.PreIndexed.Package",
                  "Packages": [
                    { "PackageIdentifier": "Git.Git", "PackageVersion": "2.50.0", "AvailableVersion": "2.51.0", "Name": "Git" },
                    { "PackageIdentifier": "Python.Python.3.13", "PackageVersion": "3.13.2" }
                  ]
                }
              ]
            }
            """;

        var result = Json.Parse(json, isInstalled: true);

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal("Git.Git", result.Packages[0].Id);
        Assert.Equal("2.51.0", result.Packages[0].AvailableVersion);
        Assert.Equal("Python.Python.3.13", result.Packages[1].Name);
        Assert.Equal("json", result.HeaderLanguage);
        Assert.Equal(0, result.SkippedRowCount);
    }

    [Fact]
    public void Json_MalformedOrDriftedSchemaReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(Json.Parse("{ not json", isInstalled: true).Packages);
        Assert.Empty(Json.Parse("{\"Other\":{\"field\":1}}", isInstalled: true).Packages);
    }

    [Fact]
    public void Factory_SelectsTableParserWithoutStructuredOutputSupport()
    {
        var parser = WingetOutputParserFactory.Create(new WingetCapabilities(null, false, null, new HashSet<int>()));
        Assert.Equal("table", parser.Kind);
    }

    [Fact]
    public void Factory_SelectsJsonParserWhenStructuredOutputIsSupported()
    {
        var parser = WingetOutputParserFactory.Create(new WingetCapabilities(new Version(1, 10, 340), true, null, new HashSet<int>()));
        Assert.Equal("json", parser.Kind);
    }

    /// <summary>Builds a fixed-width table whose column starts are the cumulative cell widths, so
    /// header token offsets and data alignment stay consistent without hand-counting spaces.</summary>
    private static string FixWidthTable(int[] widths, string[] headers, params string[][] rows)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(PadColumns(widths, headers));
        builder.AppendLine(new string('-', widths.Sum()));
        foreach (var row in rows)
        {
            builder.AppendLine(PadColumns(widths, row));
        }

        return builder.ToString();
    }

    private static string PadColumns(int[] widths, string[] cells)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < cells.Length; index++)
        {
            builder.Append(cells[index]);
            if (index < cells.Length - 1)
            {
                builder.Append(new string(' ', Math.Max(0, widths[index] - DisplayWidth(cells[index]))));
            }
        }

        return builder.ToString();
    }

    /// <summary>CJK header words occupy two display columns, so padding must count display width.</summary>
    private static int DisplayWidth(string value) => value.Sum(DisplayWidth);

    private static int DisplayWidth(char character) => character is >= '\u2E80' and <= '\uA4CF' ? 2 : 1;
}