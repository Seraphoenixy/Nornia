# Nornia 设计说明

## 1. 产品边界

Nornia 是面向 Windows 开发者的本地开发环境生命周期管理平台。它不是软件列表管理器，而是围绕以下问题组织能力：

- 本机已经安装了哪些 Runtime 和开发工具？
- 软件缺失时应通过什么渠道获取或移除？
- 某个项目声明了哪些环境要求？
- 当前机器是否满足这些要求？
- GUI、CLI 和未来的 MCP Server 如何复用同一套能力？

## 2. 架构与依赖方向

Nornia 采用 Clean Architecture 思想，领域抽象位于中心，Windows 命令、SQLite、WPF 和 CLI 位于外层。

```text
Nornia.Desktop ─┐
                ├──> Nornia.Core
Nornia.CLI ─────┤
                │
Nornia.Runtime ─┤
Nornia.Package ─┤
Nornia.Project ─┤
Nornia.Git ─────┤
Nornia.Storage ─┘
```

项目职责如下：

| 项目 | 职责 | 主要依赖 |
| --- | --- | --- |
| `Nornia.Core` | 领域模型、Provider/Git 接口、进程执行抽象 | 无项目依赖 |
| `Nornia.Runtime` | Runtime 与开发工具的发现和版本解析 | Core |
| `Nornia.Package` | 软件包搜索、清单、软件变更与环境修复回滚 | Core |
| `Nornia.Project` | `Nornia.yaml`、项目环境检查、项目启动 | Core |
| `Nornia.Git` | git CLI 封装（`-C <repo>`）与状态/diff/日志纯文本解析 | Core |
| `Nornia.Storage` | SQLite 建库、迁移和仓储实现 | Core |
| `Nornia.Composition` | 唯一服务层组合根：`AddNorniaServices()` 注册进程、Git、持久化、Provider、服务 | Core 及功能模块 |
| `Nornia.Desktop` | WPF、MVVM、设置/命令平台、依赖注入和展示 | Composition 及功能模块 |
| `Nornia.CLI` | 自动化入口和功能编排（仅追加 CLI 展示层注册） | Composition |
| `Nornia.Tests` | Provider、Git、配置文件、规则引擎与桌面服务测试 | 被测模块 |
| `Nornia.Settings.Tests` | 设置系统（解析/提交/冲突/迁移）单元测试 | Nornia.Desktop.Configuration |

必须保持以下约束：

- Core 不引用 WPF、CLI、SQLite 或具体 Provider。
- Runtime 只回答“本机有什么”，Package 负责“如何获取或移除”。
- Project 不直接调用 Winget，环境检查与环境修复保持分离。
- WPF code-behind 仅承担视图初始化，不承载业务逻辑。

## 3. 通用进程执行

Windows 开发环境检测和 Winget 操作最终都依赖外部进程。`IProcessRunner` 是统一边界，`ProcessRunner` 是当前实现。

设计要求：

- 使用 `ProcessStartInfo.ArgumentList` 逐项传参，不拼接 Shell 命令。
- 同时读取标准输出和标准错误，避免缓冲区阻塞。
- 通过 `IProgress<ProcessOutput>` 实时上报日志。
- 支持 `CancellationToken`，取消时终止整个进程树。
- 可执行文件不存在时返回失败结果，由上层决定“未检测到”或“操作失败”。

Runtime 和 Winget Provider 都依赖 `IProcessRunner`，因此测试可以使用 Fake Runner，不需要真实修改开发机。

## 4. Runtime Provider 系统

### 4.1 抽象

`IRuntimeProvider` 定义以下能力：

- `DetectAsync`：发现本机已安装实例。
- `GetVersionsAsync`：返回发现到的版本集合。
- `InstallAsync`、`RemoveAsync`、`UpdateAsync`：保留统一扩展契约。

当前 Runtime Provider 的安装、移除和升级会提示改用 Package Provider。这一决策避免 Runtime 模块直接依赖 Winget，并允许未来按策略选择 Winget、Scoop、Chocolatey 或专用版本管理器。

### 4.2 Provider 清单

| Provider | 检测命令 | 解析结果 |
| --- | --- | --- |
| `.NET`（SDK） | `dotnet --list-sdks` | 可返回多个 SDK 版本和各自安装目录 |
| `.NET Desktop Runtime` | `dotnet --list-runtimes` | 仅取 `Microsoft.WindowsDesktop.App` 行，返回多个版本和安装目录 |
| Node.js | `node --version` | 去除版本前的 `v` |
| Python | `python --version` | 同时兼容 stdout 和 stderr 版本输出 |
| Git | `git --version` | 保留 `windows.N` 版本后缀 |
| Java | `java -version` | 从 stderr 或 stdout 提取版本 |
| Visual C++ Redistributable | 注册表 `HKLM\SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes` | x86/x64/arm64 三架构各一条；`Installed=0` 判定为损坏。注册表键位只作为稳定标识输入，`InstallPath` 暴露共享 DLL 真实部署目录（`System32`，x86 在 64 位系统为 `SysWOW64`） |
| Windows App Runtime | `powershell Get-AppxPackage Microsoft.WindowsAppRuntime.*` | 逐条解析 `Version\|InstallLocation\|Architecture`；安装目录不存在时判定为损坏 |

命令型 Provider 先通过 `where.exe` 定位可执行文件；失败时依次检查 `PATH` 和 Windows `App Paths` 注册表。这样可以识别 PowerShell 能启动、但 `where.exe` 无法定位的 Python 安装。

**损坏态（Broken）检测**：除“命令不存在”外，Provider 还会上报结构上损坏的安装——`RuntimeStatus.Error` + `DetectionStatus.Broken` + `DetectionError` 诊断文本。覆盖场景：`.NET` 可执行文件存在但 `--list-sdks` 非零退出；SDK/桌面运行时安装目录缺失；VC++ 注册表 `Installed=0`；Windows App Runtime Appx 安装位置不存在。损坏实例与已安装实例分开返回（`RuntimeDetectionResult` 的 installed/broken 双列表），库存按同一 upsert 快照持久化。

### 4.3 组件分类与标识

组件目录（`EnvironmentComponentCatalog`）统一分类：**Runtime** = Visual C++ Redistributable、.NET Desktop Runtime、Windows App Runtime；**开发工具** = .NET SDK、Node.js、Python、Java、Git。库存仍使用 Runtime 模型和表保存，分类由组件名称动态推导；目录预建 Id/显示名/别名索引，解析 O(1)。Java 仅支持检测，暂不提供 Winget 自动管理。

每个发现结果包含：

```text
Id, Name, Version, InstallPath, Architecture,
Provider, InstallDate, Status
```

`Id` 根据名称、版本和路径生成稳定标识。同一安装在多次扫描中会得到相同 ID，便于后续持久化时执行 upsert。无法读取安装目录时间时，使用当前 Unix Timestamp 作为回退值。

`RuntimeDiscoveryService` 聚合所有已注册的 `IRuntimeProvider`，并行执行扫描后合并结果。新增 Provider 只需实现接口并在 Desktop 或 CLI 组合根注册。

## 5. Package Manager

### 5.1 Provider 边界

`IPackageProvider` 包含：

- `SearchAsync`
- `ListInstalledAsync`
- `InstallAsync`
- `UninstallAsync`
- `UpgradeAsync`

不维护私有软件仓库。已实现三个 Provider：

| Provider | 形态 | 解析策略 |
| --- | --- | --- |
| `WingetProvider` | Windows 默认渠道，默认 Provider | 双模式输出解析（见 5.2），截断名回查 |
| `ScoopProvider` | 可选（`where.exe scoop` 探测可用） | 行式输出保守解析；`install <id>@<version>` 指定版本 |
| `ChocolateyProvider` | 可选（`where.exe choco` 探测可用） | 稳定机器可读 `--limit-output --no-progress` 格式，`id\|version` 两列 |

`PackageProviderSelector` 按“优先指定 → 可用探测 → 默认 Winget”顺序为操作选择 Provider：环境修复步骤可携带 `PreferredProvider`，回滚与库存刷新默认走可用 Provider；组合根把 Winget 注册在 `IEnumerable<IPackageProvider>` 末位，单 Provider 屏幕与库存刷新因此默认使用 Windows 默认渠道而不是可能缺失的可选 CLI。

### 5.2 Winget 调用策略

所有变更命令使用精确包 ID，并添加非交互及协议参数：

```text
winget install --id <id> --exact ...
winget uninstall --id <id> --exact ...
winget upgrade --id <id> --exact ...
```

安装可选指定准确包版本。命令输出通过 `IProgress<ProcessOutput>` 实时传给 CLI，未来可直接连接桌面端 Output 面板。

**输出协议与解析（双模式）。** `WingetProbe` 会话级惰性探测并缓存本机 winget 的能力（`WingetCapabilities`）：版本、是否支持结构化输出（扫描 `--help` 中是否存在 `--output`）、表头语言和实测无匹配退出码。`WingetOutputParserFactory` 按能力选择解析器：

- `WingetTableParser`（当前默认）：解析固定宽度表格。表头经双语词典（英文 + 简体中文）动态定位 `name`、`id`、`version`、`available`、`source` 列，列边界按字符显示宽度（CJK 占 2 列）切片而不是按空白切列，因此名称列内的连续空格与列顺序漂移都能容忍；`Available` 列缺失或数据为空时解析为 `null`；表头无法完整识别（缺少任意一个必需列）时回退到标准的列顺序拆分。解析结果携带 `SkippedRowCount` 诊断计数，布局漂移时不会静默丢行。
- `WingetJsonParser`（契约先行）：当探针报告结构化输出可用时启用。其 schema 参照 `winget export` 的 JSON 形态定义在 `WingetJsonContracts` 并仅以合成 fixture 单测隔离；真实输出样本出现前默认关闭，schema 漂移时解析为空并由 Provider 回退一次表格解析，避免返回误导性的空清单。

