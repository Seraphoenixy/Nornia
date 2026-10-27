# 2026-10-15 源代码管理更改栏：超大工作树卡顿的系统性改造（P0–P5）

## 现象
打开工作区时整个界面一起卡顿；工作树里的变更文件越多越明显。

## 根因（逐条经过代码/测试/基准验证）
1. **R1 子进程启动与解析建模压在调用线程上**：`git status` 的 `Process.Start`、输出等待与逐条建模都会跑在 dispatcher 上（首屏因此直接冻结界面）。
2. **R2 一次发布做两遍全表等值扫描**：`SyncCollection` 先 `Compute` 差分、再在快照路径里 `SequenceEqual` 全表比一遍；10k 条记录每轮刷新多花一整趟逐项比较。
3. **R3 每个树行自带祖先引导数组**：树行构建按行分配祖先索引数组，行数一多就是行数级的分配。
4. **R4 资源管理器装饰解析是 O(路径数 × 深度 × 兄弟数)**：每个相对路径段都在子节点列表上线性找名字，宽目录（一个目录几百个文件）下装饰一轮就是几十万次比较。
5. **R5 大区间逐条通知近似二次方**：WPF 每条 `CollectionChanged` 都要过一遍 `ItemWindow`/`CollectionView`，中间区间几百项时逐条 Remove/Insert 的总成本是平方级。
6. **R6 静默截断不可见**：条目上限之外直接丢掉，界面既不说「被截断」也没有分批渲染，用户只看到「列表少了但不知道为什么」。

## 修复与证据
- R1：`ProcessRunner.RunBoundedAsync`（stdout 到 16 MB 上限即终止子进程，`OutputTruncated` 标记保留前缀为部分输出）；`GitService.RunGitBoundedAsync` 在 `Task.Run` 里启动子进程，全链路 `ConfigureAwait(false)`；读并发槽 `ReadSlots` 由 4 降到 2（并发更多读只会互相抢 IO）。证据：`GitStatusThreadingTests`（断言执行线程 ≠ 调用线程、且没有将续延排回调用方上下文）、`GitServiceTests`、`GitStatusParserTests`。
- R2：一次发布只做一次 `CollectionDiffer.Compute`，快照路径 `ReplaceRange(items, checkEquality: false)` 跳过第二遍全表比较。证据：`CoalescingPrimitivesTests`。
- R3：树行的祖先引导数组按深度共享，不再按行分配。证据：`GitViewModelTests` 的树行回归 + `WorkspaceTreeModelTests`。
- R4：`WorkspaceNode` 加 `_childrenByName` 名字索引 + `FindChild(name)`，装饰解析降为 O(深度)。证据：`WorkspaceDecorationIndexTests` 用 `DecorationSegmentLookups` 计数断言「查找次数不随兄弟节点数增长」（150 兄弟 vs 1 兄弟增量相同，且远小于 150）。
- R5：`CollectionDiffer.SinglePassReplaceThreshold = 128`——小区间保留逐条差分通知（未受影响行的容器、滚动锚点、展开态存活），纯前缀删除或中间区间超阈值时改单次 `Reset` 快照。证据：`CoalescingPrimitivesTests.SyncCollection_TailShrinkToNewEntryLimit_PublishesRemovesOnly`（120→100 尾部收缩必须是 20 条 Remove、0 次 Reset、精确落在 100）。
- R6：设置项 `git.statusLimit`（默认 10 000，范围 100–100 000）→ `GitViewModel.StatusEntryLimit` 钳制后传给读取端；`Truncated` 时页脚 `StatusTruncationSummary` 说明「仅列出前 N 项（可在设置 git.statusLimit 中调整）」并写一条 WARNING 日志；树状布局另有单次发布行数预算 `TreeRowRenderLimit=512`（`TreeRowRenderChunk=512` 步进）配「显示更多（还有 X 项未列出）」。预算与间隔都是可注入属性，不做墙钟断言。证据：`ScmLargeChangesDegradationTests`、`WorkspaceTreeAutoRefreshTests`。

## 被证伪的方案（不要重走）
- **推迟武装 watcher 来给首屏让路**：实测把首次刷新与 watcher 附加解绑后，工作树变更会静默丢失若干秒，用户看到过期的更改列表；性能收益也拿不到（首屏墙钟时间由 git 子进程主导，见下）。已回退，`LoadStateAsync` 结尾照常 `Attach`。

