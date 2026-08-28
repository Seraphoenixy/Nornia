using Nornia.Desktop;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Pure fold-section derivation: C-style braces with string/comment awareness, JSON and
/// XML element folds; unstructured text degrades to no sections.</summary>
public sealed class CodeFoldingStrategyTests
{
    private static CodeFileType BraceType() => new("csharp", "C#", "C#", Codicons.File, CodeOutlineKind.CSharp);
    private static CodeFileType JsonType() => new("json", "JSON", string.Empty, Codicons.File, CodeOutlineKind.Json);
    private static CodeFileType XmlType() => new("xml", "XML", "XML", Codicons.File, CodeOutlineKind.Xml);

    [Fact]
    public void CSharp_BracesProduceMultilineSections()
    {
        const string source = """
            class A
            {
                void M()
                {
                }
            }
            """;

        var sections = CodeFoldingStrategy.Instance.FindSections(source, BraceType());

        Assert.Equal(2, sections.Count);
        Assert.Contains(sections, s => s.StartLine == 2 && s.EndLine == 6); // class A
        Assert.Contains(sections, s => s.StartLine == 4 && s.EndLine == 5); // void M
        Assert.All(sections, section => Assert.True(section.PreserveEndLine));
        Assert.Equal(sections.OrderBy(s => s.StartLine).ThenByDescending(s => s.EndLine), sections);
    }

    [Fact]
    public void Braces_InStringsAndComments_AreIgnored()
    {
        const string source = """
            var a = "}not a brace{";
            // { comment
            /* { block } */
            var b = 1;
            {
                var c = 2;
            }
            """;

        var sections = CodeFoldingStrategy.Instance.FindSections(source, BraceType());

        Assert.Single(sections);
        Assert.Equal(5, sections[0].StartLine);
        Assert.Equal(7, sections[0].EndLine);
    }

    [Fact]
    public void Json_ObjectsAndArraysFold()
    {
        const string source = """
            {
              "outer": {
                "inner": 1
              },
              "items": [
                1, 2, 3
              ]
            }
            """;

        var sections = CodeFoldingStrategy.Instance.FindSections(source, JsonType());

        Assert.Equal(3, sections.Count);
    }

    [Fact]
    public void Xml_ElementsWithChildrenFold()
    {
        const string source = """
            <Project>
              <PropertyGroup>
                <Name>X</Name>
              </PropertyGroup>
              <ItemGroup></ItemGroup>
            </Project>
            """;

        var sections = CodeFoldingStrategy.Instance.FindSections(source, XmlType());

        Assert.Contains(sections, s => s.StartLine == 1); // Project
        Assert.Contains(sections, s => s.StartLine == 2 && s.EndLine == 4 && s.PreserveEndLine); // PropertyGroup
        Assert.DoesNotContain(sections, s => s.StartLine == 4); // leaf Name
    }

    [Fact]
    public void UnstructuredText_AndUnknownKinds_HaveNoSections()
    {
        var plain = CodeFileType.PlainText();
        Assert.Empty(CodeFoldingStrategy.Instance.FindSections("just words\nmore words", plain));
        Assert.Empty(CodeFoldingStrategy.Instance.FindSections(string.Empty, BraceType()));
    }
}

/// <summary>Preview folding + view-state contract: sections computed from the loaded file, the
/// reading position stored on the tab survives re-activation and is cleared on release.</summary>
public sealed class FilePreviewFoldAndStateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-fold-{Guid.NewGuid():N}");

    public FilePreviewFoldAndStateTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private async Task<FilePreviewTab> OpenAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task StructuredFile_ComputesFoldSections()
    {
        var tab = await OpenAsync("A.cs", "class A\n{\n    void M()\n    {\n    }\n}\n");

        Assert.NotEmpty(tab.FoldSections);
        Assert.All(tab.FoldSections, s => Assert.True(s.EndLine > s.StartLine));
    }

    [Fact]
    public async Task PlainText_HasNoFoldSections()
    {
        var tab = await OpenAsync("notes.txt", "just text\nno structure");

        Assert.Empty(tab.FoldSections);
    }

    [Fact]
    public async Task ViewState_SurvivesOnTab_AndClearsOnRelease()
    {
        var tab = await OpenAsync("state.cs", "class A { }\n");

        var state = new EditorViewState(120, 3, 7, new HashSet<int> { 42 });
        tab.ViewState = state;

        Assert.Equal(120, tab.ViewState!.VerticalOffset);
        Assert.Equal(3, tab.ViewState.CaretLine);
        Assert.Equal(7, tab.ViewState.CaretColumn);
        Assert.Contains(42, tab.ViewState.FoldedOffsets!);

        tab.ReleaseResources();

        Assert.Null(tab.ViewState);
        Assert.Empty(tab.FoldSections);
    }
}