**截断名回查。** winget 会按终端宽度截断名称（保留 `…` 后缀）。`ListInstalledAsync` 对非本地身份（非 `ARP\`/`MSIX\`）的截断行按 Id 去重回查一次 `winget show`，以受限并发（≤4）执行并携带 20 秒独立超时；失败或超时保留截断名、通过 Progress 上报诊断，不阻塞清单返回。解析成功的全名按 Id 缓存在进程内（上限 512 项），同一进程内重复刷新不再回查。

**退出码语义。** 无匹配退出码随 winget 版本漂移（历史 `0x8A150011`，v1.10 实测 `0x8A150014`），`WingetExitCodes` 同时接受两者并由探针记录实测值；无匹配对 `search`/`list` 返回空列表而不是异常。其余非零退出抛出 `WingetException`（继承 `InvalidOperationException`，携带退出码、命令名与输出摘录，CLI/Desktop 现有捕获链零改动接入）。另有两个专用语义码：

- `0x8A150016`（多个已安装实例匹配，如 .NET SDK 多个 feature band 或多个 MSIX 版本）：不带 `--version` 的卸载无法定位目标。`UninstallAsync` 捕获该码后**按版本逐个精确卸载**——先 `winget list --id <id> --exact` 查询实际安装版本（与清单共用双语解析器，此处刻意不按 Id 去重，多版本实例正是查询目的），再对每个版本执行 `uninstall --id <id> --exact --version <version>`；不使用 `--all-versions` 一刀切。逐版本卸载时再遇无匹配（并发移除/已处理）则跳过继续。版本查询本身失败时重新抛出原 `0x8A150016` 错误而不是掩盖。
- `0x8A15002B`（更新不适用，常见于缓存包快照领先于实际可升级的包）：Runtime 页升级循环把该码降级为 WARNING 日志（记录包 Id 与退出码）并继续下一个包，不让某个架构下无可用更新的包阻塞同批次其他架构的升级。

**超时分级与本地化。** 只读命令（`search`/`list`/`show`）使用 60 秒/20 秒短超时且不重试，变更命令保留 10 分钟超时加一次重试；探针与命令并行启动，探测开销不在关键路径上。`Nornia.Package` 内面向用户的消息（Progress 输出与最终用户可见异常）统一走 `WingetMessages.resx`（英文中性资源 + `zh-Hans` 卫星资源），随 `--lang` 或系统区域切换。

### 5.3 组件到软件包的映射

映射是声明式的：内置 `PackageMappings.json`（`idTemplate`/`versionTemplate`/`includeForArchitecture` 模板，`{major}`/`{minor}`/`{arch}` 占位符），新增组件只需目录条目 + 映射条目，不改解析器代码。当前支持的映射：

| 组件 | 分类 | Winget ID |
| --- | --- | --- |
| `.NET 10` | 开发工具 | `Microsoft.DotNet.SDK.10` |
| `Python 3.13` | 开发工具 | `Python.Python.3.13` |
| Node.js | 开发工具 | `OpenJS.NodeJS` |
| Git | 开发工具 | `Git.Git` |
| Windows App Runtime | Runtime | `Microsoft.WindowsAppRuntime.1.<release>` |

.NET 使用主版本选择 SDK 包，Python 使用主版本和次版本选择发行包；Node.js 和 Git 可把完整版本继续传给 Winget。Windows App Runtime 的 Appx 版本采用 `7000.x`（1.7）、`6000.x`（1.6）这类编码，包 Id 携带发布号，因此按规则推导：主版本 ≥1000 时取 `major/1000`，`1.x` 形式取次版本，无法推导时回退 `.1.8`。

Runtime 页对 Visual C++ Redistributable 有架构对齐规则：升级只尝试与所选 Runtime 行同架构的包（映射本身包含 x86 供安装/修复使用，但升级单行时不得先动另一架构）；可用版本匹配同样按架构过滤。

## 6. Project Environment

### 6.1 Nornia.yaml

项目根目录使用大小写固定的 `Nornia.yaml`：

```yaml
project:
  name: Ygdria
runtime:
  node:
    version: 22
tools:
  dotnet:
    version: 10
  git:
    version: 2.50
```

`EnvironmentProfileService` 负责：

- 在指定项目目录初始化配置文件。
- 加载并反序列化 YAML。
- 保存环境配置。
- 验证 `project.name` 等必要字段。
- 默认拒绝覆盖已有配置，避免破坏项目声明。
- 导出和导入经过验证的声明文件；声明可包含启动命令、目标架构/操作系统与非敏感环境变量，不能包含令牌或密码。

配置解析采用 YamlDotNet，并允许出现未来版本新增的未知字段，以便配置格式渐进扩展。

### 6.2 环境检查引擎

`EnvironmentCheckEngine` 是纯规则组件，不访问磁盘、不调用命令，也不安装软件。它的输入是：

```text
EnvironmentProfile + IReadOnlyCollection<Runtime>
```

输出为 `EnvironmentCheckResult` 集合，每项状态为：

| 状态 | 含义 |
| --- | --- |
| `PASS` | 找到满足声明版本的环境 |
| `FAIL` | 完全没有安装该组件 |
| `WARNING` | 已安装该组件，但版本不满足要求 |

名称映射会把配置键转换为显示名称，例如 `dotnet` 对应 `.NET`，`node` 对应 `Node.js`。

版本规则支持版本段前缀与比较器范围：要求 `10` 可匹配 `10.0.302`，要求 `3.13` 可匹配 `3.13.2`，`>=22 <23` 可匹配 22.x 但不会匹配 23.x。版本前导字符 `v` 会被忽略；Git 等 Runtime 的非数字发行后缀不影响数值比较。

稳定版要求不会被预发布 Runtime 满足：要求 `10` 时，`10.0.100-preview.1` 会得到 WARNING。首版不支持 `^`、`~`、`||` 等 NPM/SemVer 扩展语法；无效约束产生 FAIL，并标记为 `InvalidConstraint`。

检查与修复保持分离。`EnvironmentRepairPlanner` 只生成不可变修复计划：精确/前缀要求和带 `>=` 包容下界的范围可映射为目标版本；只有上界、排除性下界、无效范围以及 Java 等没有 Winget 映射的组件仅显示手动处理建议。`EnvironmentRepairExecutor` 仅在用户明确授权后执行计划，完成后刷新 Package/Runtime 快照并再次检查和持久化项目状态。

### 6.3 修复追踪与回滚

生产执行器是 `TrackedEnvironmentRepairExecutor`：每个修复批次绑定一个 `correlation_id`（操作日志 `BeginOperation("ENV_FIX", …)` 生成），逐步执行时：

1. 先探测组件当前版本（`previous_version`），再经 `PackageProviderSelector` 选择 Provider 执行安装；
2. 每步（无论成功失败）写入 `environment_repair_logs` 一行：组件、previous/target 版本、包 Id/版本、Provider、成功标记、回滚策略、失败原因与回滚提示；同时把步骤进度/成功/失败（含 Provider 错误输出摘录）以 JSON `details` 追加到 `operation_log`（同 `correlation_id`）；
3. 单步失败不中断批次：失败记入 `EnvironmentRepairFailure`（序号/总数/组件/前后版本/包/Provider/异常类型/原因/诊断输出/退出码/回滚策略），批次结束时汇总；
4. 取消（`OperationCanceledException`）中断批次并原样上抛，finally 里仍写入“执行被中断”汇总；
5. 批次结束后若存在失败，抛出 `EnvironmentRepairException`（携带 `correlation_id` 与失败列表），UI 日志经 `UiLogService.WriteException` 渲染为“失败步骤 n（correlation_id=…）+ 逐条原因”。

回滚策略（`RollbackStrategy`）在写入日志时确定：升级/安装类默认 `Automatic`（可用包管理器撤销）；Winget 明确 `PackageVersion` 的降级与默认黑名单包（VCRedist 2015+、.NET SDK Preview、WindowsAppRuntime）标记为 `Manual`，日志携带手动命令提示（先 `winget uninstall` 再按版本重装，或厂商安装程序）。

`EnvironmentRollbackService.BuildPlanAsync(correlationId)` 读取该批次日志、按 sequence 逆序为成功步骤生成 `EnvironmentRollbackPlan`（撤销最近操作优先）；`RollbackAsync` 逐步执行：`Automatic` 步骤按 previous 版本重装或卸载（previous 为 `0.0.0-uninstalled` 时卸载），`Manual` 步骤跳过并输出提示；重装失败降级为提示不中断后续步骤。

## 7. CLI 设计

CLI 输出程序集名称为 `Nornia.exe`。入口只负责参数分派、模块组合、输出格式和退出码，不复制领域规则。

```text
Nornia runtime list
Nornia runtime install <runtime> <version>
Nornia runtime remove <runtime> <version>
Nornia tool list
Nornia tool install <tool> <version>
Nornia tool remove <tool> <version>

Nornia package search <query>
Nornia package list
Nornia package install <id> [version]
Nornia package uninstall <id>
Nornia package upgrade <id>

