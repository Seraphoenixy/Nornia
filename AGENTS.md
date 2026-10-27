# AGENTS.md

Nornia 的权威项目记忆（Single Source of Truth）。只保存稳定、明确、经人工确认或充分验证的项目事实与规则。
Agent 在工作过程中自动发现的信息（项目历史、任务上下文、临时结论、经验、推测）应首先写入 `.agent/memory/`（动态候选记忆），只有经过代码/文档/测试/多次观察或用户确认、且具有长期价值后，才提升到这里。

## 项目概述

Windows 原生低内存项目观察与开发环境工作台。Desktop 使用 WPF/MVVM、.NET 10（net10.0-windows）、C#，外壳仿 VS Code；不使用 Electron/WebView。
- 定位为不编辑代码的工作台：快速打开/阅读、全局搜索、Git 审阅与源代码管理操作、本机环境管理；不提供文件编辑、LSP、调试器或扩展生态（Git 的暂存/提交/推送等**仓库写操作允许**，文件内容保持只读）。CLI（Nornia.CLI）与 Desktop 复用同一组合根。

## 构建与测试

```powershell
dotnet restore Nornia.sln
dotnet build Nornia.sln
dotnet test Nornia.sln
```

- 常规测试排除集成：`dotnet test --filter Category!=Integration`；Winget 集成测试需环境变量 `NORNIA_WINGET_INTEGRATION=1`。
- 基准：`dotnet run -c Release --project tests/Nornia.Benchmarks -- --filter <Glob> --job dry --inProcess true`（参数在 `--` 之后；`net10.0-windows` 会让 BDN 的脚手架工程还原失败，必须 `--inProcess true`；异步 `[GlobalSetup]` 不会被 await，setup 用同步方法）。详见 `docs/development.md`。
- 测试项目：`tests/Nornia.Tests`（Fakes/ 手写替身注入，不依赖真实进程或 WPF 窗口）、`tests/Nornia.Settings.Tests`（设置系统专用）。

## 架构与模块约束

- `Nornia.Core`：仅领域模型与抽象；Runtime、Package、Project、Git、Storage 只依赖 Core。核心内确实没有 WPF 依赖（`grep -r "System.Windows" src/Nornia.Core/` 零命中）——把 `ProcessRunner`/`BulkObservableCollection`/`RunOnceScheduler` 这类具体工具类再下沉到单独的 `Nornia.Infrastructure` 属外观性改动，已于 `docs/refactor-plan.md` §6.0 裁决不做（改 namespace 会违反下面的「不改 namespace」约束）；若未来真的落地跨平台 MCP Server，连同 TFM 一起处理。
- `Nornia.Composition`：唯一服务层组合根，`AddNorniaServices()` 供 CLI 与 Desktop 复用；Desktop 另注册设置系统（`Configuration/`）、命令平台（`Commands/`）、编辑器纯逻辑（`Code/`，无 WPF 依赖、可单测）。
- 业务逻辑不得写入 WPF code-behind。视图 code-behind 只做视图初始化与纯几何/输入处理；算法（如 diff 查找）必须落在 `Code/` 下不依赖 WPF 的类型里（`Code/DiffTextSearch.cs` 是范例）。
- ViewModel 一律经 `IUiDispatcher`/`IUiTimer` 回到 UI 线程：不得出现 `Application.Current`、`Dispatcher.CurrentDispatcher`、`DispatcherTimer`、`DispatcherPriority`（去注释后的词首匹配，由 `ArchitectureGuardrailTests` 强制）。持有绑定集合的 VM 用 `ThreadAffineUiDispatcher`（`Dispatcher.FromThread`，否则 `InvokeAsync` 会挂），无状态服务可用 `WpfUiDispatcher`。
- 模态交互一律经服务抽象：`IConfirmationService`/`IFolderPickerService`/`ITextPromptService`/`IMessageService`；VM 不得引用 `Views.Dialogs.*` 或 `System.Windows.MessageBox`。用完全限定名书写同样违规。
- 版本比较一律走 `Nornia.Core.Models.VersionOrder`（`Comparer`/`MaxByVersion()`）；禁止 `OrderByDescending(x => x.Version)` 之类的字典序排序（由 `ArchitectureGuardrailTests` 强制）。
- 侧栏视图由 `App.xaml` 的隐式 `DataTemplate` 定位，`MainWindow` 不得再持有「页 VM 类型 → 视图类型」的判断链；新增带侧栏的页面 = 加一个 `DataTemplate` + 覆写 `PageViewModel.Sidebar`（守卫：`ArchitectureGuardrailTests`）。「刷新当前页」由 `PageViewModel.RefreshActiveViewAsync()` 虚方法承担，不得退回窗口类型 switch。
- 文件组织：一个文件 = 一个领域/职责单元；CLI 命令、接口、模型、Converter 按领域合并；大而内聚的类保持单文件。
- 重组只移动文件，不改类型名、不改 namespace；XAML 后置 partial 类必须与 .xaml 同文件（`x:Class` 要求）。
- 编辑器 ViewModel 分文件布局（均为 `Nornia.Desktop.ViewModels`，纯移动产物）：`EditorAreaViewModel.cs`（标签编排/布局持久化）、`EditorTabModels.cs`（`EditorTabItem`/`EditorFoldRequest`/`BreadcrumbSegment`）、`FilePreviewTab.cs`、`DiffTab.cs`（后两者是 `: EditorTabItem` 的独立顶层类型，不是嵌套类）。

