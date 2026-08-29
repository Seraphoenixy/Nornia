# 开发说明

领域划分、核心流程、CLI 约定和扩展设计见 [design.md](design.md)。

## 架构

`Nornia.Core` 只包含领域模型与抽象。Runtime、Package、Project、Git、Storage 仅依赖 Core。`Nornia.Composition` 是唯一的服务层组合根：CLI 与 Desktop 都调用 `AddNorniaServices()` 复用同一套进程、Git、数据库、Provider、仓库和服务注册，各自只追加本端的展示层/命令注册。Desktop 在组合根之外另注册设置系统（`Configuration/`）、命令平台（`Commands/`）与工作台/设置编辑器等 WPF 服务。业务逻辑不应写入 WPF code-behind。

## 当前状态

- 环境 Provider：.NET SDK（`dotnet --list-sdks`）、.NET Desktop Runtime（`dotnet --list-runtimes`）、Node.js、Python、Git、Java、Visual C++ Redistributable（注册表三架构 + `Installed=0` 损坏判定）、Windows App Runtime（`Get-AppxPackage` + 安装位置存在性）。命令型 Provider 版本解析统一走 `IVersionParser` 策略（`PlainVersionParser` / `RegexVersionParser`），由 `CommandRuntimeProviderBase` 注入；各 Provider 上报损坏态（`RuntimeStatus.Error` + `DetectionStatus.Broken` + 诊断文本），与已安装实例分列返回。
- 软件包 Provider：Winget（默认）+ 可选 Scoop/Chocolatey（`where.exe` 探测可用），`PackageProviderSelector` 按“优先指定 → 可用探测 → 默认 Winget”选择。`ProcessRunner` 在不需要进度上报时一次性读取输出，需要实时输出时按行流式读取；另提供 `StreamLinesAsync`（有界通道逐行流，不保留完整输出，供 Git diff 流式解析）。Winget 输出解析由 `WingetProbe` 能力探测选择双模式解析器（`WingetTableParser` 双语显示宽度表格 / `WingetJsonParser` 契约先行 JSON，见 design.md §5.2）；截断名以并发 ≤4 按 Id 去重回查 `winget show`（20s 超时、失败保留截断名并上报诊断）；无匹配退出码（`0x8A150011`/`0x8A150014`）返回空结果而非异常；`0x8A150016`（多实例匹配）卸载回退为查询实际版本后逐个 `--version` 精确卸载（不用 `--all-versions`）；`0x8A15002B`（更新不适用）在 Runtime 升级循环降级为警告跳过；只读命令 60s 短超时不重试，变更命令保留 10 分钟超时加一次重试；失败抛携带退出码的 `WingetException`；共享库用户消息经 `WingetMessages.resx`/`WingetMessages.zh-Hans.resx` 本地化（`WingetText` 访问器）。
- `Nornia.yaml` 支持读写；版本可使用前缀或比较器范围（例如 `>=22 <23`）。环境检查引擎输出 PASS、FAIL、WARNING，并携带满足、缺失、版本不匹配或无效约束原因。
- `env plan` 和 Projects 页“生成修复计划”没有副作用。只有 CLI 的 `env fix --apply` 或用户确认 Desktop 修复对话框后，才会经软件包 Provider 安装/升级可映射的 Runtime。
- **修复追踪与回滚**：`TrackedEnvironmentRepairExecutor` 为每个修复批次生成 `correlation_id`，逐步写入 `environment_repair_logs`（前后版本/包/Provider/回滚策略/失败原因）与 `operation_log`（`details_json`）；单步失败不中断批次，取消中断并汇总，批次有失败时抛 `EnvironmentRepairException`（UI 渲染失败步骤清单）。`EnvironmentRollbackService` 按批次逆序生成回滚计划：自动步骤（重装 previous 版本/卸载），Winget 降级与黑名单包（VCRedist/.NET SDK Preview/WindowsAppRuntime）标记 Manual 并给出手动命令提示。
- CLI 提供 runtime、package、env、project 命令，按 `ICommand` + `CommandDispatcher` 组织；命令名集中在 `CliConstants`，帮助文本由 `HelpCommand` 根据命令注册自动生成，不存在手写重复。
- WPF 的 Dashboard、Runtime（含损坏态/架构对齐升级）、Packages、Projects（含修复回滚）、Settings（设置编辑器 + 键盘快捷方式）页面已接入对应服务，外部进程输出统一显示在 Output 面板；底部面板为 输出/问题/终端 三页签，Problems 实时派生 WARNING/ERROR 条目，新 ERROR 自动切页并展开（design.md §9）。
- Git 集成（只读 diff、不提供编辑）：`Nornia.Git` 通过 `IGitService`（git CLI + `-C <repo>`）提供状态（porcelain v2）、暂存/取消暂存、丢弃、提交、分支、历史（分页）、拉取/推送（`GitPullOptions` 安全拉取：快进 + 脏工作区拒绝）、获取、贮藏（list/stash/pop/drop）、传入/传出提交与净文件影响（`HEAD...upstream` / `upstream...HEAD`）、逐文件统一 diff 解析（`GitStatusParser`/`GitDiffParser`/`GitLogParser` 为纯文本解析、可单测）与流式 diff（`StreamDiffAsync`/`StreamCommitFileDiffAsync` + `GitDiffStreamParser`，500K 行上限）。桌面共享编辑器区（`EditorAreaViewModel`/`EditorAreaView`）维护同一标签条：资源管理器打开文件生成 `FilePreviewTab`（只读预览），`DiffTab` 支持行内/左右分栏 diff、未修改区域折叠、字符级差异、更改导航（Alt+F5 族）与 +N/−M 统计（容量分层见 design.md §13.2/§13.5）。顶层「源代码管理」页（`GitViewModel`/`GitView`）在二级左侧栏展示分支/同步状态（↑↓）、暂存/未暂存更改（平铺/树状布局、多选批量命令、拖放暂存）、贮藏、提交拆分菜单（提交/提交并推送/提交并同步）、获取/拉取/推送/安全同步、传入/传出更改与历史；`GitRepositoryWatcher` 文件监视自动刷新（`git.autorefresh`，design.md §9）。`StatusRefreshed` 驱动资源管理器树上的 git 状态字母与“含更改”文件夹圆点（`WorkspaceViewModel.ApplyGitStatus`/`WorkspaceStatusSource`）。Git 命令只暴露在 Desktop，CLI 不做 git 套皮。Git 根由项目工作区上下文在运行时向上查找 `.git` 推导（`ProjectWorkspaceService`），不再单独持久化。
- **设置系统（Settings v2）**：用户设置 `%LOCALAPPDATA%\Nornia\settings.jsonc` + 工作区 `<工作区>\.vscode\settings.json`（JSONC 注释保留、原子写、外部修改 200ms 去抖热加载、乐观修订冲突检测）；机器状态 `state.json`（布局/最近标签/阅读态，原子写）。`BuiltInSettingsCatalog`（约 33 个强类型设置，作用域 User/Workspace/Language/MachineState，仓库级设置不能启动外部进程——外部编辑器命令与自定义 Shell 仅用户级）；`ScopedSettingsService`（快照/提交/重置/会话）；`LegacySettingsMigrator` 一次性迁移旧 `settings.json`（v2→v3，备份不删）。设置编辑器（`SettingsEditorViewModel`）：搜索/分类/作用域/语言覆盖/来源链/逐键重置/分类恢复/布局恢复/冲突四项处理（design.md §14）。
- **命令平台与快捷键**：`CommandRegistry`（约 30 命令，When 上下文 `terminalFocus`/`inputFocus`/`editorTextFocus`）+ `KeybindingService`（`keybindings.json`：默认+用户合并、`-` 移除、和弦、冲突/诊断、三方合并保存、热重载）+ QuickInput（命令面板 Ctrl+Shift+P、快速打开 Ctrl+P，模糊子序列过滤，文件枚举上限 5000）；Ctrl+W / Ctrl+PgUp/PgDn 按终端焦点路由（终端焦点时作用于终端会话）。
- **工作台与代码编辑器**：统一标签条（内容页标签 + 文档标签投影、关闭族/固定/预览转正/拖拽重排，design.md §13.1）；TextMate 语法高亮（30 语言、后台分析、LRU 缓存、快照版本门控，代码与 Diff 共用 `CodeToken*` 主题调色板）；折叠/大纲/迷你地图/Ctrl+滚轮缩放/查找与转到行/阅读态持久化（`state.json` 按工作区）。
- SQLite 位于 `%LOCALAPPDATA%\Nornia\nornia.db`。schema 演进使用版本化 `IDatabaseMigration`（`InitialSchemaMigration`=1、`PackageArchitectureMigration`=2、`RepairLoggingMigration`=3：logs 增列 + `environment_repair_logs`），由 `MigrationRunner` 在事务中按版本顺序执行并记录到 `schema_migrations`；迁移逻辑与执行机制分离，新增 schema 变更只需新增迁移类。仓库依赖 `ISqliteConnectionFactory` 抽象，可用任意连接源替换（文件、共享内存等）以便测试。
- Runtime/Package 扫描使用 upsert 快照：已存在的记录按主键更新，未再次发现的 Runtime 标记为 Missing（`GetAllAsync` 只返回在场记录，Missing 历史仅存于库内）。清单刷新分三层新鲜度：内存 30 秒合并（`InventoryScanCacheSeconds`）→ 持久化快照 TTL 门控（`PersistedScanTtlSeconds`，默认 6 小时；`scan_state` 表记录各 kind 的扫描时间/耗时/环境指纹，Runtime 侧指纹由 `EnvironmentFingerprintProvider` 零进程派生计算 PATH + 五个命令行工具候选路径 + VC++ 注册表版本，指纹缺失或变化时 fail-open 全扫）→ 全量扫描后重写快照与 `scan_state`。**显式动作（页面「重新扫描/刷新」按钮、CLI `env check`/`env fix`、任何安装/卸载/升级/修复之后的清单重载）一律走 `RefreshForcedAsync` 绕过全部门控**；页面首激活先 `GetPersistedAsync` 秒出首屏再走门控刷新，并在页头显示「上次扫描」。
- Dashboard 的计数由 `SummaryRepository` 一条聚合 SQL 提供；文件系统缓存扫描结果缓存 60 秒，`CleanAsync` 总是使用强制刷新并在清理后失效缓存。
- Output 日志仍实时显示，并经异步单写入队列落入 SQLite；启动时自动清理 90 天前的 SQLite 操作日志。Serilog 文件日志保留 14 天。这些数值集中在 `Nornia.Core.NorniaSettings`。异常经 `UiLogService.WriteException` 结构化落盘（`ExceptionDiagnosticFormatter` 多行诊断 + `details_json` + Serilog 级别映射）。