Nornia env check [path]
Nornia env plan [path]
Nornia env fix [path] [--apply]
Nornia project init [path] [--name <name>]
Nornia project export [path] <destination>
Nornia project import <source> [path]
Nornia project open [path]
```

退出码约定：

| 退出码 | 含义 |
| --- | --- |
| `0` | 命令成功，环境检查全部通过 |
| `1` | 操作失败，或环境存在 FAIL/WARNING |
| `2` | 未知命令或参数组合 |
| `130` | 用户通过 Ctrl+C 取消 |

`project open` 在 CLI 中默认调用 `code <path>`。Desktop 从 `%LOCALAPPDATA%\Nornia\settings.json` 读取编辑器命令，并把项目目录作为唯一参数传入；Settings 页面可填写 PATH 命令或选择具体的 `.exe` 文件。

## 8. 数据存储

SQLite 是本机环境资产索引，使用 `Microsoft.Data.Sqlite.Core`、Windows SQLite Provider 和 Dapper，位置固定为 `%LOCALAPPDATA%\Nornia\nornia.db`。初始化通过 `schema_migrations` 版本表执行；每个迁移在事务内完成，因此可安全重复启动和升级旧库。数据库包含：

- `runtimes`
- `packages`
- `projects`
- `environment_profiles`
- `environment_bindings`
- `scan_state`（清单快照级扫描元数据：kind 主键 `runtimes`/`packages`、scanned_at、duration_ms、环境指纹）
- `logs`
- `environment_repair_logs`（环境修复批次步骤：correlation_id、sequence、组件、前后版本、包、Provider、成功标记、回滚策略、失败原因/提示）

所有时间字段存储 Unix Timestamp。Core 定义 `IRuntimeRepository`、`IPackageRepository`、`IProjectRepository`、`IEnvironmentProfileRepository`、`IOperationLogRepository`、`IEnvironmentRepairLogRepository` 和 `IOperationLogWriter`，Storage 提供 SQLite/Dapper 实现，避免领域层依赖 SQLite。

迁移现为三步：`InitialSchemaMigration`（1）、`PackageArchitectureMigration`（2）、`RepairLoggingMigration`（3）——为 `logs` 增列 `correlation_id`/`operation_kind`/`details_json`（按 `pragma_table_info` 幂等增列），并创建 `environment_repair_logs` 及其 `(correlation_id, sequence)`、`timestamp` 索引。

`EnvironmentInventoryService` 在 Runtime 扫描后替换持久化快照：本次未发现的旧 Runtime 标记为 Missing，不直接删除；`GetAllAsync` 是"在场清单"读取，只返回非 Missing 记录，并把 `RuntimeStatus.Error` 无损推导回 `DetectionStatus.Broken`（持久化不存检测明细）。`PackageInventoryService` 在 Winget 已安装清单和包变更后写入 Package 快照（`status=0` 历史行同理不返回）。Package 使用 `Id + Provider + Architecture` 作为资产键：解析 `x86`、`x64`、`arm64` 标识后保留真实架构变体，只合并完全重复的 Winget 记录；无法识别时显示 `Unknown`。`ProjectCatalogService` 在 `project init`、`env check`、`project open` 与桌面端对应操作中登记项目，保存完整 YAML 内容及检查通过的 Runtime Binding。项目目录不可达时仅更新为 Missing；用户显式移除才删除项目、配置和绑定。

**清单刷新的新鲜度分层。** 两个清单服务共用同一刷新语义：内存 30 秒合并 → `scan_state` 表（`kind` 主键 + `scanned_at`/`duration_ms`/`fingerprint`）驱动的持久化快照 TTL 门控（`NorniaSettings.PersistedScanTtlSeconds`）→ 全量扫描。Runtime 侧 TTL 门控还需 `EnvironmentFingerprintProvider` 的指纹一致：该 provider 零进程派生地哈希 PATH、五个命令行工具的候选路径（与 `CommandRuntimeProviderBase` 共享 `WindowsPathLocator`）与 VC++ 注册表版本，指纹无法计算（null）或已变化时 fail-open 回落全扫；Package 侧无廉价探针，仅 TTL 门控。快照与 `scan_state` 都提交成功才视为一次有效扫描，崩溃只会让时间戳偏旧、下次多扫一次（安全方向）。持久化读取抛异常（如首启建表竞态）同样回落全扫。显式动作（「重新扫描/刷新」按钮、CLI `env check`/`env fix`、安装/卸载/升级/修复后的清单重载）一律 `RefreshForcedAsync` 绕过全部门控；页面首激活先 `GetPersistedAsync` 秒出首屏、再门控刷新，并在页头显示「上次扫描：X 前」提示。

Dashboard 从资产库分别统计 Runtime、开发工具、项目和需处理环境数；Projects 页面显示已登记项目，可加载、刷新、检查、打开和移除历史项目。日志通过单写入异步队列写入 SQLite，防止阻塞 WPF UI，并在退出时 flush；启动时清理超过 90 天的 SQLite 操作日志。Serilog 文件日志仍保存 14 天。

## 9. 桌面界面接入

Desktop 是功能模块的组合根，通过依赖注入创建页面 ViewModel。页面不会自行构造 Provider，也不会在 code-behind 中调用业务服务。

界面采用 VS Code Dark Modern 工作台风格，并复用其信息层级：顶部自定义标题栏、左侧 Activity Bar（现代半透明高亮 + 徽标）、带分区标题的 Explorer 风格主侧栏、中间编辑器标签条与面包屑 + 内容区域、底部可拖动 Output Panel（标签顶部强调边框），以及深色状态栏（左右分组、可点击项、悬停反馈）。颜色令牌直接对齐 VS Code 官方 Dark Modern / Light Modern / High Contrast 调色板（`#181818`、`#1F1F1F`、`#2B2B2B`、`#0078D4`、`#F85149` 等，见 `Themes/*.xaml`）；列表、表格、输入框和按钮统一采用紧凑尺寸。窗口控制、拖动和缩放属于纯视图行为，可留在 Window code-behind；业务操作仍全部由命令绑定进入 ViewModel。

Desktop 采用统一的 VS Code 外壳（`MainWindow`）：[48px Activity Bar | 二级左侧栏（共享列，可拖动分隔条调整宽度，页面无侧栏时整列收起）| 主内容区]。活动栏提供五个顶层入口：环境管理、资源管理器、源代码管理、项目管理、设置。每个顶层页通过 `PageViewModel.Sidebar` 虚属性提供自己的二级左侧栏视图（侧栏模板放在侧栏 ContentControl 的 Resources 中，内容模板放在主内容区，避免同类型双模板冲突）；设置页无侧栏，内容占满全宽。活动栏徽标：环境管理=问题数、源代码管理=Git 更改数、项目管理=已登记项目数。

**标题栏（命令中心）。** 标题栏自左至右：Logo + 完整菜单条（文件/编辑/选择/查看/转到/运行/终端/帮助，菜单项经命令平台 `Commands[id]` 绑定并显示 `InputGestureText` 快捷键）、居中「返回/前进 + 工作区命令中心」胶囊（`TitleBarCommandCenterBackgroundBrush` 三主题令牌：深/浅为半透明 tint，高对比为实心面）、右侧布局切换（左边栏/底栏按钮，激活态字形高亮）与窗口控制按钮。窗口控制采用紧凑形态：字形 `IconClose`（12）+ `DpiHelper` 系统按钮盒 32×28（DPI 缩放），不再沿用 46×34 的系统尺寸。

**内存导航历史。** 顶栏「后退/前进」维护内存级导航栈（容量 100，不持久化）：每次页面切换、环境页小节切换或工作区标签切换记录一段 `NavLocation`（活动栏目标 ID + 环境小节 + 标签键 + 编辑器文件路径），同一位置去重不入栈；前进栈在新导航时清空。回放时按「活动栏目标 → 环境页小节 → 工作区标签」顺序应用，已关闭的文件标签按路径重开（文件仍存在时），回放期间屏蔽历史记录避免回放本身再次入栈。

**响应式侧栏语义。** 窄窗口的自动收起只是「窗口太窄」的临时隐藏：此时显式切换（顶栏按钮或 Ctrl+B）意味着用户要重新打开侧栏——清除自动收起标记并显式显示，避免窗口加宽后侧栏意外复活；再切换一次则是显式隐藏，加宽后同样不复活。

资源管理器页（`ExplorerPageViewModel`，顶层「资源管理器」）是统一的项目状态所有者：二级左侧栏为工作区文件树（`WorkspaceViewModel`/`WorkspaceView`），主内容区为共享编辑器（`EditorAreaView`）。`OpenProjectPathAsync` 是打开项目的唯一入口，一次调用同步四处——资源管理器加载文件树、终端切换工作目录、Git 仓库路径与项目登记并刷新；同时把项目工作流上下文（`Projects.ProjectPath`/`ProjectName`）指向同一目录，保证资源管理器「检查环境」与项目页检查的是同一个项目。资源管理器侧边栏顶部保留「项目」分区展示当前项目与环境状态（✓/⚠），打开工作区时根目录默认展开。Dashboard 的 `NavigationTargets.Projects` 任务携带上下文（check / repair / new / repair:project=…）跳转到「项目管理」并转发给项目工作流；项目目录行的「打开」经 `NavigationTargets.Explorer` 路由回资源管理器页。

「项目管理」页（`ProjectsViewModel`）的二级左侧栏是 VS Code 风格的项目目录列表：标题栏有「刷新列表」与「登记项目」，每个项目行显示健康徽标/名称/路径/上次检查时间，行内提供「打开」（在资源管理器中打开，同步资源管理器与终端）、「在编辑器中打开」（外部编辑器命令，见设置）与「移除记录」三个动作；列表下方在无项目时显示空状态。主内容区为环境检查/修复工作流（路径框 + 状态驱动的初始化/检查/预览修复/应用修复按钮 + 结果表格 + 修复计划条）。目录缺失的历史项目保留在列表中，但「打开」按钮因目录不存在而禁用。

「源代码管理」页（`GitViewModel`/`GitView`，顶层「源代码管理」）恢复为 VS Code 式源码管理：二级左侧栏展示仓库状态、暂存/未暂存更改、提交、分支与历史（行内按钮经 `AncestorType=UserControl` 解析到 GitViewModel），主内容区为同一个共享编辑器（diff 标签）。`GitViewModel` 构造器注入 `EditorAreaViewModel`，`DiffOpenRequested` 直接在页内接线到共享编辑器；`StatusRefreshed` 经资源管理器页转发 `Explorer.ApplyGitStatus` 同步树装饰；「在资源管理器中显示」经 `RevealRequested` 路由回资源管理器页并 reveal 文件。侧栏「图表」面板的提交图形泳道（`GitGraphCell`）按分支语义着色：当前分支用 `GraphCurrentBranchBrush` 高亮，本地分支取 `GraphLane*` 六色相、远端分支取 `GraphRemote*` 六色相（按分支名 FNV-1a 哈希稳定取色，不同分支尽量异色），色键逐行携带（`GitGraphRow.LaneColorKeys`），延续中的泳道命中更深层分支 tip 时自该行起切换色键（分段着色——线性历史下本地分支领先跟踪远端也能一眼分辨本地段/远端段）；颜色切换精确落在圆点上：行的上半段取入色（`LaneIncomingColorKeys`，即上一行的出色）、圆点及下半段取出色，相邻两个圆点之间的连线全程单色（线的颜色由端点决定），无分支数据时回退泳道索引循环取色；圆点实心压线（无背景光晕）；几何常量集中在 `GitGraphLayout`（纯函数，可单测），与 `GitView.xaml` 的图形列宽同源。