## 关键设计决策

- SQLite 位于 `%LOCALAPPDATA%\Nornia\nornia.db`；数据是**机器本地**资产（已安装清单、项目绝对路径、修复记录），不放 Roaming——`NorniaPaths.MigrateFromRoamingIfNeeded()` 在组合根解析路径前做一次性幂等搬迁。
- schema 不用版本化迁移（`IDatabaseMigration`/`MigrationRunner`/`schema_migrations` 已删除）：`NorniaDatabase.CreateSchemaAsync` 建表全部 `IF NOT EXISTS`，破坏性/一次性语句由 `PRAGMA user_version` 门控，仅在版本落后时执行一次后抬升；唯一索引排在一次性升级之后。版本常量是 `NorniaDatabase.SchemaVersion`。
- 日志类数据不入库：操作日志已整体移除（`logs` 表与 `IOperationLogRepository`/`IOperationLogWriter` 不存在）；环境修复明细写数据目录下的 `environment-repair-logs.jsonl`。
- 环境清单“快照优先”：内存 30 秒合并 → 持久化快照 TTL（默认 6 小时）+ 环境指纹门控 → 全量重扫；显式动作（重新扫描/`env check`/`env fix`/安装/卸载/升级）一律 `RefreshForcedAsync` 绕过门控。
- 设置 v2：用户 `%LOCALAPPDATA%\Nornia\settings.jsonc` + 工作区 `<工作区>\.vscode\settings.json`（JSONC 注释保留、原子写、热加载）；`state.json` 存机器状态；旧 `settings.json` 启动时一次性迁移并备份。
- CLI 语言：neutral 英文资源为默认 + `CliMessages.zh-Hans.resx` 卫星资源，`--lang` 覆盖；Desktop 界面固定简体中文。
- Git 集成经 git CLI（`-C <repo>`）：读侧有状态/差异/日志/分支/标签/贮藏/进出提交等；写侧是**完整 SCM 面**（`ApplyHunkAsync`/`Stage`/`Unstage`/`Discard`/`Commit`/`Stash`/分支/标签/`Fetch`/`Pull`/`Push`，由 `GitServiceTests`/`GitViewModelTests` 覆盖）。Git 命令只暴露在 Desktop，CLI 不做 git 套皮；Git 根由工作区运行时向上查找 `.git` 推导。
- 超大工作树不得冻结界面（红线，细节与测试名见 `docs/design.md`「超大工作树的读取、发布与降级」）：git 状态读取走有界读取（stdout 16 MB 上限、超限即终止子进程并标记部分输出）且子进程在 `Task.Run` 里启动、解析建模不占调用线程（`GitStatusThreadingTests`）；一次发布只做一次前后缀差分，中间区间 ≤128 项时逐条 Remove/Insert 保住未受影响行的容器与滚动锚点、否则单次 Reset 快照（`CoalescingPrimitivesTests`）；资源管理器装饰经子节点名字索引解析，成本随路径深度而非兄弟数（`WorkspaceDecorationIndexTests` 计数断言）；条目上限 `git.statusLimit`（默认 10 000）之外的条目被丢弃且**必须可见地降级**（页脚提示 + WARNING 日志 + 树行数预算与「显示更多」，`ScmLargeChangesDegradationTests`）。绑定集合只能由构造 ViewModel 的那条线程发布，而设置会话的 `Changed` 可能落在线程池——必须回排所有权线程（`GitStatusLimitOwnerThreadTests`）。曾试过「推迟武装 watcher 给首屏让路」：会静默丢失工作树变更且拿不到性能收益（墙钟由 git 子进程主导），已回退。
- 跟踪文件数（不是变更数）才是 `git status` 的成本维度：一次 status 要刷新索引，等于对每个跟踪文件做一次 stat，stat 与索引不一致时还要读内容算哈希。因此 ① **打开工作区时 git 不得挡住目录树**：`ProjectWorkspaceService.PublishAsync` 并发等待所有 `ContextChanged` 订阅者，慢的 git 读取不再把资源管理器与首屏排在后面（`ProjectWorkspaceServiceTests`）；② **前台全量加载允许 git 写回索引**（`GitStatusReadOptions.AllowIndexWriteBack`：前台不再注入 `GIT_OPTIONAL_LOCKS=0`，静默刷新保持禁止——否则静默刷新自己写 `.git/index` 会被 watcher 反触发成刷新循环；`GitServiceTests`、`GitViewModelTests`）；③ **首屏先用 `--untracked-files=normal`**：若快照里出现被折叠的未跟踪目录（`GitRepositoryStatus.HasCollapsedUntrackedDirectory`）就不发布该折叠条目，并由紧随的一次静默 `-uall` 刷新补齐（`GitViewModelTests`）；④ `git.fsmonitor` 是用户可选项且默认关闭（开启会让 git 为该仓库常驻 `fsmonitor--daemon`）；⑤ 写操作内部不再为读一个字段而重算整棵工作树（丢弃全部改走 `ls-files --others`、推送改走 `rev-parse --abbrev-ref`），watcher 缓冲溢出/监视器错误只触发状态读取，不再升级为全量加载（`GitServiceTests`、`GitViewModelTests`）。
- 终端走 ConPTY（`TerminalScreen`/`AnsiParser` 纯模型），失败时回退重定向模式。
- 环境修复可追踪（`correlation_id`）：`IEnvironmentRepairExecutor.ExecuteAsync` 生成批次 id 并**作为返回值交给调用方**（这不是可选信息，而是回滚的唯一钥匙），每步明细追加到 `%LOCALAPPDATA%\Nornia\environment-repair-logs.jsonl`；批次失败抛 `EnvironmentRepairException`（携带 id 与失败列表）。
- 回滚入口必须保持闭环：CLI `Nornia env rollback <correlation-id> [--apply]`、`env fix --apply` 成功后打印批次 id 与撤销命令、Desktop 修复后把 id 写入 Output。可自动撤销的步骤自动执行；Winget 降级与黑名单包（VCRedist/.NET SDK Preview/WindowsAppRuntime）标记 Manual 并给出手动命令提示。回滚单步失败记入 `EnvironmentRollbackResult.Failures` 而不静默、不中断批次。