## 语言策略

Desktop 界面语言固定为简体中文。CLI 使用 `--lang <language-code>` 或系统区域设置选择语言：英文资源是默认（neutral）资源，`CliMessages.zh-Hans.resx` 提供简体中文卫星资源，未翻译的键回退到英文。例如 `Nornia --lang zh-Hans runtime list`。

## 文件组织约定

一个文件 = 一个领域/职责单元，小类型按领域合并，避免“每类一个文件”的碎片化：

- **CLI 命令**按领域合并（`Commands/RuntimeCommands.cs`、`ToolCommands.cs`、`PackageCommands.cs`、`CacheCommands.cs`、`EnvironmentCommands.cs`、`ProjectCommands.cs`），路由基础设施（`ICommand` + `CommandDispatcher` + `HelpCommand` 除外——HelpCommand 与路由不同 namespace，保持独立）合并在 `CommandDispatcher.cs`。
- **接口**按领域合并（`Nornia.Core/Interfaces` 下的 `ProcessContracts.cs`、`RepositoryContracts.cs`、`ProviderContracts.cs`、`InventoryContracts.cs`）；小型接口与实现放同一文件（如 `ConfirmationService.cs` 内含接口与实现）。
- **模型**按领域合并（`GitModels.cs`、`PackageModels.cs`、`ProcessModels.cs`、`ProjectModels.cs`、`CoreModels.cs`）；`EnvironmentComponentCatalog.cs`、`NorniaSettings.cs` 等内聚大文件保持独立。
- **Converter** 全部集中在 `Nornia.Desktop/Converters/Converters.cs`；**ViewModels** 的辅助类型并入所属页面（`NavigationItem`→`MainViewModel.cs`、`WorkbenchTabViewModel.cs` 承载标签类型族 + 环境小节元数据、`SettingsEditorViewModel.cs` 承载 `SettingEditorItem`/`KeyboardShortcutItem`、`GitViewModel.cs` 承载 SCM 树行类型族）。
- **Desktop 子系统目录**：设置系统集中在 `Nornia.Desktop/Configuration/`（模型/JSONC 文档/存储/解析器/协调器/服务/状态库/迁移器），命令平台集中在 `Nornia.Desktop/Commands/`（注册表+上下文键+when 表达式、快捷键服务、keybindings JSONC 文档、命令清单引导），代码编辑器纯逻辑集中在 `Nornia.Desktop/Code/`（语言目录/分词/大纲/折叠/迷你地图几何/diff 呈现/容量策略/只读文档源，均无 WPF 依赖、可单测）；纯逻辑与 WPF 视图代码后置分开放置。
- **大而内聚的类保持单文件**（>200 行：`GitService`、`GitViewModel`、`AppDataCacheService`、`WingetProvider`、`ProjectsViewModel`、`EnvironmentRollbackService`（含追踪执行器）等），不制造巨型文件。
- **约束**：重组只移动文件，不改类型名、不改 namespace；XAML 代码后置 partial 类必须与 .xaml 同文件（`x:Class` 要求）。