「源代码管理」页的 VS Code 对齐增强：

- **分支/同步状态**：提交框上方显示当前分支、上游短名与 ↑/↓ 领先落后计数（结构化 `CurrentBranch`/`AheadCount`/`BehindCount`，不再解析摘要字符串）；有待拉取/待推送时同步按钮高亮（`NeedsSync`）。
- **更改列表树状/平铺布局**：`ScmLayout` 列表/树状切换（VS Code 标题栏视图按钮）。树状布局把更改按目录分组为文件夹行 + 缩进文件行（`ScmFolderNode`/`ScmFileNode`），目录折叠态会话级保存；平铺与树状共用同一套多选（Ctrl/Shift）与批量暂存/取消暂存/丢弃命令，未暂存列表另支持把文件行**拖入已暂存分区**执行 `git add`。
- **提交与同步**：提交按钮为拆分菜单——提交 / 提交并推送（提交后直接推送，不要求工作区干净）/ 提交并同步（提交后先快进拉取再推送）；独立命令 获取（`fetch`，不动工作树）/ 拉取 / 推送 / 安全同步（拒绝脏工作区，快进拉取成功后才推送）。认证策略：Nornia 永不接受或存储凭据，push/pull 依赖 Git Credential Manager 读取 Windows 凭据管理器。
- **传入/传出更改**：有上游时，最近提交图以虚线空心节点标出同步范围：「传出的更改 + 当前分支」位于本地 tip；远端独有提交占据独立泳道并先行绘制，「传入的更改 + 上游分支」分界位于远端独有提交之后、共同/本地历史之前，远端泳道在该节点处汇入共同历史。不再将上游线性追加到底部，也不再压缩成顶部计数行。边界行仍可展开并懒加载该范围的**净文件影响**（`git diff --name-status HEAD...upstream` / `upstream...HEAD`——自合并基到上游/本地的净变化，同一文件被多个提交修改只计一次）；提交集合或「上游|ahead|behind」签名变化时重建，静默刷新不清空、不闪烁。
- **贮藏（Stash）**：侧栏「贮藏」分区列出 `git stash list`，支持贮藏全部更改（含未跟踪）、应用并移除（pop）、丢弃（二次确认）。
- **自动刷新**：`GitRepositoryWatcher`（递归 `FileSystemWatcher`，200ms 尾部去抖、UI 线程派发）监听工作树变化静默刷新 SCM 状态（受 `git.autorefresh` 设置控制）。`.git` 目录只放行影响可见状态的路径（`index`、`HEAD`、`packed-refs`、`FETCH_HEAD`、`ORIG_HEAD`、`MERGE_HEAD`、`CHERRY_PICK_HEAD`、`refs\`、`rebase-*`），忽略 object/lock/log 抖动；git 读写操作前后打开 250ms 抑制窗口，`.git\index` 事件在窗口内丢弃（git 自身回写 index stat cache 不会触发刷新循环），而外部的 HEAD/refs 切换仍能立即捕获。监视器错误（缓冲区溢出/目录移除）时停止监听并发一次通知，视图进入降级态，目录恢复后下次刷新重新附着。
- **历史增强**：分页加载（每页 30 条，「加载更多」）、复制变更路径/分支名/提交哈希/提交主题（多选）、复制 git 日志文件路径；选中提交文件在共享编辑器打开提交 diff（`diff:<hash>:<path>` 标签键去重）。

「环境管理」页（`EnvironmentManagementViewModel`）的二级左侧栏是分区列表（概览/运行库/开发工具/软件包/缓存管理），主内容区显示所选分区页——与资源管理器/源代码管理/项目管理共用同一套侧栏机制，实现资源使用统一。

资源管理器与共享编辑器区以 VS Code 工作台的方式整合在一个「编辑器组」内：

- **共享编辑器区**（`EditorAreaViewModel`/`EditorAreaView`）：资源管理器与源代码管理页共用同一单例，资源管理器打开文件 → 只读内容标签（`FilePreviewTab`），源代码管理选中更改/提交文件 → `DiffTab`（行内/分栏布局，容量分层见 13.2，流式加载见 13.5）。diff 标签有明确的「加载中 / 无差异 / 有差异」三态：加载完成后即使为空（二进制、空文件或失败）也会退出「加载 diff 中…」空状态。资源管理器的「查看更改」用 `GitViewModel.RepositoryPath` + `Explorer.GetGitInfo` 构造 `GitDiffRequest` 后同样送入共享编辑器。
- **资源管理器 Git 装饰**：`WorkspaceViewModel.ApplyGitStatus` 把 `GitRepositoryStatus`（暂存/未暂存/未跟踪）映射为 `relativePath → (字母, staged/untracked)`，`WorkspaceNode` 通过共享的 `WorkspaceStatusSource` 在懒加载树的节点上渲染状态字母（M/A/D/? 等，颜色对齐 diff 配色）与「含更改」文件夹圆点；状态来源为 `GitViewModel.StatusRefreshed`（源代码管理页每次刷新后触发），由 `ExplorerPageViewModel` 接线驱动装饰。相对路径统一使用 `/` 分隔以匹配 git 输出。资源管理器行交互符合 VS Code：单击文件夹行切换展开/折叠（由视图层处理，勾选箭头仍可用），单击文件行在共享编辑器区打开展示标签。

| 页面 | 接入能力 |
| --- | --- |
| Dashboard | 持久化 Runtime 数量、开发工具数量、已登记项目数量、环境异常数量 |
| Runtime | 扫描 .NET SDK、.NET Desktop Runtime、Node.js、Python、Java、Visual C++ Redistributable、Windows App Runtime（含损坏态检测）；安装/移除/升级经软件包 Provider，升级按架构对齐，「更新不适用」降级为警告跳过 |
| 开发工具 | 扫描、安装、移除和升级 .NET SDK、Git |
| Packages | 软件包搜索、已安装清单、安装、卸载、升级（默认 Winget，可选 Scoop/Chocolatey 自动选择） |
| 缓存管理 | 扫描 AppData 与用户根目录中的应用/开发工具缓存；不关联软件包，按目录名称推测应用/生态分类并汇总。分类表与缓存明细表通过可拖动水平分隔条布局，单击分类只显示其明细；整格可点击的居中复选框实时决定分类行的清理数量与空间，并支持对当前分类全选/全不选，确认后仅清理勾选内容 |
| Projects | 已登记项目、选择目录、初始化 `Nornia.yaml`、环境检查、修复计划、确认后应用修复（含批次追踪/回滚）、打开、刷新、移除项目记录 |
| Settings | 设置编辑器（搜索/分类/作用域/语言覆盖/重置/冲突处理）+ 键盘快捷方式编辑器，并打开本地资产库和诊断日志目录 |

所有页面共享 `UiLogService`。外部进程的 stdout 和 stderr 经由 `IProgress<ProcessOutput>` 写入底部 Output 面板；页面操作状态和异常也写入同一日志流。缓存扫描与分类不写逐目录、逐候选日志，成功时只写一条含候选数、分类数、空间与耗时的终态摘要，跳过、失败和异常仍保留诊断。导航只切换 Page ViewModel，首次进入页面时触发必要的加载，后续可以通过页面刷新命令显式重新读取。

**底部面板三页签与 Problems。** 底部面板是 VS Code 式三页签结构：输出（完整日志流）/ 问题（Problems）/ 终端（多会话）。Problems 页签是 `UiLogService` 条目的**派生视图**——实时过滤 WARNING/ERROR 条目（不额外存储），支持级别过滤（全部/信息/警告/错误）与文本过滤、单条/全选/复制消息与复制全部；出现新的 ERROR 时面板自动切到 Problems 页签并展开。问题计数驱动：活动栏环境管理徽标、状态栏「N 个问题」与环境健康字形。异常日志经 `UiLogService.WriteException` 结构化落盘：`ExceptionDiagnosticFormatter` 生成人类可读多行诊断（异常类型/原因/内部原因/建议；`EnvironmentRepairException` 追加「失败步骤 n（correlation_id=…）」与逐条失败原因），`details_json`（类型、消息、堆栈、内部异常、修复失败列表）写入 `operation_log.details_json`，Serilog 文件日志同步写入（级别映射 ERROR/WARNING/DEBUG/VERBOSE）。

Desktop 同时配置了按日滚动的 Serilog 文件日志，保留 14 天，位置为 `%LOCALAPPDATA%\Nornia\logs`。应用注册 Dispatcher、AppDomain 和未观察任务异常处理：可恢复的 UI 异常会显示提示并写入 Output，其他异常会写入诊断日志。

Dashboard 同时承担任务中心职责：根据持久化的项目健康状态、软件包更新、缓存候选和最近操作生成按优先级排序的下一步建议，并可携带筛选上下文跳转到专业页面。Desktop 长任务使用统一操作状态，提供取消、关联日志编号、成功/失败结果和恢复建议；运行期间仅禁用当前页面的冲突按钮，导航、表格查看和 Output 面板保持可用。卸载、环境修复和缓存清理必须二次确认；安装与升级保留完整日志，缓存扫描与清理只保留终态摘要和异常诊断。

## 10. 测试策略

当前测试覆盖：

- 多版本 .NET SDK 输出解析。
- Node.js、Python、Git 版本解析和路径解析。
- 命令不存在时返回空检测结果。
- Winget 双语表格解析（golden fixtures：真实中文清单含 spinner/进度条噪声、英文表头、无可用列、截断名、无结果消息、未知表头回退）、结构化 JSON 契约、截断名回查并发/去重性能回归、无匹配退出码语义与本地化消息。
- `Nornia.yaml` 初始化与读取。
- 环境检查的 PASS、WARNING、FAIL。
- 前缀与比较器版本范围、预发布拒绝、无效约束和 Runtime 发行后缀。
- 修复计划、仅手动项和显式执行后库存刷新。
- Provider 失败时仍可返回其他 Provider 的扫描结果。
- UTF-8 外部进程输出、取消外部进程、Winget 查询进度转发。
- SQLite 重复初始化、旧库升级、Runtime/Package 快照状态、日志过期清理，以及项目配置/绑定、路径缺失保留和显式移除。
- 统一外壳与资源管理器整合：打开项目同步四处（资源管理器/终端/Git 仓库/项目登记）、共享编辑器标签（文件预览与 diff）、资源管理器 Git 状态字母与“含更改”文件夹圆点（`ApplyGitStatus` 由 `GitViewModel.StatusRefreshed` 驱动）。
- 工作台标签条（`WorkbenchViewModelTests`/`WorkbenchMainViewModelTests`/`WorkbenchTabCapabilitiesTests`）：页面/文档混合标签、重复打开激活不复制、关闭族（其他/右侧/未固定）、固定标签豁免、预览转正、拖拽重排回写共享编辑器顺序、标签键去重。
- 代码编辑器（`CodeWorkbenchServiceTests`/`CodeFoldingAndViewStateTests`/`CodeOutlineAndOptionsTests`/`CodeFindNavigationTests`/`LanguagePresentationAndDiffTests`）：TextMate 分词与语言目录、快照版本门控与缓存容量、折叠区间（括号/JSON 数组/XML/缩进/Markdown）与折叠态恢复、大纲与选项、搜索匹配/计数器/跳转、行内差异构建的边界与回退、迷你地图几何映射、阅读态（滚动/光标/折叠/搜索词）往返。
- 设置系统（`tests/Nornia.Settings.Tests` + `SettingsCenterTests`）：JSONC 解析/补丁保留注释与 BOM/换行、作用域解析优先级与非法作用域诊断、提交（验证失败/文件错误/乐观修订冲突）、重置、外部变更观察与会话键过滤、旧版 `settings.json` 一次性迁移与备份。
- 命令平台与快捷键（`MainViewModelTests`/`QuickInputViewModelTests` 等）：命令注册/When 表达式解析、快捷键归一化/和弦/冲突检测/三方合并保存、命令面板与快速打开的模糊过滤与文件枚举上限。
- Git（`GitViewModelTests`/`GitRepositoryWatcherTests`）：树状/平铺布局与多选批量命令、传入/传出净文件影响、贮藏 pop/drop、拉取/推送策略、日志分页与复制、监视器路径过滤/抑制窗口/去抖/错误降级。
- 修复与回滚（`TrackedEnvironmentRepairExecutorTests`）：correlation_id 批次日志、逐步失败不中断、取消中断与汇总、回滚计划逆序与手动策略提示。
- 软件包加固（`WingetProviderHardeningTests`/`RuntimePackageResolverTests`）：`0x8A150016` 多实例按版本逐个卸载回退、无匹配卸载提示、spinner 噪声、Windows App Runtime 包 Id 推导。
- 设计系统守卫（`DesignSystemResourceTests`）：三主题令牌集合一致、所有 `DynamicResource` 键可解析到令牌、视图无硬编码字号字面量（新增字号令牌须在 App.xaml/`UiFontService`/测试清单三处同步）、菜单与下拉弹出层无白底、文件图标透明底 + 等宽槽位、语法高亮调色板主题化与对比度、标题栏命令中心令牌三主题齐备与窗口控制紧凑字号。

涉及安装、卸载的测试只验证命令参数，不真实修改开发机。发布前应在隔离 Windows VM 中增加 Winget 集成测试；集成测试断言语言无关（先探测表头语言再断言），并记录探针实测的无匹配退出码与结构化输出支持，供兼容矩阵积累。

## 11. 后续扩展

建议后续按以下顺序推进：

1. 扩展专用 Runtime Provider（如 Go、Rust 工具链）与更多软件包渠道。
2. 以现有 Core 接口为能力边界实现 MCP Server。

源码阅读侧的全局搜索只作为只读项目观察能力维护：搜索当前工作区、增量展示结果、固定上限并支持取消。暂不扩展为跨文件替换、全文索引、搜索编辑器或完整代码编辑器。

（Scoop、Chocolatey Provider 与 `PackageProviderSelector` 自动选择已落地，见 5.1。）

## 12. 外观与交互增强（Theme & Terminal Phase 2）

### 12.1 语法高亮与实时换肤

- 三主题定义共享的 `CodeToken*` 语法高亮调色板令牌（对齐 VS Code Dark Modern / Light+ / High Contrast 语义）；`CodeTokenColorizer` 把 TextMate token snapshot 按语义类别映射到调色板，代码预览与 Diff 直接共用该路径。`ThemeHighlightingColorizer` 仅把 AvalonEdit 内置定义作为无 token 时的安全回退（每次 `SimpleHighlightingBrush` 新建，换肤时可重新着色），未映射类别保留内置色。
- `ThemeService.Apply` 广播 `ThemeEvents.ThemeChanged`；`CodeDocumentView`/`DiffDocumentView`/`GitGraphCell` 等代码类视图订阅后**即时重应用**（编辑器画刷统一收口到 `ApplyTheme`/`ApplyEditorTheme`，新建画刷避免 Frozen 复用），不再需要重启才完整换肤。
- 守护：三主题调色板令牌一致 + 与编辑区底色 WCAG 对比度 ≥3.0 + “Brush 解析点收敛”文本断言。

### 12.2 右键菜单与布局

- 右键菜单补齐：文件树（打开/查看更改/复制路径/相对路径/系统资源管理器显示/展开折叠全部/刷新）、终端内容区（选区复制/全选/粘贴/清屏/复制全部）、代码/diff 内容区（复制/全选）；菜单命令走各页 ViewModel，DataContext 经 `DataContextBridge` 进弹出层。
- 菜单与下拉的深色模式主题化：WPF 默认模板把弹出层钉在系统调色板（多数系统呈白色）上，App.xaml 为 `ContextMenu`/`MenuItem`（TopLevelHeader/TopLevelItem/SubmenuHeader/SubmenuItem 四角色，经 `TemplateBinding Background` 由各自样式驱动高亮底，标题栏样式外观不受影响）/`Separator`/非可编辑 `ComboBox`/`ComboBoxItem` 补齐显式 ControlTemplate：弹出层、图标栏与下拉背景统一走 `MenuBackgroundBrush` 令牌，边框走 `StrongBorderBrush` + `RadiusMd`，快捷键与下拉箭头走 `MutedTextBrush`，禁用态沿用既有触发器，焦点环保留 `AppFocusRing`；不新增主题令牌。守卫测试 `MenuAndDropdownStyles_ThemedWithoutWhiteSurfaces` 文本断言相关样式块包含模板与主题令牌、且无系统调色板硬编码键。
- 布局状态持久化（现为 `ApplicationStateStore`/`state.json` 事务式字段操作，见 14.3）：窗口边界（防离屏钳制）、侧栏宽、底栏高、SCM 分栏高、`LastWorkspace`、按工作区最近标签与阅读态；拖拽分栏与窗口移动缩放防抖写回，启动恢复；设置页提供「恢复布局」一键重置。

### 12.3 强调色

- `ThemeFactory` 以覆盖字典方式叠加强调族画刷（按钮/焦点/标签顶边/活动徽标/选中，不动正文与表面色限制对比度风险），`AccentPreset` 跟随主题/青色/鸢尾紫/自定义 `#RRGGBB`；设置页即时预览、保存持久化。