## 硬性约束（Windows 安装器与命令行）

- 安装器（`installer/Nornia.iss`）必须注册文件与文件夹两项右键菜单“Open with Nornia”；注册表写 HKCU 以避免管理员权限（`HKCU\Software\Classes\*` 与 `HKCU\Software\Classes\Directory`）。
- 输出文件名格式：`Nornia-{version}-{RID}-Setup.exe`。
- 命令行参数处理在 `App.xaml.cs`：目录参数按项目打开；文件参数只打开该文件，不打开父目录。
- CLI 参数组合不合法一律抛 `CliUsageException`（退出码 2）；未知选项与多余位置参数不得静默忽略。
- 测试中不得使用墙钟阈值断言性能（「跑得比 X ms 快」）；等待异步信号用截止时间轮询，节流/去抖间隔应做成可注入参数。
- 任何写进本文档或 `docs/design.md` 的「声称」（幂等、只执行一次、失败必上报等）都必须有对应测试；无测试支撑的声称不得写进文档。
- 结构性/架构性约束除了写进本文档，还必须配一条文本守卫测试（见 `tests/Nornia.Tests/ArchitectureGuardrailTests.cs`：去注释后按词首正则断言「不得再出现」）。文本守卫必须同时断言「确实枚举到了足够多的源文件」，避免因目录或文件名变化而静默空转。确需豁免时在守卫的白名单里登记并写明理由。

## 文档

- `README.md`：功能总览与 CLI 示例。
- `docs/design.md`：详细设计。`docs/development.md`：开发约定（架构/当前状态/语言策略/文件组织/测试）。
