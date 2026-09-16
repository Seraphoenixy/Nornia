# 2026-09-16 开发工具扩展依赖管理（pip / npm / dotnet tool）

## 背景
将开发工具从软件包管理独立后，新增"工具扩展依赖"子面板：选中工具后在其下方列出该生态的扩展（如 Python 的 pip 包），支持查看/安装/卸载/升级/全部升级。仅 Desktop（CLI 不加命令）。计划文件：`.trae/documents/devtool-extension-dependency-management.md`（已批准）。

## 关键设计（已实现并通过测试）
- 不新增持久化：`ToolExtensionInventoryService` 按生态内存 30 秒冷却缓存（`NorniaSettings.InventoryScanCacheSeconds`）+ single-flight 合并并发扫描；显式动作强制刷新。
- 生态映射独立成静态 `ToolEcosystemMap`（python→Pip、node→Npm、dotnet→DotnetTool，OrdinalIgnoreCase），不改 `EnvironmentComponentCatalog`；Git/Java 无生态 → 面板隐藏。
- 三适配器继承 `ToolExtensionProviderBase`（`LocateExecutableAsync`：where.exe 首匹配 → `WindowsPathLocator.ResolveFromPath` → `ResolveWindowsAppPath`；`RunCheckedAsync` 非零退出抛异常）：
  - Pip：`python -m pip ...`（不依赖 pip.exe 在 PATH）；install 带版本用 `==version`，无版本用 `--upgrade`；uninstall 加 `-y`。
  - Npm：Windows 下是 .cmd shim → 经 `cmd.exe /s /c "<完整命令串>"` 运行；`ls`/`outdated` 非零退出但 JSON 合法时仍解析；outdated 返回 `{name:{latest}}` 形态。
  - DotnetTool：`dotnet tool list -g` 表格解析（跳过 "Package Id" 表头行与全横线分隔行）；install 带/不带 `--version`；无「可用更新」。
- `ToolExtension(Ecosystem, Name, Version, AvailableVersion?)`：`HasUpdate` 由非空 `AvailableVersion` 决定；`ToolExtensionInventoryService.Merge` 用 outdated 结果填 `AvailableVersion`（同版本清空、未安装的 outdated 忽略）。

## 关键教训 / Bug 修复
- **dotnet tool list 缺 `tool` 前缀（真实环境 exit 1）**：`ListInstalledAsync` 曾写成 `["list","-g"]`，而 `dotnet list` 是另一顶层命令（要求 package/reference 子命令），真实环境报「未提供必需的命令。」exit 1。install/uninstall/upgrade 均有 `tool` 前缀，唯独 list 漏。修复为 `["tool","list","-g"]` 并补命令形态回归测试 `ListInstalledAsync_UsesToolListCommand`。教训：三适配器的命令形态断言应逐一覆盖（FakeProcessRunner 断言 Arguments），此前只断言了输出解析，命令形态错误无法被测试拦住。
- **`_activeEcosystem` 生态守卫**：`LoadExtensionsForSelectionAsync` 中该字段在 await 前未赋值，导致 `if (_activeEcosystem == panelEcosystem)` 守卫恒为 false、列表永不发布（16 个 VM 测试失败）。修复：选中工具即先赋占位值再置 `ExtensionPanelVisible = true`，扫描完成时若已切换工具则丢弃结果。
- **复数命令命名**：命令由 `UninstallExtensionsAsync`/`UpgradeExtensionsAsync` 生成复数 `UninstallExtensionsCommand`，而 `[NotifyCanExecuteChangedFor]` 与 XAML 绑定曾写单数名 → MVVMTK0016 编译错误 + 运行时绑定失效。属性名必须与生成的命令名完全一致。
- 测试定位模拟：复用 `RuntimeProviderTests` 范式——`TempDirectory` + 真实空 exe 文件让 `File.Exists` 通过 where.exe 解析；命令断言取 `runner.Calls[^1]`（where.exe 定位会产生第一次调用）；Arguments 断言不含 FileName（"python"/"dotnet" 是 FileName）。

## 文件清单（新增）
- Core：`Models/ToolExtensionModels.cs`、`Interfaces/ToolExtensionContracts.cs`
- Runtime：`Extensions/ToolEcosystemMap.cs`、`ToolExtensionProviderBase.cs`、`PipExtensionProvider.cs`、`NpmExtensionProvider.cs`、`DotnetToolExtensionProvider.cs`、`ToolExtensionInventoryService.cs`
- Composition：`ServiceCollectionExtensions.cs` 注册 3 provider + 清单服务
- Desktop：`ToolsView.xaml` 子面板（Row 3 Auto，含错误行与空态互斥 DataTrigger）、`ToolsViewModel.cs`
- 测试：`FakeServices.cs` 新增 `FakeToolExtensionProvider`/`FakeToolExtensionInventoryService`；新建 5 个测试文件（ToolEcosystemMap / Pip / Npm / DotnetTool / Inventory / ToolsViewModel 共 55 个测试）；4 处 `new ToolsViewModel(...)` 构造点追加第 7 参。

## 验证状态
已验证：`dotnet build` 0 警告 0 错误；`dotnet test --filter Category!=Integration` 全量 1509 个测试 0 失败（新增 55 个全绿）。等用户确认后可考虑将「三适配器命令形态」等稳定事实提升到 AGENTS.md。
