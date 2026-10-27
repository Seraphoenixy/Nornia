# 2026-10-25 改进路线交付（①快赢 → ②工作区符号 → ③Blame/文件历史 → ④Markdown 并排）

按用户批准的优先级路线一次性交付四个阶段，硬约束是"内存占用不得过量增长"。全量测试从 1714 增至 1750 全绿。

## 同日第三轮：测试套件提速与用例精简

- **耗时画像（TRX 按类聚合，基线 73.3s/1747 项）**：ProcessRunnerTests 14.5s（真子进程）、TerminalStartupTests 7.7s（真 ConPTY 会话）、DesignSystemResourceTests 6.0s（88 项各整读源文件）、大文件类合计 ~8s（每个用例写 8.6-9.5MB 临时文件）。
- **根因结论**：程序集 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`（AssemblyInfo.cs，原因：VM 测试触碰 Application 资源 + 共享 STA 宿主，BAML 加载期间字典可变）→ 全套件串行，sum≈wall。放开并行需要先拆 STA 集合，属独立工程，未动。
- **已落地优化（全部保绿，1746 项）**：ProcessRunner 14.5→6.3s（简单用例换 cmd.exe 冷启、1MB 排水改"预生成文件+type"、进度双测合一）；窗口化分层测试不再写真 8MB+ 文件——注入 `ContentLimits` 小上限（Full=64KB/Windowed=1MB）配 0.45MB 文件，代码路径与生产阈值完全一致（MarkdownFilePreview 2.17→0.14s、Escape 0.81→0.02s、WindowedFind 两个类 9.5MB×9 次→0.45MB）；Terminal 打字回归不再跑 `dotnet`（终端内首次 dotnet 触发 SDK 探测实测可达 20s，--version 与 build 同样冷，改为纯 PowerShell 标记命令）+ PSReadLine 缓冲 1500→400ms；DesignSystem 源文件读取走 `ConcurrentDictionary` 缓存（隔离跑 6→4s，套件内受 IO 竞争影响收益打折——该类剩余耗时主要是字符串扫描而非 IO）。
- **两个反直觉教训**：①不同编码的字节样本不能合并进同一次子进程输出——解码器按读块检测，分块边界混合会人为触发 GB18030 误回退（已试败并回退，测试注释里有记录）；②cmd 链式命令（`type & ping & echo`）会孤儿化孙进程，孤儿持有管道写端导致"终止子进程"语义无法验证——单进程悬挂类用例必须 powershell。
- **环境噪声警告**：同代码全量墙钟在 1m13s–2m02s 间波动（Huorong/Defender 对测试子进程的实时扫描 + 负载）；评估测试性能改动必须用 TRX 按类聚合的 sum，不要用单次墙钟。

## 同日第二轮：性能与易用性全面改进（P1–P10，全量 1758 全绿）

- **拼音首字母匹配**（`Code/PinyinInitials.cs`，GBK 区位边界算法）：经典边界表的 h 档起点是错的（B9A7），实测真实边界 **B9FE**（过=B9FD 是最后一个 g；关/管 guan 常见表会误判为 h）——已修正并测试。QuickInput 每键全量打分改用预计算的小写标题+首字母缓存（Open/ReplaceItems 重建），新增 90k 首字母档（100k 标题子串之下、50k 详情之上）。
- **设置搜索框**：VM 的 `SearchText` 过滤（含 @modified/@user/@workspace/@id: 令牌）一直存在，缺的只是 XAML 输入口——加了水印搜索框。
- **Blame 边距重写**：FormattedText 在 SetAnnotations 一次性缓存（滚动帧零分配）、加载占位行、截断常驻"⚠ 已截断"标记、MouseMove 悬浮详情（作者·完整时间·短哈希·主题）。Tab 增加 `IsBlameLoading`/`AutoEnableBlameOnce`；EditorArea 的 `BlamePreferred` 会话级偏好让新标签自动跟随（GitLens 全局开关心智）。
- **文件历史入口扩展**：资源管理器文件右键（`WorkspaceViewModel.FileHistoryRequested` → ExplorerPageViewModel 路由到 Git 页）+ 命令面板"查看当前文件的提交历史"（仓库相对路径解析，仓库外静默）——工作区干净时也有入口。
- **大文件逃生门**：窗口化/摘要标签顶部悬浮"仍要尝试全文打开"（`OpenFullAnyway`：本标签 FullSourceBytes 抬到 EffectiveWindowedSourceBytes 再重载；仍受 32MB 上限约束）。
- **Esc 统一退层**（窗口级兜底，隧道先于视图）：终端放行，其余 QuickInput → 跳转行 → 查找框（`MainViewModel.DismissTopLayer`）。
- **标签溢出**：标签条右端常驻"⋯"按钮 + `workbench.action.showAllEditors` 命令 → 复用 MRU 切换器。Ctrl+T/F12 保持空闲（避免语义错位）。
- **通知 toast 底座**：`PageViewModel.OperationFailed` 事件（失败终态单一咽喉点）→ MainViewModel 聚合所有页 → 右上角 toast（容量 3、8 秒自动消失、点击关闭；Task.Delay+IUiDispatcher 回排，不用 DispatcherTimer）。历史面板刻意不做（Output 已有）。
- **性能**：编辑器载荷预算 128MB→**64MB**（只回收非选中标签）；快速打开失效 1.5s 去抖（文件事件风暴不打断后台索引）；spool 临时文件清理已验证正确（DisposeAsync File.Delete）。**LOH 三重复制（Content/TextDocument/FlowDocument）未动**——需要视图所有权重构，独立会话处理。
- **Git 更改平铺列表 type-ahead**：字母/数字按文件名前缀直跳（800ms 缓冲，退格删除；GitView code-behind 纯输入处理）。
- **并排 Markdown**：源码光标跨标题 → 渲染面滚到对应标题（单向同步 + 重入闸）；反向滚动同步（块级双向映射）刻意不做。
- **编码强制解码测试补齐**：无 BOM UTF-16 自动判二进制但强制可解；奇数字节宽容替换符不抛 UI。


## 事后裁决：②工作区符号索引已整体移除（2026-10-25 当天，用户判定"占内存还没收益"）

- 已删除：`Code/WorkspaceSymbolIndex.cs`、`WorkspaceSymbolIndexTests.cs`、MainViewModel 的索引缓存/`OpenWorkspaceSymbolPickerAsync`/`GoToDefinitionAsync`、`FilePreviewTab.WordAtCaretProvider` 及视图注入、命令 `workbench.action.showAllSymbols`(Ctrl+T) 与 `editor.action.revealDefinition`(F12) 的注册与路由。
- **保留**（与符号索引无关的通用修正，独立有价值）：QuickInput 的 `ModePrefix` 前缀剥离、`ReplaceItems` 立即重过滤、详情模糊匹配档、快速打开 `>`/`?` 前缀模式。
- **不要再重提**：语法级工作区符号索引/F12 转到定义——索引要么常驻（内存超标）要么按需重建（每次等待秒级），用户认定收益不抵内存。若未来重做，方向应是外部引擎（如 ripgrep 式临时进程）或持久化磁盘索引，而不是内存驻留。


## 内存受限设计的关键决策（候选记忆，多次验证后可提升到 AGENTS.md）

- **工作区符号索引（`Code/WorkspaceSymbolIndex.cs`）**：只在首次 Ctrl+T/F12 时构建，不随启动常驻；名称构建期驻留、路径共享引用；符号上限 150k/文件 20k/单文件 2MB，超限 `Truncated` 置位并显示在标题与详情里；工作区切换/文件变化时**整表丢弃**（挂在 `InvalidateQuickOpenCache` 同一钩子），下次使用重建。Ctrl+T 选择器每键只灌前 200 条预过滤行（`QuickInput.ReplaceItems` 现在会立即重过滤——此前切换条目集合后可见列表滞后一次击键，顺手修了）。
- **F12 转到定义是语法级**：光标词由视图注入 `WordAtCaretProvider`（AvalonEdit 词扩展）；索引 `FindByName` 先精确（区分大小写）后词边界后缀（Python 的 "def main" 对应光标词 "main"）；多命中弹 QuickInput，同语言/同文件优先。不做语义解析（无重载/分部概念），未命中写 tab.Notice。
- **QuickInput 三处行为修正**：`ModePrefix`（'@' ':' '>' '?' 打分前剥离，否则 ":42" 永远过滤不到行项目）；单动作项兜底只在 ModePrefix 非空时启用（放宽到普通面板会破坏"乱序关键字必须过滤空"的既有测试语义）；详情模糊匹配档（5k 分，命令 id/键位兜底，拉丁关键字对中文标题可用）。
- **Blame（`IGitService.GetBlameAsync` + `--porcelain`）**：有界读取（16MB 上限即终止子进程，截断置位）；模型紧凑（提交表 + 每行 int 下标，20 万行/1 万提交上限）；边距 `Views/CodeBlameMargin.cs`（段首行显示"作者 · 相对时间 · 短哈希"，纯布局在 `Code/BlameAnnotationLayout.cs`）；窗口化/二进制不支持（行集随翻页变化会整体错位）；外部重载成功后自动重取（工作树未提交行归 0000 哈希块，作者显示"未提交"）。
- **文件历史（`GetFileLogAsync` = `git log --follow`）**：Git 侧栏新分区（右键已暂存/未暂存文件行进入），分页 50/次；行点击复用 `GitDiffRequest(commitHash)` 打开该提交对该文件的 diff（预览语义）。文件夹菜单**故意没加**（`--follow` 只接受单文件路径spec）。
- **Markdown 并排（`MarkdownViewMode.SideBySide`）**：三列布局（源码|GridSplitter|渲染）由 `UpdateMarkdownLayout` 驱动（与大纲栏同模式）；渲染语义与纯渲染一致（门控：仅 Full 层），从纯渲染切入保留产物、从源码切入重建；纯渲染跳过的高亮/折叠派生在并排时补齐（`OnMarkdownModeChanged` else 分支）。`EnsureMarkdownSourceMode` 与渲染发布/主题重建的判断都已包含 SideBySide。
- **查找三键进命令平台**：Ctrl+F/Ctrl+G/F3(±Shift) 从两个视图 code-behind 移除，注册为 `editor.action.startFind/findNext/findPrevious` + `workbench.action.gotoLine`（when 表达式解析器支持括号）。视图侧用 `FindFocusRequest`/`GoToLineFocusRequest` 请求序号解决"ObservableProperty 同值不通知"的重复聚焦；diff 侧用 `FindStepRequested` 事件 + `FindSelectionSeeder` 委托（匹配列表必须住在视图层，VM 只发方向）。
- **大文件阈值设置化**：`nornia.reading.fullSourceMb/windowedSourceMb/fullDiffLines/windowedDiffLines`（编辑器分类）；`ReadOnlyContentLimits` record 单调性由 `Effective*` 抬平（窗口化 < 全量时窗口化层自然消失）；DiffTab 的 `BuildDiffSnapshot` 静态构建因此多了 limits 参数。
- **编码选择器**：状态栏编码可点击（自动/UTF-8/GB18030/UTF-16 LE/BE），强制解码走 `DecodeAsync` 新增的 `forcedEncoding` 参数（跳过探测与二进制判定，替换回退）；窗口化源 `FileReadOnlyDocumentSource` 同样接受强制编码；标签级会话态不持久化。
- **重开已关闭编辑器（Ctrl+Shift+T）**：只记描述符（路径/GitDiffRequest），不持有标签对象；预览槽替换与 LRU 驱逐不入史（VS Code 同语义）；文件已消失的记录跳过继续下一条。

## AGENTS.md 已同步的修正

- "Git 集成只读 diff" 已过期 → 改为完整 SCM 面描述（ApplyHunk/Stage/Commit/Push 全套，GitServiceTests/GitViewModelTests 覆盖）。
- 定位表述从"只读工作台"改为"不编辑代码的工作台（Git 仓库写操作允许，文件内容只读）"。

## 后续候选（本轮未做，评估时用过）

- Blame 悬浮详情（当前只有段首行注解）；文件历史的资源管理器右键入口（需跨 VM 布线）；`#` 前缀在快速打开里直达工作区符号（现在是 Ctrl+T 专用面板）；通知中心/toast；标签溢出搜索菜单；AGENTS.md 把四阶段能力正式提升进"关键设计决策"（需用户确认）。
