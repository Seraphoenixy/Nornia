using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Nornia.Desktop.Services;

/// <summary>
/// 等宽字体目录:设置页「编辑器字体 / 终端字体」下拉框的候选来源。下拉框展示
/// <see cref="SupportedMonoFamilies"/> 的全部候选——这正是「支持哪些字体」的权威列表,
/// 跨机器保持一致(工作区/语言级设置可以随仓库共享);本机未安装的候选在列表内以
/// 「（未安装）」标记,选中后仍可写入设置,渲染时由 WPF 逐字形回退,不会缺字。
/// </summary>
public static class FontCatalog
{
    /// <summary>源码/Diff 阅读器默认字体(<c>editor.fontFamily</c>)。与 App.xaml
    /// <c>MonoFontFamily</c> 令牌的主选一致(Win11 内建、含连字)。</summary>
    public const string DefaultEditorFamily = "Cascadia Code";

    /// <summary>终端默认字体(<c>terminal.integrated.fontFamily</c>)。</summary>
    public const string DefaultTerminalFamily = "Cascadia Code";

    /// <summary>通用后备链:所选字体缺少字形(CJK 等)时由 WPF 按顺序逐字形回退。</summary>
    public const string FallbackChain = "Consolas, Microsoft YaHei";

    /// <summary>支持的等宽字体(按推荐顺序),即设置下拉框的完整选项。</summary>
    public static IReadOnlyList<string> SupportedMonoFamilies { get; } = new[]
    {
        "Cascadia Code",
        "Cascadia Mono",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "Fira Mono",
        "Source Code Pro",
        "IBM Plex Mono",
        "Roboto Mono",
        "Ubuntu Mono",
        "Inconsolata",
        "Hack",
        "DejaVu Sans Mono",
        "Menlo",
        "Courier New",
    };

    /// <summary>本机已安装的字体族名(大小写不敏感,惰性枚举一次;无字体服务的宿主退化空集)。</summary>
    private static readonly Lazy<HashSet<string>> InstalledFamilies = new(() =>
    {
        try
        {
            return new HashSet<string>(Fonts.SystemFontFamilies
                .Select(family => family.Source?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 纯单元测试宿主没有可用字体服务时的降级:全部视为未安装,候选列表不受影响。
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }, isThreadSafe: true);

    public static bool IsInstalled(string familyName) =>
        !string.IsNullOrWhiteSpace(familyName) && InstalledFamilies.Value.Contains(familyName.Trim());

    /// <summary>本机已安装的支持字体(下拉框以此标记「未安装」)。</summary>
    public static IReadOnlyList<string> InstalledSupportedFamilies =>
        SupportedMonoFamilies.Where(IsInstalled).ToArray();

    /// <summary>把所选字体补上通用后备,构成渲染令牌(所选字体为空时回退默认字体)。</summary>
    public static string ComposeRenderingFamily(string? primary) =>
        string.IsNullOrWhiteSpace(primary) ? $"{DefaultEditorFamily}, {FallbackChain}" : $"{primary.Trim()}, {FallbackChain}";
}