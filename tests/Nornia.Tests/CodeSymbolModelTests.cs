using Nornia.Desktop.Code;
using Nornia.Desktop;

namespace Nornia.Tests;

public sealed class CodeSymbolModelTests
{
    private static CodeFileType CSharp() => new("csharp", "C#", "C#", Codicons.File, CodeOutlineKind.CSharp);
    private static CodeFileType Xml() => new("xml", "XML", "XML", Codicons.File, CodeOutlineKind.Xml);
    private static CodeFileType Json() => new("json", "JSON", string.Empty, Codicons.File, CodeOutlineKind.Json);

    [Fact]
    public void CSharp_UsesVisibleSymbolAncestorsAndSeparateBraceFoldRange()
    {
        var document = CodeSymbolAnalyzer.Instance.Analyze("""
            namespace Demo
            {
                class Calculator
                {
                    void Add()
                    {
                    }
                }
            }
            """, CSharp().OutlineKind);

        var type = Assert.Single(document.All, node => node.ShowInOutline && node.Kind == "type");
        var method = Assert.Single(document.All, node => node.ShowInOutline && node.Name == "Add");
        Assert.Equal("Calculator", type.Name);
        Assert.Equal(3, type.Range.StartLine);
        Assert.Equal(4, type.FoldingRange.StartLine);
        Assert.Equal(method, document.FindDeepestContaining(6));
        Assert.Equal(["Demo", "Calculator", "Add"], document.GetAncestors(method).Select(node => node.Name).ToArray());
        Assert.Contains(type.VisibleChildren, node => ReferenceEquals(node, method));
    }

    [Fact]
    public void Xml_UsesTheActualClosingTagAndPreservesItForFolding()
    {
        var document = CodeSymbolAnalyzer.Instance.Analyze("""
            <Project>
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """, Xml().OutlineKind);

        var group = Assert.Single(document.All, node => node.Name == "PropertyGroup");
        Assert.Equal(2, group.Range.StartLine);
        Assert.Equal(5, group.Range.EndLine);
        Assert.Contains(document.ToFoldSections(), section =>
            section.StartLine == 2 && section.EndLine == 5 && section.PreserveEndLine);
        Assert.Equal("UseWPF", document.FindDeepestContaining(4, 20)?.Name);
    }

    [Fact]
    public void Json_BuildsNestedPropertyRangesAndKeepsCompletedNodesAfterMalformedTail()
    {
        var document = CodeSymbolAnalyzer.Instance.Analyze("""
            {
              "outer": {
                "inner": 1
              }
            }
            """, Json().OutlineKind);

        var inner = Assert.Single(document.All, node => node.ShowInOutline && node.Name == "inner");
        Assert.Equal(["outer", "inner"], document.GetAncestors(inner).Select(node => node.Name).ToArray());
        Assert.Equal(inner, document.FindDeepestContaining(3, 5));

        var partial = CodeSymbolAnalyzer.Instance.Analyze("<Project>\n  <Name>ok</Name>\n  <Broken>", Xml().OutlineKind);
        Assert.Contains(partial.All, node => node.Name == "Name");
    }
}