### 12.4 交互式终端（ConPTY）

- `Native/ConPty`（P/Invoke `CreatePseudoConsole` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`，STARTUPINFOEX 以纯 blittable 原生布局手动传递，附带 `CREATE_NO_WINDOW` + `STARTF_USESHOWWINDOW/SW_HIDE` 保证任何失败路径都不会弹出新控制台窗口）启动 shell 于伪终端；`TerminalScreen`（字符单元网格 + 滚动回滚环形 + 备用缓冲区）与 `AnsiParser`（SGR 16/256/真彩色、光标/擦除/滚动区、Alt 屏 1049、持久解析器跨块保留 SGR 与半截 CSI 序列）为纯模型；`TerminalSurfaceControl` 负责渲染（每行 FormattedText 彩色 Runs，渲染经同帧合并的 `RequestRender` 调度，小增量即时、批量输出按帧率天然合并）、键盘直达（Enter/Ctrl+C 选区复制或 SIGINT/方向键/粘贴）、拖拽选区、滚轮回滚与右键菜单。
- 16 基色走主题 `Terminal*` 令牌（三主题各一套），默认前景/背景/光标同理；`ThemeEvents` 即时换肤。
- **可用性判定与回退**：个别受限环境（无完整 ConHost/ConDrv 的虚拟化/服务会话）里 ConPTY 会出现「启动成功但零输出」——子进程未挂上伪控制台却逃逸到父控制台。`TerminalService` 对每次 ConPTY 会话执行首字节判定（2 秒窗口）：判定失败即丢弃该会话并回退到带屏的重定向模式（`StartupWarning` 标明「回退」），保证嵌入式外观兜底、不弹出新窗口也不留下僵死外壳；正常 Windows 桌面环境判定通过即为完整交互式终端（实时回显、光标由外壳驱动、增量输出）。
- 渲染侧无固定节流延迟：`TerminalSurfaceControl.RequestRender` 以同帧去重调度 `InvalidateVisual`，打字/提示符重绘即时，批量输出受 WPF 帧率限制自动合并；会话侧栏只按会话状态刷新，不再为每次输出块全量重建文本。

### 12.5 文件类型图标（语言缩写 Monogram）

- 模型：`CodeFileType` 携带 `IconMonogram`（VS Code 风格语言缩写，如 `C#`/`JS`/`PY`/`{}`/`HTML`，PlainText 默认 `TXT`）、`IconColorToken`（既有 `FileTypeIcon*Brush` 八色相之一）与 `IconGlyph`（仅保留为图片等无徽标场景的回退字形）；`CodeFileTypeRegistry` 覆盖约 67 种扩展名并按语言族着色：C#/VB/F# 紫、C/C++ 青、Java/Kotlin/Rust/Ruby 红、Python 绿、JS/TS/CSS/PowerShell/BAT 蓝、JSON/YAML/SQL/INI/TOML 黄、XML/HTML/PHP/项目文件橙、MD/TXT/LOG/SH/SLN 灰；未知扩展名回退 PlainText。
- 渲染形态为**透明底 + 语言色相文字**：缩写用 `MonoFontFamily` 粗体、字号走 `IconBadge`（9）令牌；文本型图标统一固定等宽槽位 `Width="24"` + `TextAlignment="Center"`（覆盖最长 4 字符缩写），保证同列表内图标等宽、文件名纵向对齐；目录节点保留文件夹字形、无 `FileType` 的 diff 标签回退字形，且与缩写共用同一等宽槽位。颜色经转换器解析：`PathToIconMonogramConverter`/`PathToIconBrushConverter`（路径 → 缩写/色相画刷）与 `FileTypeToBrushConverter`（`CodeFileType` → 画刷，无 Application 宿主时回退透明），均在 App.xaml 注册；不新增主题令牌，色相仅用于文字前景，禁止回退到 `Background` 底色。
- 消费点：资源管理器树（`WorkspaceView`）、Git 变更/暂存/提交文件行（`GitView`）、预览面包屑（`FilePreviewView`）与编辑器标签（`WorkbenchView`）外观一致。守卫测试 `FileIcons_RenderThemedMonogramBadges` 断言令牌字号、等宽槽位、透明底（无语言色相 `Background`）与转换器在位。

