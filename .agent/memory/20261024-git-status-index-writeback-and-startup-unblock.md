# 2026-10-24 启动卡顿的真维度：跟踪文件数 M（git status 索引刷新）与四项修复

## 现象
打开一个有大量 **git 跟踪文件** 的目录（工作区恢复/右键打开/`--dir` 参数）时整个界面极度卡顿，
与"更改栏里有多少条变更"无关。上一轮 P0–P5（见 `20261015-scm-changes-panel-large-repo.md`）修的是
变更条目数 N，**M 这个维度没被覆盖**。

## 根因（代码 + 本机实测）
1. **`git status` 是 Θ(M)**：它默认刷新索引 = 对每个跟踪文件做一次 stat，stat 与索引不一致时
   （哪怕内容没变，例如构建/解压/拷目录改过 mtime）还要读内容算哈希。
   实测（合成仓库，6 万跟踪文件，git 2.56）：
   | 状态 | 每次 `git status` |
   | --- | --- |
   | 索引新鲜 | 105 ms |
   | 把全部跟踪文件 mtime 改掉（内容几乎未变） | **3.8 s，每次都是** |
   | 同上，`-uno` vs `-uall` | 4.27 s vs 4.29 s（慢的是跟踪文件，不是未跟踪遍历） |
   上限都管不到它：`git.statusLimit` 只在解析阶段 `break`（`GitParsers.cs`），16 MB stdout 上限只截内存。
2. **`GIT_OPTIONAL_LOCKS=0`（原先对所有 readOnly 读取注入）让这份成本永远无法摊销**：
   | 模式 | run1 | run2 | run3 | `.git/index` mtime |
   | --- | --- | --- | --- | --- |
   | `GIT_OPTIONAL_LOCKS=0` | 3759 ms | 3786 ms | 3819 ms | 始终不变 |
   | 默认（允许写回） | 4376 ms | **110 ms** | 109 ms | 被更新 |
   反向验证：让 git 写过一次后，即使再带该标志也回到 111 ms → 标志本身不慢，是"永远不会变快"。
3. **这次读取还排在"打开目录"的关键路径上**：`ProjectWorkspaceService.PublishAsync` 顺序 await 每个
   `ContextChanged` 订阅者，而 GitViewModel 在构造器订阅（构造早于 ExplorerPageViewModel），
   于是资源管理器的目录枚举要等 SCM 全量加载（status + 2×for-each-ref + log + stash + 2×range log，
   每仓库 2 个读槽）跑完 —— 用户看到的"读目录慢"其实是"目录读取在排队等 git"。
4. 放大器：静默刷新节流 500ms（最高 ~2 次/秒全量 status）；watcher 溢出/错误会把"仅 status"升级成
   全量 6 进程；写操作（丢弃全部/推送）内部各自再跑一次完整 status，紧接着视图层又刷一次。

## 修复（用户批准执行 1、2、4、5）
- **1 索引写回**：`GitStatusReadOptions.AllowIndexWriteBack`；前台全量加载带该标志（不再注入
  `GIT_OPTIONAL_LOCKS=0`）并顺带 `-c core.untrackedCache=true`；静默刷新保持禁止（它每 500ms 一次，
  自己写 `.git/index` 会被 watcher 反触发成刷新循环）。写回只在共享读槽内、同一仓库同时只有一次全量
  加载，git 自己的 `.git/index.lock` 负责串行。
- **2 发布链并发**：`PublishAsync` 用 `Task.WhenAll` 并发等待所有订阅者；每个订阅者都跑到，失败仍按
  单个/聚合异常上报并回滚（语义不变，`ActivateAsync` 仍等全部完成，因此现有测试的时序假设不变）。
- **4 首屏与缓存**：工作区首次全量加载先发 `-unormal`；快照里出现折叠的未跟踪目录
  （`GitRepositoryStatus.HasCollapsedUntrackedDirectory`）时不发布该条目，紧随一次静默 `-uall` 补齐；
  无折叠目录时两种模式逐条等价，只读一次。新增设置 `git.fsmonitor`（默认关闭）。
