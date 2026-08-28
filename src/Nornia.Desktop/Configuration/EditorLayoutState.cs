namespace Nornia.Desktop.Configuration;

/// <summary>可恢复的单个编辑器标签描述。文件预览标签由 <see cref="FilePath"/> 重建;
/// diff 标签由仓库路径 / 文件路径 / 暂存状态 / 提交哈希重建(工作区切换或重启后无需再次交互)。</summary>
public sealed record EditorTabState(
    string TabKey,
    bool IsPreview = false,
    string? FilePath = null,
    string? RepositoryPath = null,
    string? DiffPath = null,
    bool IsStaged = false,
    bool IsUntracked = false,
    string? CommitHash = null);

/// <summary>
/// 编辑器组布局树的序列化节点(WorkspaceApplicationState.EditorLayout, schema v2)。
/// 单个 record 承载两种节点形态:
///   * 叶节点(编辑器组): <see cref="GroupId"/> + <see cref="Tabs"/> + <see cref="ActiveTabKey"/>
///     + <see cref="Mru"/>(组内标签 MRU 序,最常使用在前 — VS Code ISerializedEditorGroupModel.mru),
///   * 分支节点(分割): <see cref="Orientation"/> + <see cref="Children"/> + <see cref="Weights"/>。
/// 顶层节点额外携带活动组 id(<see cref="ActiveGroupId"/>)与组级 MRU(<see cref="GroupMru"/>),
/// 叶子为 null。
/// 采用扁平可空形态而非多态 $type,使旧版/手改状态文件的反序列化更宽容,便于恢复时的校验与修复。
/// </summary>
public sealed record EditorLayoutState(
    string? Orientation = null,
    string? GroupId = null,
    IReadOnlyList<EditorTabState>? Tabs = null,
    string? ActiveTabKey = null,
    IReadOnlyList<string>? Mru = null,
    IReadOnlyList<EditorLayoutState>? Children = null,
    IReadOnlyList<double>? Weights = null,
    string? ActiveGroupId = null,
    IReadOnlyList<string>? GroupMru = null)
{
    public const string VerticalOrientation = "vertical";
    public const string HorizontalOrientation = "horizontal";

    public bool IsSplit => !string.IsNullOrWhiteSpace(Orientation);
}