## 13. 工作台标签条与代码编辑器

### 13.1 统一标签条（Workbench）

`WorkbenchViewModel`/`WorkbenchView` 把主内容区升级为 VS Code 式统一标签条，混合两类标签：

- **内容页标签**（`PageWorkbenchTab`）：环境管理 / 项目管理 / 设置，实例预建、首次导航才加入条带，重复导航激活不复制；
- **文档标签**（`EditorWorkbenchTab`）：从共享编辑器区（`EditorAreaViewModel`）投影的文件预览与 diff 标签，双向同步（编辑器开/关/选中 ⇄ 条带增删/选中，`_suppressEditorSync` 防重入）。

视图页（资源管理器 / 源代码管理）**永不产生页面标签**——它们的主内容就是共享编辑器；视图页下若选中了页面标签会规整为空（`ClearNonDocumentSelection`），活动文档保持选中。标签身份 `TabKey` 去重（`page:标题` / `editor:<文件标签键>`），文件与其 diff 是不同标签。

标签条能力（对齐 VS Code）：关闭（Ctrl+W，右侧邻位优先回退左侧）、关闭全部、关闭其他、关闭右侧、关闭未修改；**固定**（pin，会话级，豁免后三类批量关闭）；**预览标签**（斜体标题，单击下一文件即被替换，双击/右键「转正」转常驻）；**拖拽重排**（条带顺序回写共享编辑器集合，后续打开保持一致）；Ctrl+PageUp/PageDown 环绕切换；右键菜单（关闭族/固定/转正/复制标题/复制路径）；全部关闭后显示空状态。

### 13.2 只读容量分层

只读源码与 diff 共用 `ReadOnlyContentCapacity` 容量策略（无 WPF/Git 依赖，视图分配前即可单测）：

| 层级 | 源码 | Diff | 行为 |
| --- | --- | --- | --- |
| Full | ≤8 MB | ≤100,000 行 | 完整加载 + 完整语言分析 |
| Windowed | ≤32 MB | ≤500,000 行 | 窗口化加载：只读当前窗口，可「加载上一窗/下一窗」滚动扩展 |
| Summary | 超限 | 超限 | 摘要降级，标注「窗口分析」 |

### 13.3 语言流水线（TextMate）

`CodePresentationService` 是只读语言流水线，产出不可变 `CodePresentationSnapshot`（Token 列表 + 大纲 + 折叠区间 + 行密度）：

- **分词**：`TextMateLanguageTokenizer` 用 TextMateSharp 按语言逐行分词（每行 40ms 时间预算——长行按 `tokenizationSupportWithLineLimit` 语义压缩预算，超时丢弃该行并把状态回退到行首检查点、不污染后续行；每行 4096 token 上限、256 行状态检查点），**保留每个 token 的原始 scope 栈**（含 Plain 类别，主题粗/斜体可作用其上），scope 同时映射到语义类别 `CodeTokenKind`（注释/字符串/数字/关键字/类型/函数/属性/标签/链接）驱动迷你地图；C# 关键字表兜底 TextMate 未覆盖的词元。原生 Onigwrap 缺失或单语言语法不可用时**按文档回退纯文本**，不影响启动与其他标签。
- **语言目录**：`BuiltInGrammarCatalog` 是 TextMate scope 的唯一事实源，覆盖 30 种语言（csharp/vb/xml/xaml/json/jsonc/js/ts/jsx/tsx/html/css/python/yaml/markdown/powershell/c/cpp/java/sql/fsharp/scss/less/shell/bat/ini/go/rust/php）；`GrammarResource`（`TextMateSharp.Grammars@2.0.4:<id>`）同时是打包/许可校验的稳定清单身份；未知语言刻意回退纯文本。
- **缓存**：64 MB 加权 LRU（权重 ≈ token/大纲/折叠/密度规模），键 = 规范化路径 + 文件长度 + 修改时间 + 编码 + 窗口起始行 + 语言配置版本；标签切换不重复昂贵解析。
- **版本门控**：快照携带 `DocumentVersion`，视图只接受匹配当前文档版本的快照，防止过期分析覆盖新文档。
- **渲染**：`TokenTheme` 以 vscode-main 的 last-match-wins 语义把 scope 栈解析为语义角色 + 字体样式（注释斜体、markup.bold 加粗、markup.underline.link 下划线等）；共享的 `CodeTokenColorizer`（AvalonEdit 前景层）按角色查 `CodeToken*` 调色板并套用 `Typeface`/`TextDecorations`，不改 AvalonEdit 全局定义；代码预览与 Diff 直接使用同一套 token 规则和调色板，`ThemeHighlightingColorizer` 只负责无 token 时的内置定义回退；换肤即时重着色（绘制时解析刷子 + 清样式缓存）。行密度数组同时驱动迷你地图缩略。

### 13.4 编辑器表面

`CodeDocumentView`（AvalonEdit 封装）提供：

- **Ctrl+滚轮缩放**：代码区独占，钳制范围，字号写回设置（显示字号 = `editor.fontSize` 作者字号 × 界面缩放倍率，随分辨率/`nornia.appearance.uiScale` 缩放；持久化只写作者字号）；
- **折叠**：`CodeFoldingStrategy` 按语言推导可折叠区间（C#/C 族括号、JSON 含数组、XML/XAML 多行元素、缩进语言、Markdown 标题段；缩进/括号语言合并 `#region`/`#endregion` 显式区域，总区域 5000 上限），纯文本逻辑可单测；`FoldingRegions`（vscode-main `foldingRanges` 同构：parent/level/命中查询）承担区域几何与层级；**折叠栏** `FoldGutterMargin` 是行号与内容之间的 chevron 列——展开 ▾/折叠 ▸、悬停高亮、点击 chevron 切换、点击被折叠内容行展开最近外层（对齐 vscode foldingDecorations），折叠段首行整行半透明背景（`CodeFoldBackgroundBrush`）+ AvalonEdit "…" 占位；`editor.folding` 开关折叠栏与折叠；折叠态随阅读态捕获/恢复；
- **大纲**：按 `OutlineKind` 解析符号（类/方法/成员等），大纲面板 + 过滤框，符号导航居中定位；
- **Markdown 渲染**：`.md` / `.markdown` 文件在完整容量层级默认显示 WPF 原生 Markdown 预览，源码按钮切回 AvalonEdit；Markdig AST 映射到 FlowDocument，支持标题、列表、引用、表格、任务列表、脚注、代码块、本地相对图片、链接和安全 HTML 白名单。数学扩展支持 `$...$` / `$$...$$` 与 `\(...\)` / `\[...\]`，由 WpfMath 绘制常用 TeX；单公式失败时保留原文，图表/脚本/远程媒体安全降级，不执行外部内容。窗口化或摘要大文件不构建完整 AST，自动保持源码模式；预览/源码模式与阅读位置按标签状态恢复。
- **迷你地图**：全文档缩略（`MinimapLayout` 纯几何：每行 ≥1px 钳制，大文档在条带内滚动），行密度着色，视口框可点击/拖拽导航；默认关闭（`editor.minimap.enabled`）；
- **查找**：内容内搜索（匹配数上限保护），全部匹配 + 当前匹配双层背景标记，「3 / 40」计数器，上/下一个匹配，大小写/全词/正则/所选范围（非法正则红字报错，详见 §16.3），**转到行**（`行` 或 `行:列`，越界钳制、居中 + 瞬态整行强调）；
- **行号栏**：VS Code 式——光标行行号着色；缩进参考线（每 4 列一竖线，主题化）；
- **阅读态**：滚动/光标/折叠/搜索词在预览失去编辑器时捕获为 `EditorViewState`，按工作区持久化到 `state.json`，重开标签恢复。

### 13.5 Diff 呈现

`DiffPresentation`/`DiffContentSource` 在 git 行级差异之上做审查友好投影：