## 基准结论（`GitStatusPipelineBenchmark`，--job dry）
- 10 000 个未跟踪文件时，墙钟时间由 **git 子进程本身**主导（约 105 ms），解析+建模只占其中一小截；因此「把读取搬离 UI 线程 + 有界读取」是正确方向，而在 UI 侧再做花活省不了多少。
- 条目上限主要砍的是**分配**而不是时间：首屏只保留 1 000 条时 3.31 MB → 1.80 MB。
- 运行配方：本仓库测试/基准工程是 `net10.0-windows`，BenchmarkDotNet 生成的脚手架工程在该 TFM 下还原失败（`NU1201`，对所有基准都成立，非本次引入），因此必须 `--inProcess true`；`--job` 只接受 `dry|short|medium|long|verylong`；filter 要带通配符。
  例：`dotnet run -c Release --project tests/Nornia.Benchmarks -- --filter *GitStatusPipeline* --job dry --inProcess true`
- BDN 的 in-process 发射**不会 await 异步 `[GlobalSetup]`**：setup 必须写成同步方法，否则基准拿到空数据（症状是 NullReferenceException 与荒谬数字）。
- 病理仓库复现脚本：`tools/heavy-git-repo.ps1 -Untracked 10000 -Modified 200 -Tracked 500`。脚本只在带 `.nornia-heavy-repo` 标记的目录里动手（`-Force` 重建），最终用 Nornia 自己的参数（`--porcelain=v2 --branch -z --untracked-files=all`）按 NUL 切分核对记录数——不带 `-z --untracked-files=all` 时 git 会把新目录折叠，条目数会少报。含中文注释的 .ps1 在 PowerShell 5.1 下必须存成带 UTF-8 BOM，否则解析失败。

## 意外发现的真 bug（顺手修掉，已加回归）
调试 `git.statusLimit` 实时生效时发现：设置会话的 `Changed` 事件不保证落在 ViewModel 的构造线程上（会话可能由没有同步上下文的线程打开，watch 续延随之落回线程池——设置页曾出过同一形态的事故）。跨线程发布会被 `NotSupportedException`（CollectionView 线程亲和）**从中间打断**：20 条移除通知只发出第 1 条就抛出，列表停在 119 条，整轮刷新被 `RunQuietRefreshAsync` 的 catch 吞掉，只剩截断提示与实际条数互相矛盾。
修复：`GitViewModel.RunOnOwnerThread`（构造期同时捕获 `SynchronizationContext` 与所有权线程 id；已在所有权线程就内联，否则经该上下文投递），`ApplyScopedSettingsSafely` 与静默刷新重试计时器都走它。
测试侧教训：**没有 WPF 宿主的测试线程证明不了这条红线**——`SettingsLiveEffectTests` 的测试线程既没有同步上下文、又因为 `CollectionViewSource.GetDefaultView` 隐式创建了调度器，任何回排都只会排进没人泵的队列。要么按生产形态自建 STA 线程 + `DispatcherSynchronizationContext` 并自己泵帧（`GitStatusLimitOwnerThreadTests` 就是这么做的，并把 `Changed` 用替身重投到线程池才能真的复现崩溃），要么就别把这类断言放在普通测试线程上。
红/绿都已验证：把 `RunOnOwnerThread` 改成恒内联，该测试以「所有权线程在 60s 内没有完成这一步」失败；恢复后通过。

## 验证状态
`dotnet build Nornia.sln` 与 Release 构建 0 错误 0 警告；全量 `dotnet test --filter Category!=Integration` 见本次会话记录（改造期间各阶段均单独跑绿）。基准工程新增 `BenchmarkDotNet.Artifacts/` 到 `.gitignore`。

## 提升状态
四条红线、被证伪的 watcher 方案与基准运行配方已提升到 `AGENTS.md`（关键设计决策/构建与测试）与 `docs/design.md`、`docs/development.md`；README 只写用户可见行为。红/绿双向验证记录见上，因此本文件保留为经验层（BDN 使用细节、脚本坑、测试宿主教训），不再作为待提升候选。
