using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nornia.Core.Models;

namespace Nornia.Desktop.ViewModels;

/// <summary>依赖树的一个节点(懒加载)。IsLeaf 决定是否显示展开箭头;
/// 展开时由 <see cref="ToolsViewModel.LoadDependencyNodeAsync"/> 逐节点调用
/// IToolExtensionProvider.GetDependenciesAsync 填充 Children。</summary>
public sealed partial class ToolExtensionDependencyNode(ToolExtensionDependency dependency) : ObservableObject
{
    public string Name => dependency.Name;

    public string? Version => dependency.Version;

    /// <summary>pip 版本说明符原文(如 ">=1.0,<2");npm 通常为 null。</summary>
    public string? Constraint => dependency.Constraint;

    public bool IsLeaf => dependency.IsLeaf;

    public bool HasChildren => !IsLeaf;

    public ObservableCollection<ToolExtensionDependencyNode> Children { get; } = [];

    /// <summary>本节点的依赖是否已加载(已加载则展开不再派生进程)。</summary>
    [ObservableProperty] private bool isLoaded;

    /// <summary>本节点依赖加载中(防重入;XAML 可显示"加载中…")。</summary>
    [ObservableProperty] private bool isLoading;

    public void PublishChildren(IReadOnlyList<ToolExtensionDependency> dependencies)
    {
        Children.Clear();
        foreach (var dependency in dependencies)
        {
            Children.Add(new ToolExtensionDependencyNode(dependency));
        }

        IsLoaded = true;
    }
}