- **折叠未修改区域**：长未修改上下文折叠为「… N 行未修改 …」可展开段（`diffEditor.hideUnchangedRegions.enabled`）；
- **字符级差异**：`IntralineDiffBuilder` 在 Git 提供的更改对内运行有界 Myers 字符 diff（≤16K 字符、编辑距离 ≤512、50ms 时间预算，越界回退整行高亮），绝不改动 hunk 上下文与行号权威；
- **概览尺**：右侧细 gutter 绘制变更分布与当前光标行/查找匹配；
- **更改导航**：连续增删块计数，「上一个/下一个更改」（Alt+F5 / Shift+Alt+F5）居中定位并显示「n / m」；
- **摘要与状态**：顶栏「+N −M」增删统计、diff 来源标签（暂存/未暂存/未跟踪/提交 short-hash）、标签状态标记（加载中/无差异/有差异三态）；并排模式同步滚动；
- **流式加载**：`IGitService.StreamDiffAsync`/`StreamCommitFileDiffAsync` 经 `IProcessRunner.StreamLinesAsync`（无界保留输出的逐行流，500K 行硬上限，终止事件携带退出码与限流标记）增量解析（`GitDiffStreamParser`），未跟踪文件直接按「全新增」流式输出（4KB 探针 NUL 检测二进制，UTF-8 严格解码失败回退 54936）。

### 13.6 进程流式执行

`IProcessRunner.StreamLinesAsync` 是 `RunAsync` 的流式补充：有界通道（512）双泵（stdout/stderr 并行 ReadLine），超限终止进程树并标记 `OutputLimitReached`；接口默认实现（兼容体）从 `RunAsync` 结果回放，既有 Fake Runner 源码安全。Git diff 流式解析是当前唯一生产消费方。

## 14. 设置系统（Settings v2）

### 14.1 文件布局

| 文件 | 作用 | 性质 |
| --- | --- | --- |
| `%LOCALAPPDATA%\Nornia\settings.jsonc` | 用户设置（JSONC，注释保留） | 用户级 |
| `<工作区>\.vscode\settings.json` | 工作区设置 | 仓库级，受安全规则约束 |
| `%LOCALAPPDATA%\Nornia\keybindings.json` | 用户快捷键 | 见 15.2 |
| `%LOCALAPPDATA%\Nornia\state.json` | 机器状态（布局/最近标签/阅读态） | 不可共享，不进入设置解析 |

### 14.2 设置目录与安全规则

`BuiltInSettingsCatalog` 定义约 30 个内置设置（`SettingKey<T>` 强类型键 + 显示元数据 + 校验器），分类：工作台（预览标签/标签上限）、编辑器（字号/字体/换行/空白符/迷你地图/行号/缩进参考线/折叠）、Diff（并排/隐藏未修改/字符级差异/概览尺/同步滚动）、终端（字号/字体/默认 Shell/自定义 Shell/会话侧栏）、工作区与源代码管理（Git 自动刷新/排除文件/树状线/紧凑文件夹）、常用（外部编辑器命令/恢复上次工作区）、外观（主题/强调色/界面缩放）、布局（面板初始展开/侧栏与面板默认尺寸）。阅读器（`editor.fontFamily`）与终端（`terminal.integrated.fontFamily`）的字体是 `SettingEditorKind.Font` 下拉框：选项即 `FontCatalog.SupportedMonoFamilies` 支持列表（跨机器可移植），每项以自身字形渲染并标注本机未安装项。

作用域（`SettingScope`）：User / Workspace / Language / MachineState。解析优先级：默认 ← 用户 ← 工作区 ← 用户语言 ← 工作区语言（工作区语言要求该设置同时允许 Workspace 与 Language）。**安全规则**：仓库（工作区）配置不能启动外部进程——`nornia.general.externalEditor` 与 `nornia.terminal.customShells` 仅允许 User 作用域；工作区文件中出现用户级专属设置会被忽略并产生诊断（原文保留），不支持语言覆盖的设置同样忽略并诊断。

### 14.3 服务结构

- `JsoncSettingsDocument`：JSONC 读写，补丁保留注释、BOM、换行风格与其他键的格式；修订号（revision）标识文件代际。
- `SettingsDocumentStore`：读（无效文件回退上一有效根）与补丁（**同一临界区内读取-计算-原子写-读回**，防止并发提交丢写；原子写 = 临时文件 + `File.Replace`，IOException 指数退避重试 3 次；`WriteThrough` 落盘）。
- `SettingsResolver`：按目录把用户/工作区（含语言覆盖）文档解析为 `UntypedSettingValue`（默认值 + 五层可选值 + 有效值 + 来源），逐值类型校验，失败值产生诊断而不中断。
- `ScopedSettingsService`：快照（按 工作区|语言 上下文键缓存，有效值变化才递增修订号）、提交（作用域校验 → 值校验 → 乐观修订比对，不一致时逐键 `JsonNode.DeepEquals` 冲突检测 → 原子补丁 → 变更集发布）、重置（按作用域/语言删键）、会话（`OpenSessionAsync` 订阅键集合，`Changed` 事件只推送订阅键，`RefreshAsync` 全量）。
- `SettingsChangeCoordinator`：`FileSystemWatcher`（目录级、含子目录）+ 200ms 去抖；`AcknowledgeWrite`/`ConsumeSelfWrite` 用修订号识别并吞掉自写回环；外部修改触发受影响上下文重新解析，有效值变化才发布 `SettingsChangeSet`。
- `ApplicationStateStore`（`state.json`，SchemaVersion 1）：窗口边界（防离屏钳制）、侧栏宽、底栏高、SCM 分栏高、`LastWorkspace`、`ActiveSidebar`、`PanelVisible`、**按工作区**的 `RecentTabs`/`ReadingStates`/`ActiveEditor`；事务式字段操作（`ApplicationStateOperation`/`Field`），原子写，`ResetLayoutAsync` 一键恢复布局默认。
- `LegacySettingsMigrator`：一次性只读迁移（`SettingsMigrationVersion=3` 门控）——旧 `settings.json`（v2 DTO）字段映射到新设置（编辑器命令/主题/强调色/字号/换行/空白/迷你地图/行号/缩进/折叠/Diff 全套/终端/自动刷新/树状线/布局默认值…），窗口/侧栏/面板/SCM 分栏/`LastWorkspace` 迁入 `state.json`；旧文件备份为 `settings.v2.<时间戳>.backup.json` 后保留不删；迁移版本写入 `state.json`，重复启动直接跳过。

### 14.4 设置编辑器（Settings 页）

`SettingsEditorViewModel` 取代旧表单式设置页：

- **搜索**（标题/Id/描述/分类/关键词，大小写不敏感）+ 分类导航；虚拟化的设置卡片列表；
- **作用域切换**（用户/工作区，工作区需已打开项目）、**语言覆盖**下拉（`[languageId]` 覆盖层）；
- 卡片展示：来源徽标（默认值/用户/工作区覆盖/语言覆盖）、**来源链**（默认 → 用户 → 工作区 → 用户语言 → 工作区语言）、当前作用域值与有效值、按编辑类型渲染（开关/枚举/字体下拉/文本/数字/JSON 结构——字体下拉逐项用自身字形预览并标注未安装），内联校验错误；
- 每键操作：重置（按作用域删键）、复制 Id、**分类恢复**、**布局恢复**（清 `state.json` 布局字段）；
- **冲突处理**：同键并发修改时顶部冲突条 + 逐项「重新加载 / 使用磁盘值 / 保留本地 / 复制差异」；
- 更改按键即提交（事务自动保存，保存状态提示）。

## 15. 命令平台与快捷键

### 15.1 命令注册与 When 上下文

`CommandDescriptor`（Id/标题/分类/字形/默认快捷键/执行器/CanExecute/Enablement/菜单位置）注册进 `CommandRegistry`（不可变字典，`ExecuteAsync` 先查 CanExecute 再查 When 上下文）。`CommandAccessor`（XAML `Commands[id]`）把注册表命令适配为 `ICommand`（`CommandManager.RequerySuggested` 驱动）。

`ContextKeyService` 维护上下文键（`terminalFocus`/`inputFocus`/`editorTextFocus` 等，焦点变化时更新），`WhenExpression` 是 VS Code 风格 when 子集的解释器：`!`、`&&`、`||`、括号、`==`/`!=`（忽略大小写）、`=~`（正则，50ms 超时）；词法/语法错误按表达式缓存为失效（不再重试），防止恶意 keybinding 卡死 UI。

### 15.2 快捷键服务

`KeybindingService`（`%LOCALAPPDATA%\Nornia\keybindings.json`）：

- **合并**：命令默认快捷键 + 用户绑定；`-` 前缀命令移除同键同 when 的默认项；
- **归一化**：修饰键固定序 `ctrl+shift+alt+win`，`control→ctrl`、`pageup`/`pagedown`、`oemtilde→`` ` 等别名；
- **和弦**（chord）：如 `ctrl+k ctrl+s`，首击后 1 秒内等待次击，Esc 取消，待命中状态回显（`PendingChord`）；
- **冲突与诊断**：同键同 when 的绑定对产生冲突列表；when 表达式解析失败产生诊断（编辑器可见）；
- **保存三方合并**：基线（上次加载）/磁盘（外部修改）/本地（会话内修改）逐命令比对——外部与本地同时修改同一命令时中止并提示重新加载，绝不静默覆盖；原子写 + 自写修订号识别；
- **文件监视**：经 `SettingsChangeCoordinator` 热重载（JSON 错误时保留旧绑定并显示诊断）；
- **分发**：`MainWindow.PreviewKeyDown` → 归一化击键 → `DispatchAsync`（when 过滤、精确匹配优先、前缀匹配进入和弦待定），菜单 `InputGestureText` 与命令面板的「主绑定」显示均取 `GetPrimaryBinding`。

### 15.3 命令清单与 QuickInput

