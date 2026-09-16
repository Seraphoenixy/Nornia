# 2026-09-16 SCM：新建文件夹中的新文件在更改列表不可跟踪

## 现象
新建文件夹并在其中新建文件后，更改（Changes）列表出现空白文件行，文件无法单独暂存/丢弃/查看 diff。

## 根因（已验证）
- `GitRepositoryWatcher` 递归监视正常，OS 层不丢事件（实测）。
- 真正缺陷在 `GitViewModel.LoadStateLiteAsync`（watcher 驱动的静默刷新）：`GetStatusAsync(includeAllUntracked: false)` → `git status --untracked-files=normal`，git 把整个未跟踪新目录折叠成单条目 `? newdir/`（含尾斜杠）。
- `GitChangeItem.FileName` 对尾斜杠路径取空字符串 → `BuildTreeRows` 渲染成空名文件行，且无法展开看到内部文件。
- 显式刷新 `LoadStateCoreAsync` 用默认 `all`，无此问题——只有静默刷新路径受影响。

## 修复（已通过测试验证）
`LoadStateLiteAsync` 改为默认 `GetStatusAsync(path, cancellationToken)`（`--untracked-files=all`），逐文件列出未跟踪内容，与显式刷新/VS Code 一致。watcher 已忽略 bin/obj/node_modules，条目上限（StatusMaximumEntries）兜底病理仓库。

## 验证状态
已验证（GitViewModelTests 新增 `WatcherDrivenSilentRefresh_ListsEveryUntrackedFileInNewFolders` 回归测试 + 相关 115 个测试通过）。等用户确认后可考虑提升到 AGENTS.md。
