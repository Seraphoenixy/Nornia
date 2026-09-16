# 2026-09-16 源代码管理更改树展开/折叠卡顿修复

## 现象
更改树（GitView 树状布局）展开/折叠目录时卡顿，仓库越大越明显。

## 根因（已验证）
1. **二次方扫描（主因）**：`GitViewModel.FlattenTree` 对每个文件行执行 `folder.Files.First(file => file.FileName == entry.Name)`，在排序后的条目列表里逐文件线性扫描整个文件夹的 Files 列表 → 单文件夹 O(F²)。大仓库某目录数千文件时，一次切换要做数百万次字符串比较，阻塞 UI 线程。且每次状态刷新也全量重建。
2. **整列表 Reset（次因）**：`RebuildTreeRows` 用 `ReplaceRange` 发布整树，发单个 `Reset`，ListBox 全部可见行容器重建、滚动锚点丢失。代码库已有 `SyncCollection`（前后缀差分，只对受影响区间发 Remove/Insert，见 `CollectionDiffer`），设计意图正是「展开/折叠最小 churn」，但树行路径没用它。

## 修复（已通过测试验证）
1. `FlattenTree` 改为：`folderNames` 排序 + `Files.Sort` 一次，双指针归并（同名目录在前，与旧 `OrderBy().ThenBy(IsFolder)` 顺序完全一致）→ 每层 O(F log F)。
2. `FillTreeRows` 改用 `SyncCollection` 替代 `ReplaceRange`：切换只产生受影响区间的 Remove/Insert，其余行与容器不动、滚动锚点不跳；无变化时（记录值相等）不发任何事件。

## 边界与教训
- 值相等依赖 record：`ScmRowNode`/`ScmFolderNode`/`ScmFileNode`/`GitChangeItem` 均为 record（值语义），`BuildTreeRows` 复用同一批 `GitChangeItem` 实例，故 `SyncCollection` 的 EqualityComparer 判定正确。
- `SyncCollection` 无公共前缀时回退 `ReplaceRange`（整表 Reset）——仅当切换的是树第一个目录才触发，属可接受最坏情况。
- 新回归测试 `BuildTreeRows_SameNameFolderAndFile_OrdersFolderFirst` 固定「同名目录/文件混排目录在前」边界。

## 验证状态
已验证：构建 0 警告 0 错误；GitViewModelTests 91 个全绿（含新回归测试）；全量 1509 测试 0 失败。