`DesktopCommandBootstrapper` 注册约 30 个命令（弱引用 MainViewModel，描述符不持有 UI 状态）：命令面板（`workbench.action.showCommands`，Ctrl+Shift+P）、快速打开（`workbench.action.quickOpen`，Ctrl+P）、打开设置（Ctrl+,）、键盘快捷方式（Ctrl+K Ctrl+S）、打开项目目录、主题切换×3、关于、切换侧栏（Ctrl+B）、切换面板（Ctrl+J）、关闭活动编辑器（Ctrl+W，terminalFocus 时路由到终端会话）、上一个/下一个编辑器（Ctrl+PgUp/PgDn）、终端新建/切换/聚焦/清除/关闭、复制所选（Ctrl+C，非输入焦点）、全选（Ctrl+A）、输出清除、显示输出/问题/终端面板、刷新活动视图（F5）、活动栏 1-8 定位（Ctrl+1..8）、Diff 上/下一个更改（Alt+F5 / Shift+Alt+F5，`activeEditor == diff`）。

`QuickInputViewModel` 是纯逻辑 QuickInput（无 WPF 依赖，可单测）：顶居中输入框 + 可过滤列表；过滤 = 子串（标题/详情）或标题模糊子序列（VS Code 风格）；首项预选使 Enter 立即确认。命令面板行 = 注册表命令（标题 · 分类 · Id · 主绑定），快速打开行 = 工作区文件枚举（上限 5000，跳过 `.git`/`bin`/`obj`/`node_modules`/`.vs`/`packages` 与不可读目录），选中即打开预览标签。

## 16. 源代码阅读标签页与 Diff 标签页（VS Code 阅读体验镜像）

两条阅读标签页以 vscode-main 为蓝本实现 VS Code 的**只读阅读体验**（明确不模仿编辑能力：无光标编辑/撤销/保存/智能感知；阅读器 IsReadOnly + 容量分层见 13.2）。

### 16.1 标签条（EditorGroupView）

- **单区标签条**：全部标签在同一可滚动条带；固定（pinned）标签置左（`TogglePinCommand` 重排固定块在前，Pin 字形标记），溢出时等宽收缩省略号（`UpdateTabSizing` 检测内部 ScrollViewer 溢出）；中键关闭；`SelectionMode=Extended` 支持多选批量关闭（右键"关闭所选"）。
- **Ctrl+Tab / Ctrl+Shift+Tab 最近编辑器切换器**：`EditorGroupsViewModel` 维护工作台级编辑器 MRU（组选中/集合变化时 `TouchEditorMru`/`PruneEditorMru`），`MainViewModel.ShowEditorMruSwitcher` 经 QuickInput 列出最近标签，回车激活（`workbench.action.openNext/PreviousRecentlyUsedEditor`，Ctrl+Tab / Ctrl+Shift+Tab）。
- 标签右键菜单：拆分（右/下）、关闭所选/其他/全部、固定/取消固定、转正、复制路径、在资源管理器中显示（`explorer /select`）、用外部编辑器打开。

### 16.2 面包屑（Breadcrumb）

- 路径分段链（驱动器/目录/文件，过深以"…"折叠）：`EditorTabItem.BreadcrumbSegments`（`BuildBreadcrumbSegments` 逐段累计完整路径，纯函数可单测）；段点击在资源管理器定位（`RevealBreadcrumbCommand` → `explorer /select`）；Ctrl+Shift+. 聚焦面包屑，←/→ 跨段。
- 符号段：光标行 → 大纲最近祖先符号（`CodeDocumentView.CaretLineChanged` → `FilePreviewTab.UpdateCaretLine`），斜体显示，点击打开 QuickInput 符号选择器；Diff 标签无符号段。

### 16.3 查找部件（FindWidget）

编辑器区右上角**浮动**查找浮层（`ToolTipBackgroundBrush`+`ShadowPopupEffect`+`RadiusMd`，替代全宽栏）：输入框 + "第 N / 共 M"计数 + 上一/下一/全部高亮开关/关闭；大小写/全词/正则（非法正则显示红字错误并返回无匹配，修正后自动消失）；**find-in-selection**（`SelectionRangeProvider` 由视图注入选区范围，VM 在选区窗口内搜索并把行号重挂回全文；"所选范围"复选框按选区存在与否显隐）；`ShowAllHighlights` 关闭时隐藏全文装饰（导航计数保留）。键盘：Enter/Shift+Enter 下/上一个、F3/Shift+F3、Esc 关闭；查找栏已打开时 Ctrl+F 把焦点交回输入框；正文有选区时按 Ctrl+F 用选中文本覆盖更新搜索框（无论搜索框是否为空）。**窗口化大文件**（>8 MB）：选项同样作用于流式磁盘搜索（`FileReadOnlyDocumentSource.SearchAsync` 按行读取并支持大小写/全词/正则），上/下一个匹配落在当前窗口外时自动加载其所在窗口，匹配子集重挂为窗口内行号+偏移（全文计数保留），快速连续切换选项以原子版本令牌 + 取消令牌门控，过期搜索结果不落地。惰性加载完成前输入的查询在内容就绪后重算。

### 16.4 代码表面（CodeDocumentView / AvalonEdit）

- **Sticky scroll**：滚动时把覆盖视口顶端的折叠段头钉在顶部（`CodeFoldSection` 数据源，≤3 级，点击跳段首；`editor.action.toggleStickyScroll`，`editor.stickyScroll.enabled` 设置）。
- **括号匹配 + 当前词高亮**（`ReadingHighlightRenderer`，段级精确背景）：括号对用当前匹配色、词全文出现用搜索匹配色；容量上限 500 处保护。
- **小地图**：块模式 / 字符格模式（`editor.minimap.renderCharacters`）切换与宽度设置（`editor.minimap.width`，60–320）；视口指示、点击/拖拽导航、搜索/光标标记保留。
- **相对行号**（`editor.lineNumbers=relative`，`CodeLineNumberMargin` 显示与光标的距离）与**垂直标尺**（`editor.rulers`，逗号分隔列号，`RulerRenderer` 全高细线）。
- 阅读选项统一由 `SettingsOptionsMapper.CodeReading` 从设置映射进 `CodeReadingOptions`，新建/恢复预览标签时应用；原有点：折叠、缩进参考线、当前行高亮、overviewRuler（光标/匹配）、搜索导航（F3/Shift+F3/Esc）、容量分层、主题即时换肤（`ThemeEvents`）。

### 16.5 Diff 标签（DiffDocumentView）

- 内联 / 并排双模式（含同步滚动、行号边距、行尾缩略、字符级 intraline、可点击 overviewRuler、hunk 导航 Alt+F5、上下文折叠展开条）；模式默认值 `diffEditor.renderSideBySide` 设置持久化（会话级）。
- **窄窗自动内联**：`diffEditor.useInlineViewWhenSpaceIsLimited`（默认开）——窗口 < 540px 时即使请求并排也以内联显示（仅显示层，不改 DiffMode）。
- **@@ hunk 头吸顶**：内联模式滚动经过 hunk 头后将其钉在顶部，点击跳转（`UpdateDiffSticky`）。
- **改行 modified 着色**：并排构建时对"old=Removed ∧ new=Added"的同行对标记 `IsModified`，用 `DiffModifiedBrush` 整行渲染（三主题同步，`DesignSystemResourceTests` 守卫）。行号右侧原有的变更加粗实线（3px，`EditorGutterAddedBrush`/`EditorGutterDeletedBrush`/`DiffModifiedBarBrush`）已按需求移除，变更识别由整行着色 + 行号区 ± 符号列承担（对应令牌保留在主题中，仍受守卫）。
- diff 右击菜单：复制 / 全选 / 复制全部（统一 Diff）/ 在代码标签页打开文件（`DiffTab.OpenInCodeRequested` → `OpenFileAsync`）；状态条含光标 Ln/Col（聚焦面驱动）。
- **忽略行尾空白**：diffEditor.ignoreTrimWhitespace 设置 + 工具栏切换（DiffTab.ToggleIgnoreWhitespaceCommand），真实按 --ignore-space-at-eol 重算（IGitService/GitService 流式方法均支持，测试桩默认实现忽略该标记）。
- **Diff 内查找**：顶部查找栏（IsFindBarOpen 切换）对统一文本搜索（忽略大小写），全部匹配高亮 + 当前行强调（DiffFindRenderer），Enter/F3 下一条、Shift+F3 上一条、Esc 关闭；并排模式下同步滚动两侧到对应旧/新行号。
- 其余遵循 VS Code `diffEditor` 选项族：错误/警告概览装饰留待文件级诊断数据模型（UiLogEntry 目前无文件/行字段）。

### 16.6 快速打开前缀模式

QuickInput 统一快速打开前缀框架（对齐 quickAccess）：`@` 前缀切换为当前文件符号列表（标题带 `@` 前缀保持模糊过滤），`:` 前缀切换为行号模式（Enter 解析 `42` / `42,16` 复用 `GoToLineCommand`），其余为文件枚举；前缀保留在输入框，关闭自动解绑（`AttachQuickOpenPrefixMode` + `OnQuickInputForPrefixChanged`）。

### 16.7 语言与命令

- 状态栏语言按钮 → QuickInput 语言选择器（`CodeFileTypeRegistry.All` 去重枚举），选中 `SetLanguage` 切换解析/高亮/大纲类型（状态栏 `Ln x, Col y` + 选区"已选择 N 字符 / M 行"）。
- 命令注册（`DesktopCommandBootstrapper`，When=activeEditor 约束）：`editor.foldAll`/`unfoldAll`/`foldLevel 1..6`（Ctrl+K Ctrl+0/J/1..6，`FoldToLevel` 按嵌套深度）、`editor.toggleStickyScroll`、`workbench.action.gotoSymbol`（Ctrl+Shift+O，预置 `@`）、`workbench.action.gotoLine`、最近编辑器切换。
- 新增设置键：`editor.stickyScroll.enabled`、`editor.minimap.renderCharacters`、`editor.minimap.width`、`editor.rulers`、`diffEditor.ignoreTrimWhitespace`、`diffEditor.useInlineViewWhenSpaceIsLimited`（后两个键已入目录，画像层实现待服务层）。