- **5 去重复**：`DiscardAsync`/`DiscardUnstagedAsync` 的"全部"分支改走 `ls-files --others --exclude-standard`
  + 一条 `restore -- .`；`PushAsync` 改走 `rev-parse --abbrev-ref HEAD`（失败路径才补
  `--is-inside-work-tree` 以区分"分离 HEAD"与"不是仓库"）；watcher 溢出/错误不再升级为全量加载
  （HEAD 真移动时仍由 HEAD 签名比较升级）。

## 被证伪 / 未采纳（不要重走）
- **`core.fsmonitor` 默认开启**：实测 `git -c core.fsmonitor=true status` 会为该仓库常驻
  `git fsmonitor--daemon` 进程并留下 `.git/fsmonitor--daemon/`（命令返回后仍在），而本次测量里
  没有可测收益（索引热时 58 ms vs 58 ms）。它是机器级副作用，故只作为用户显式选项。
- **静默刷新也允许写回**：会让每次静默刷新自己产生 index 事件 → 反触发下一轮（churn 期间可自持），
  且抑制窗口只有 250ms、覆盖不了秒级读取的退出时刻。
- **给静静默刷新加"读前预热"式第二次 `-unormal` 读取**：索引写回已经把第二次读取变成热路径，
  再拆一次只多付一次进程启动（~200ms）而不减墙钟。（首屏只在**首次全量加载**这样做，理由是
  未跟踪目录很重时先给摘要。）

## 证据（测试名）
`ProjectWorkspaceServiceTests`（并发/失败语义）、`GitServiceTests.GetStatusAsync_AllowIndexWriteBack_*`、
`GetStatusAsync_WithoutIndexWriteBack_StillDisablesOptionalLocks`、`GetStatusAsync_FileSystemMonitorOption_PassesCoreFsmonitor`、
`GetStatusAsync_UntrackedModeSelectsNormalOrAll`、`DiscardAll_ReadsUntrackedListWithoutAFullStatus`、
`DiscardAllUnstaged_ReadsUntrackedListWithoutAFullStatus`、`PushAsync_ResolvesBranchWithoutAStatusRead`、
`PushAsync_DetachedHead_IsRefusedWithoutAStatusRead`、`GitViewModelTests.FullLoad_AllowsIndexWriteBack_SilentRefreshDoesNot`、
`FirstLoad_CollapsedUntrackedDirectory_IsRefinedByAnAllUntrackedRead`、`WatcherOverflow_DoesNotEscalateToFullLoad`、
`FileSystemMonitorSetting_PassesCoreFsmonitorToStatusReads`、`WatcherDrivenSilentRefresh_ListsEveryUntrackedFileInNewFolders`。

## 复现方法（脚本已移出仓库，放在 %TEMP%）
`%TEMP%\nornia-tracked-perf-probe\git-status-scaling.py` 与 `git-status-optional-locks.py`
（生成 `%TEMP%\nornia-tracked-perf` 合成仓库，可随时删除）。仓库自带
`tools/heavy-git-repo.ps1 -Untracked 10000 -Modified 200 -Tracked 500` 的 `-Tracked` 默认只有 500，
正好把 M 维度设成常量——要复现本问题必须把 `-Tracked` 提到万级。

## 验证状态
`dotnet build Nornia.sln`：0 错误 0 警告。`dotnet test Nornia.sln --filter Category!=Integration`：
Nornia.Tests 1691 通过、Nornia.Settings.Tests 11 通过、0 失败。未做真机 GUI 走查（WPF 窗口无法在
本会话中交互）。

## 提升状态
已提升到 `AGENTS.md`（关键设计决策新增一条）与 `docs/design.md`（「超大工作树的读取、发布与降级」
由四条红线扩为六条，并把成本维度区分为 N 与 M）。本文件保留为经验层（实测数据、被证伪方案、
复现脚本位置）。
