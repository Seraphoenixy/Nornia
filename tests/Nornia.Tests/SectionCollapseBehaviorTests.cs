using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nornia.Desktop;
using Nornia.Desktop.Converters;
using Nornia.Desktop.Views.Controls;

namespace Nornia.Tests;

/// <summary>CollapsibleSection 折叠可见性的 STA 诊断:验证控件在真实 WPF 环境下的
/// 内容呈现与 IsExpanded → 内容可见性链路(侧栏"折叠不可见"问题的护栏)。
/// 控件 BAML 引用 App 级 StaticResource,因此用共享 STA 线程 + 最小 Application 资源。</summary>
public sealed partial class SectionCollapseBehaviorTests
{

    /// <summary>泵空 Dispatcher 队列到 Background 优先级,让绑定更新(DataBind)落地。</summary>
    private static void PumpQueue()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>离线布局,让 UserControl 的内容进入视觉树(无需真实窗口)。</summary>
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(240, 2000));
        element.Arrange(new Rect(0, 0, 240, 2000));
    }

    private static void FindContentHost(DependencyObject node, ref ContentPresenter? host)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ContentPresenter { Content: ListBox } presenter)
            {
                host = presenter;
            }

            FindContentHost(child, ref host);
        }
    }

    private sealed partial class FakeSectionHost : ObservableObject
    {
        [ObservableProperty]
        private bool expanded = true;
    }

    [Fact]
    public void SettingContentKeepsChromeAndTogglesVisibility()
    {
        WpfStaContext.Run(() =>
        {
            var section = new CollapsibleSection { Title = "测试", IsExpanded = true };
            var list = new ListBox { MinHeight = 50 };
            section.SectionContent = list;
            Layout(section);
            PumpQueue();

            // 外部内容不能顶掉分区头按钮
            var headerButton = default(Button);
            void FindButton(DependencyObject node)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is Button button)
                    {
                        headerButton = button;
                    }

                    FindButton(child);
                }
            }

            FindButton(section);
            Assert.NotNull(headerButton);

            var contentHost = default(ContentPresenter);
            FindContentHost(section, ref contentHost);
            if (contentHost is null)
            {
                var dump = new System.Text.StringBuilder();
                void Dump(DependencyObject node, int depth)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    {
                        var child = VisualTreeHelper.GetChild(node, i);
                        var detail = child switch
                        {
                            ContentPresenter p => $" Content=[{p.Content?.GetType().Name ?? "null"}] Vis={p.Visibility}",
                            _ => string.Empty,
                        };
                        dump.AppendLine($"{new string(' ', depth * 2)}{child.GetType().Name}{detail}");
                        Dump(child, depth + 1);
                    }
                }

                Dump(section, 0);
                Assert.Fail($"未找到承载列表的 ContentPresenter。视觉树:\n{dump}");
            }

            Assert.NotNull(contentHost);
            Assert.Equal(Visibility.Visible, contentHost.Visibility);

            // 折叠 → 内容呈现器应 Collapsed
            section.IsExpanded = false;
            PumpQueue();
            Assert.Equal(Visibility.Collapsed, contentHost.Visibility);

            section.IsExpanded = true;
            PumpQueue();
            Assert.Equal(Visibility.Visible, contentHost.Visibility);
        });
    }

    [Fact]
    public void TwoWayBoundIsExpandedFollowsSourceChanges()
    {
        WpfStaContext.Run(() =>
        {
            var host = new FakeSectionHost();
            var section = new CollapsibleSection { Title = "测试" };
            section.SetBinding(CollapsibleSection.IsExpandedProperty, new Binding(nameof(FakeSectionHost.Expanded))
            {
                Source = host,
                Mode = BindingMode.TwoWay,
            });

            var list = new ListBox { MinHeight = 50 };
            section.SectionContent = list;
            Layout(section);
            PumpQueue();

            var contentHost = default(ContentPresenter);
            FindContentHost(section, ref contentHost);
            Assert.NotNull(contentHost);
            Assert.Equal(Visibility.Visible, contentHost.Visibility);

            host.Expanded = false;
            PumpQueue();
            Assert.Equal(Visibility.Collapsed, contentHost.Visibility);

            host.Expanded = true;
            PumpQueue();
            Assert.Equal(Visibility.Visible, contentHost.Visibility);
        });
    }
}
