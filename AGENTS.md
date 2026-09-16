# AGENTS.md

Nornia 的权威项目记忆（Single Source of Truth）。只保存稳定、明确、经人工确认或充分验证的项目事实与规则。
Agent 在工作过程中自动发现的信息（项目历史、任务上下文、临时结论、经验、推测）应首先写入 `.agent/memory/`（动态候选记忆），只有经过代码/文档/测试/多次观察或用户确认、且具有长期价值后，才提升到这里。

## 项目概述

Windows 原生低内存项目观察与开发环境工作台。Desktop 使用 WPF/MVVM、.NET 10（net10.0-windows）、C#，外壳仿 VS Code；不使用 Electron/WebView。
定位为只读工作台：快速打开/阅读、全局搜索、Git 审阅、本机环境管理；不提供编辑、LSP、调试器或扩展生态。CLI（Nornia.CLI）与 Desktop 复用同一组合根。

## 构建与测试

```powershell
dotnet restore Nornia.sln
dotnet build Nornia.sln
dotnet test Nornia.sln
```

- 常规测试排除集成：`dotnet test --filter Category!=Integration`；Winget 集成测试需环境变量 `NORNIA_WINGET_INTEGRATION=1`。
- 基准：`dotnet run -c Release --project tests/Nornia.Benchmarks --filter <Benchmark>`。
- 测试项目：`tests/Nornia.Tests`（Fakes/ 手写替身注入，不依赖真实进程或 WPF 窗口）、`tests/Nornia.Settings.Tests`（设置系统专用）。

## 架构与模块约束

- `Nornia.Core`：仅领域模型与抽象；Runtime、Package、Project、Git、Storage 只依赖 Core。
- `Nornia.Composition`：唯一服务层组合根，`AddNorniaServices()` 供 CLI 与 Desktop 复用；Desktop 另注册设置系统（`Configuration/`）、命令平台（`Commands/`）、编辑器纯逻辑（`Code/`，无 WPF 依赖、可单测）。
- 业务逻辑不得写入 WPF code-behind。
- 文件组织：一个文件 = 一个领域/职责单元；CLI 命令、接口、模型、Converter 按领域合并；大而内聚的类保持单文件。
- 重组只移动文件，不改类型名、不改 namespace；XAML 后置 partial 类必须与 .xaml 同文件（`x:Class` 要求）。

## 关键设计决策

- SQLite 位于 `%LOCALAPPDATA%\Nornia\nornia.db`；schema 演进使用版本化 `IDatabaseMigration` + `MigrationRunner`（事务内按版本顺序执行，记录到 `schema_migrations`）。
- 环境清单“快照优先”：内存 30 秒合并 → 持久化快照 TTL（默认 6 小时）+ 环境指纹门控 → 全量重扫；显式动作（重新扫描/`env check`/`env fix`/安装/卸载/升级）一律 `RefreshForcedAsync` 绕过门控。
- 设置 v2：用户 `%LOCALAPPDATA%\Nornia\settings.jsonc` + 工作区 `<工作区>\.vscode\settings.json`（JSONC 注释保留、原子写、热加载）；`state.json` 存机器状态；旧 `settings.json` 启动时一次性迁移并备份。
- CLI 语言：neutral 英文资源为默认 + `CliMessages.zh-Hans.resx` 卫星资源，`--lang` 覆盖；Desktop 界面固定简体中文。
- Git 集成只读 diff（`IGitService`，git CLI `-C <repo>`）；Git 命令只暴露在 Desktop，CLI 不做 git 套皮；Git 根由工作区运行时向上查找 `.git` 推导。
- 终端走 ConPTY（`TerminalScreen`/`AnsiParser` 纯模型），失败时回退重定向模式。
- 环境修复可追踪（`correlation_id` + `environment_repair_logs`），批次失败生成逆序回滚计划；可自动撤销的步骤自动执行，Winget 降级与黑名单包（VCRedist/.NET SDK Preview/WindowsAppRuntime）标记 Manual 并给出手动命令提示。

## 硬性约束（Windows 安装器与命令行）

- 安装器（`installer/Nornia.iss`）必须注册文件与文件夹两项右键菜单“Open with Nornia”；注册表写 HKCU 以避免管理员权限（`HKCU\Software\Classes\*` 与 `HKCU\Software\Classes\Directory`）。
- 输出文件名格式：`Nornia-{version}-{RID}-Setup.exe`。
- 命令行参数处理在 `App.xaml.cs`：目录参数按项目打开；文件参数只打开该文件，不打开父目录。

## 文档

- `README.md`：功能总览与 CLI 示例。
- `docs/design.md`：详细设计。`docs/development.md`：开发约定（架构/当前状态/语言策略/文件组织/测试）。
