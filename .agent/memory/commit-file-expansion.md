# 最近提交文件清单

2026-10-11：提交展开原先逐项 Add 到 ObservableCollection，内层 ItemsControl 非虚拟化；即使外层提交列表虚拟化，单个提交仍生成全部文件行。

改为 BulkObservableCollection 单次发布、有限高度（320px）的 Recycling ListBox。多文件提交在清单内滚动，保留文件点击打开提交 Diff 的原模板。回归覆盖 5000 文件单次通知、重复展开使用缓存，以及实际生成容器数量与末尾文件可达性。
