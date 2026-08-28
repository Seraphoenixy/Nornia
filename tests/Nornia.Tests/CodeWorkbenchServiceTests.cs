using Nornia.Desktop;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>File-type resolution (extension → language / highlighting / outline kind) with a safe
/// plain-text fallback for unknown types.</summary>
public sealed class CodeFileTypeRegistryTests
{
    [Theory]
    [InlineData("Program.cs", "csharp", "C#", "C#", CodeOutlineKind.CSharp)]
    [InlineData("MainWindow.xaml", "xml", "XAML", "XML", CodeOutlineKind.Xml)]
    [InlineData("app.config", "xml", "XML", "XML", CodeOutlineKind.Xml)]
    [InlineData("package.json", "json", "JSON", "", CodeOutlineKind.Json)]
    [InlineData("run.ps1", "powershell", "PowerShell", "PowerShell", CodeOutlineKind.None)]
    [InlineData("app.py", "python", "Python", "Python", CodeOutlineKind.Indentation)]
    [InlineData("util.cpp", "cpp", "C++", "C++", CodeOutlineKind.Brace)]
    [InlineData("data.sql", "sql", "SQL", "SQL", CodeOutlineKind.None)]
    [InlineData("readme.md", "markdown", "Markdown", "MarkDown", CodeOutlineKind.Markdown)]
    [InlineData("style.css", "css", "CSS", "CSS", CodeOutlineKind.None)]
    [InlineData("main.ts", "typescript", "TypeScript", "JavaScript", CodeOutlineKind.Brace)]
    public void FromPath_MapsKnownExtensions(string path, string languageId, string displayName, string highlighting, CodeOutlineKind outline)
    {
        var type = CodeFileTypeRegistry.Instance.FromPath(path);

        Assert.Equal(languageId, type.LanguageId);
        Assert.Equal(displayName, type.DisplayName);
        Assert.Equal(highlighting, type.HighlightingName);
        Assert.Equal(outline, type.OutlineKind);
    }

    [Fact]
    public void FromPath_ExtensionMatchingIsCaseInsensitive()
    {
        Assert.Equal("csharp", CodeFileTypeRegistry.Instance.FromPath("A.CS").LanguageId);
        Assert.Equal("xml", CodeFileTypeRegistry.Instance.FromPath("a.Xaml").LanguageId);
    }

    [Fact]
    public void FromPath_MapsMarkdownWithUnicodeAndSpaces()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath(
            @"D:\project\BDS2560-SW1\docs\BDS2560SW1HREQ 中央处理模块引导软件高级需求.md");

        Assert.Equal("markdown", type.LanguageId);
        Assert.Equal("MarkDown", type.HighlightingName);
        Assert.Equal("text.html.markdown", type.GrammarScopeName);
    }

    [Fact]
    public void FromPath_UnknownExtension_FallsBackToUnknownType()
    {
        // 未识别扩展 → “?”徽标兜底类型:渲染仍是纯文本(任意内容安全显示),但不再冒充 TXT。
        var type = CodeFileTypeRegistry.Instance.FromPath("archive.zzz");

        Assert.Equal("plaintext", type.LanguageId);
        Assert.Equal(string.Empty, type.HighlightingName);
        Assert.Equal(CodeOutlineKind.None, type.OutlineKind);
        Assert.Equal(Codicons.File, type.IconGlyph);
        Assert.Equal("?", type.IconMonogram);
    }

    [Fact]
    public void FromPath_NoExtension_FallsBackToUnknownType()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath("LICENSE");

        Assert.Equal("plaintext", type.LanguageId);
        Assert.Equal("?", type.IconMonogram);
    }
}

