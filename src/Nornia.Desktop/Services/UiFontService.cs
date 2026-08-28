using System;
using System.Collections.Generic;
using System.Windows;

namespace Nornia.Desktop.Services;

/// <summary>
/// Applies the persisted global UI font scale (settings.json <c>UiFontScale</c>) to the font
/// size / measurement tokens defined in App.xaml. Every view reads those tokens through
/// <c>DynamicResource</c>, so rewriting the token values in <c>Application.Current.Resources</c>
/// re-sizes the whole shell immediately (no restart). The code editor font size follows the same
/// scale as a multiplier on top of the author value (<c>editor.fontSize</c> × scale): the code and
/// diff readers scale with the interface so they track the display resolution. The persisted
/// <c>editor.fontSize</c> stays in author space (see <see cref="CurrentScale"/> and the read-back
/// division in EditorAreaViewModel).
/// </summary>
public static class UiFontService
{
    public const double MinScale = 0.85;
    public const double MaxScale = 1.3;
    public const double DefaultScale = 1.0;

    /// <summary>当前生效的界面缩放倍率(由 <see cref="Apply"/> 写入)。作者字号
    /// (<c>editor.fontSize</c>)乘以该倍率得到源码 / Diff 阅读器的显示字号;写回设置时
    /// 除以该倍率,保证持久化值始终是作者字号。</summary>
    public static double CurrentScale { get; private set; } = DefaultScale;

    /// <summary>Author values of every scalable token in App.xaml (Type*/Icon*/Size*). Must stay
    /// in sync with the XAML definitions — the design-system tests assert the key set.</summary>
    public static IReadOnlyDictionary<string, double> BaseValues { get; } = new Dictionary<string, double>
    {
        // type scale (text): Body 13 承载全部小字阶;中间档 BodyLg 15 / Subtitle 16 / Heading 18
        // 承接行级大正文、副标题与区块标题;大字阶 Title 20 / Display 22 / Hero 26
        ["TypeBody"] = 13,
        ["TypeBodyLg"] = 15,
        ["TypeSubtitle"] = 16,
        ["TypeHeading"] = 18,
        ["TypeTitle"] = 20,
        ["TypeDisplay"] = 22,
        ["TypeHero"] = 26,
        // CountBadge 徽标标签字号(VS Code 11px 徽标),独立于 IconBadge(文件 Monogram 字形档)。
        ["TypeBadge"] = 11,
        // icon glyph scale: 行级图标全部并入 IconInline(12),其余按角色分离
        ["IconChevron"] = 9,
        ["IconInline"] = 12,
        ["IconNav"] = 15,
        ["IconClose"] = 12,
        ["IconActivity"] = 24,
        ["IconActivityCompact"] = 16,
        ["IconEmpty"] = 28,
        // 文件类型图标的语言缩写文字档(透明底 + 语言色相的 Monogram)
        ["IconBadge"] = 9,
        ["IconMonogram"] = 12,
        // Monogram 单元格尺寸:Viewbox 以该尺寸为界等比自适应,随界面缩放(避免长缩写溢出)。
        ["IconMonogramSlot"] = 18,
        ["IconMonogramCell"] = 18,
        // measurement tokens (row/control heights that follow the scale — VS Code default density: title/tab 35, status/compact row 22)
        ["SizeRowSm"] = 22,
        ["SizeRowCompact"] = 22,
        ["SizeTableRow"] = 22,
        ["SizeRowMd"] = 26,
        ["SizeControlMd"] = 24,
        ["SizeStatusBar"] = 28, // 合并文档状态段后加高(原 22,VS Code 默认密度)
        ["SizeTitleBar"] = 35,
        ["SizeActivity"] = 48,
        ["SizeTabBar"] = 35,
        ["SizeCommitBox"] = 40,
    };

    public static double ClampScale(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>Applies the scale to the live application resources (no-op before the
    /// application exists, e.g. in unit tests). <see cref="CurrentScale"/> is only updated when a
    /// real Application host is present — unit hosts must never observe a stale scale.</summary>
    public static void Apply(double scale)
    {
        if (Application.Current is null) return;
        ApplyTo(Application.Current.Resources, scale);
        CurrentScale = ClampScale(scale);
    }

    /// <summary>Writes every base token back as <c>round(value * scale, 1)</c>. Replacing the
    /// resource values triggers a full DynamicResource refresh. At scale 1.0 the written values
    /// equal the App.xaml author values (idempotent).</summary>
    public static void ApplyTo(ResourceDictionary resources, double scale)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var effective = ClampScale(scale);
        foreach (var (key, baseValue) in BaseValues)
        {
            resources[key] = Math.Round(baseValue * effective, 1);
        }
    }
}
