namespace Nornia.Desktop.Code;

/// <summary>等宽字体家族常量:渲染路径(视图)统一引用 App.xaml 的 MonoFontFamily 令牌,此常量仅作为
/// 无 Application 宿主(单元测试 / 纯绘制)时的兜底来源,避免在 Views 层散布字体字面量。</summary>
public static class ViewFonts
{
    public const string MonoFamily = "Cascadia Mono, Consolas";
}