/// <summary>Outline extraction: C# types/members, XML element hierarchy, JSON properties and the
/// graceful degradation of unstructured text.</summary>
public sealed class CodeOutlineParserTests
{
    [Fact]
    public void CSharp_ExtractsTypesAndMembersWithLines()
    {
        const string source = """
            using System;

            namespace Demo;

            public sealed class Calculator
            {
                public int Add(int a, int b) => a + b;

                public string Label { get; set; }

                private readonly int _base;
            }
            """;

        var entries = CodeOutlineParser.Instance.Parse(source, CodeOutlineKind.CSharp);

        Assert.Contains(entries, e => e.Kind == "namespace" && e.Name == "Demo" && e.Line == 3);
        Assert.Contains(entries, e => e.Kind == "type" && e.Name == "Calculator" && e.Line == 5);
        Assert.Contains(entries, e => e.Kind == "member" && e.Name == "Add" && e.Line == 7);
        Assert.Contains(entries, e => e.Kind == "member" && e.Name == "Label" && e.Line == 9);
        // Fields are intentionally skipped (noisy; members = methods/properties/events/indexers).
        Assert.DoesNotContain(entries, e => e.Name == "_base");
    }

    [Fact]
    public void CSharp_RandomText_YieldsNoEntries()
    {
        var entries = CodeOutlineParser.Instance.Parse("this is not c# at all\njust words", CodeOutlineKind.CSharp);

        Assert.Empty(entries);
    }

    [Fact]
    public void Xml_ExtractsElementHierarchyWithDepth()
    {
        const string source = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup />
            </Project>
            """;

        var entries = CodeOutlineParser.Instance.Parse(source, CodeOutlineKind.Xml);

        Assert.Contains(entries, e => e.Name == "Project" && e.Depth == 0 && e.Line == 1);
        Assert.Contains(entries, e => e.Name == "PropertyGroup" && e.Depth == 1);
        Assert.Contains(entries, e => e.Name == "TargetFramework" && e.Depth == 2);
        Assert.Contains(entries, e => e.Name == "ItemGroup" && e.Depth == 1);
    }

    [Fact]
    public void Xml_MalformedDocument_YieldsNoEntries()
    {
        var entries = CodeOutlineParser.Instance.Parse("<unclosed><tag>", CodeOutlineKind.Xml);

        Assert.Empty(entries);
    }

    [Fact]
    public void Json_ExtractsPropertyNamesWithLines()
    {
        const string source = """
            {
              "name": "nornia",
              "version": 1,
              "nested": {
                "enabled": true
              },
              "items": [1, 2, 3]
            }
            """;

        var entries = CodeOutlineParser.Instance.Parse(source, CodeOutlineKind.Json);

        Assert.Contains(entries, e => e.Name == "name" && e.Line == 2 && e.Depth == 0);
        Assert.Contains(entries, e => e.Name == "version" && e.Line == 3);
        Assert.Contains(entries, e => e.Name == "nested" && e.Line == 4);
        Assert.Contains(entries, e => e.Name == "enabled" && e.Line == 5 && e.Depth == 1);
        Assert.Contains(entries, e => e.Name == "items" && e.Line == 7);
    }

    [Fact]
    public void Json_MalformedDocument_YieldsNoEntries()
    {
        var entries = CodeOutlineParser.Instance.Parse("{ this is not json: }", CodeOutlineKind.Json);

        Assert.Empty(entries);
    }

    [Fact]
    public void PlainTextOrNone_YieldsNoEntries()
    {
        Assert.Empty(CodeOutlineParser.Instance.Parse("just text line one\nline two", CodeOutlineKind.None));
        Assert.Empty(CodeOutlineParser.Instance.Parse(string.Empty, CodeOutlineKind.CSharp));
    }

    [Fact]
    public async Task FilePreviewTab_PopulatesDecodeMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nornia-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Demo.cs");
            await File.WriteAllTextAsync(path, "class Demo\n{\n}\n");
            var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

            await tab.LoadAsync();

            Assert.Equal("Demo.cs", tab.Name);
            Assert.Equal("class Demo\n{\n}\n", tab.Content);
            Assert.Equal("C#", tab.LanguageName);
            Assert.Equal("csharp", tab.FileType.LanguageId);
            Assert.Equal("UTF-8", tab.EncodingName);
            Assert.Equal("LF", tab.NewlineTypeText);
            Assert.Equal(3, tab.LineCount);
            Assert.False(tab.IsBinary);
            Assert.False(tab.IsTruncated);
            Assert.Equal(string.Empty, tab.Notice);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }
}