## 测试

- `tests/Nornia.Tests` 是单元测试：仓储/迁移使用临时 SQLite 文件或共享内存连接工厂；服务与 ViewModel 通过 `Fakes/` 下的手写替身（`FakeProcessRunner`、`FakeUiLogService`、`FakeRuntimeInventory`、`FakeGitService` 等）注入，不依赖真实进程或 WPF 窗口。统一外壳/编辑器区/资源管理器装饰相关测试见 `ExplorerPageViewModelTests`（打开项目同步资源管理器/终端/项目目录、共享编辑器标签、Git 状态刷新驱动装饰）、`GitViewModelTests`（树状/平铺布局与多选、传入/传出、贮藏、拉取推送策略、日志分页）、`EditorAreaViewModelTests`、`WorkspaceViewModelTests`（共享编辑器标签、每标签 diff 模式、状态字母与文件夹圆点、reveal-in-explorer 展开祖先并选中、区分文件夹展开与文件打开）。
- `tests/Nornia.Settings.Tests` 是设置系统专用单元测试：JSONC 解析/补丁（注释、BOM、换行保留）、作用域解析优先级与非法作用域/语言覆盖诊断、提交验证失败与乐观修订冲突、重置、外部变更观察与会话键过滤、`state.json` 往返与布局重置、旧版 `settings.json` 一次性迁移。
- Markdown 文件预览位于 `Nornia.Desktop/Markdown` 与 `MarkdownPreviewView`：Markdig 负责 AST，WPF `FlowDocument` 负责原生排版，WpfMath 负责 TeX 公式。Markdown 预览保持只读，完整加载文件默认渲染，窗口化/摘要大文件回退源码；本地相对图片可加载，远程图片与脚本不执行。公式失败保留输入文本，链接由文件预览页路由到工作台文件打开或系统浏览器。
- 修复与回滚测试：`TrackedEnvironmentRepairExecutorTests`（correlation_id 批次日志、单步失败不中断、取消中断与汇总、回滚计划逆序与手动策略）、`WingetProviderHardeningTests`（`0x8A150016` 多实例逐版本卸载回退、无匹配卸载提示、spinner 噪声）、`RuntimePackageResolverTests`（Windows App Runtime 包 Id 推导）。
- 统一外壳的交互约定：二级左侧栏由 `PageViewModel.Sidebar` 提供（环境管理=分区列表、资源管理器=工作区树、源代码管理=Git 视图、项目管理=项目目录，设置无侧栏收起该列）；资源管理器单击文件夹行切换展开/折叠由视图层 `WorkspaceView` 处理（`WorkspaceNode.IsExpanded` 仍驱动懒加载），文件单击才在共享编辑器里打开；diff 标签有「加载中/无差异」两态（`DiffTab.IsLoaded`/`IsEmptyDiff`）。
- `tests/Nornia.Benchmarks` 使用 BenchmarkDotNet，运行示例：`dotnet run -c Release --project tests/Nornia.Benchmarks --filter CatalogLookupBenchmark`。
- 外观与交互：语法高亮随三主题 `CodeToken*` 调色板（`CodeTokenColorizer`；`ThemeHighlightingColorizer` 仅处理 AvalonEdit 回退定义），`ThemeService` 经 `ThemeEvents` 广播运行时换肤，代码与 Diff 视图即时重应用；强调色（`ThemeFactory` 覆盖字典，预设+自定义）即时生效并持久化；布局（窗口边界/侧栏宽/面板高/SCM 分栏）持久化并防离屏钳制；右键菜单见各 ViewModel 命令；交互式终端走 ConPTY（`TerminalScreen`/`AnsiParser` 纯模型、`TerminalSurfaceControl` 渲染与输入），失败时回退重定向模式；文件图标按大类语义字形 + 八色系。
- 测试补充：终端模型（`TerminalScreenTests`：ANSI/滚动回滚/备用屏/256 色/持久 SGR）、图标（`FileTypeIconTests`）、布局设置往返（`LayoutSettingsTests`）、高亮映射（`ThemeHighlightingColorizerTests`）、强调色（`ThemeFactoryTests`）。
- Winget 输出解析使用 `Fixtures/Winget/` golden fixtures（真实捕获的中文清单含 spinner/进度条噪声行、英文表头、无结果消息、截断名、未来 JSON 契约形态）驱动 `WingetOutputParserTests`；`WingetEnrichPerformanceTests` 用阻塞式 Fake Runner 确定性地断言回查并发 ≤4、按 Id 去重与失败不阻塞，不依赖真实计时。
- Winget 集成测试（`WingetIntegrationTests`）默认跳过，只有设置 `NORNIA_WINGET_INTEGRATION=1` 时才运行，用于在隔离 Windows VM 或 CI 上验证 Provider/探针/错误语义（版本行、`winget list` 端到端解析、表头语言无关断言、无匹配退出码实测记录）。常规运行可用 `dotnet test --filter Category!=Integration` 排除。
- 后续规划：
  - WPF UI 测试可使用 FlaUI 或 Appium（Windows Application Driver）在 CI 上驱动真实窗口；
  - 使用 Testcontainers 的 Windows 容器（或 GitHub Actions `windows-latest` runner）搭建隔离的 Winget 集成环境，并针对 Winget 输出协议版本做兼容性矩阵测试。
