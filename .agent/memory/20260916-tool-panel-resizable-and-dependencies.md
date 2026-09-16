# 2026-09-16 扩展依赖子面板：可拖动高度 + pip/npm 包依赖树

## 需求
1. 「开发工具」页扩展依赖子面板高度固定 240 不可调 → 改为可拖动（GridSplitter）。
2. 包依赖关系：pip + npm 双生态，可递归懒加载展开的依赖树（用户已确认）。

## 关键设计（已实现并通过测试）
- **模型/契约**：`ToolExtensionDependency(Name, Version, Constraint, IsLeaf)`（Core 模型）；`IToolExtensionProvider` 新增 `GetDependenciesAsync(name)` 只返回**直接依赖一层**，递归展开由 VM 逐节点驱动。
- **pip**：`python -m pip show <name>` 输出 RFC 头，解析第一个 `Requires:` 行（值可能缩进延续）。`SplitRequirements` 拆分 `pkg[extra]>=1.0,<2` → Name=pkg、Constraint=">=1.0,<2"；**版本说明符内部逗号续段**（`<2`）以版本符号开头则并入前一项，避免误判独立包。pip 的 IsLeaf 恒 false（是否叶子需递归才知道）。
- **npm**：`cmd.exe /s /c "npm ls -g --json"` 一次拿整棵全局树，`BuildDependencyIndex` 建 name→直接依赖 索引（实例级内存缓存，进程只派生一次）；key 剥 `@版本` 后缀（peer 冲突场景，`StripVersionSuffix` 从下标 1 找 @ 避免误剥 scoped）；IsLeaf 精确（有嵌套 dependencies 才可展开）。
- **dotnet tool**：无依赖概念，返回空列表。
- **VM**：`ToolExtensionDependencyNode`（ObservableObject，Name/Version/Constraint/IsLeaf/IsLoaded/IsLoading/Children）；ToolsViewModel 加 `SelectedExtension`（单选，与多选并存）、`DependencyRoots`、`DependencyPanelTitle`、`dependencyErrorText`（树错误独立显示，**不复用** SetExtensionPanelError 以免清空扩展列表）；`LoadRootDependenciesAsync` 复用 `_activeEcosystem` 守卫；`LoadDependencyNodeAsync` 单飞（IsLoading/IsLoaded）；`SyncDependencyTree` 在列表刷新后清失效选中。
- **XAML**：外层 Grid 5→6 行，Row3 插 `HorizontalSashStyle` GridSplitter（ResizeDirection=Rows、PreviousAndNext、Visibility 随面板同步），工具网格 `*` MinHeight=120、面板行 240 MinHeight=120；面板内部改两列（左扩展 DataGrid + 右依赖树 Border 宽 260）；TreeViewItem.Expanded 经 EventSetter 转发到 code-behind 薄调用 `LoadDependencyNodeAsync`；空态用 `DependencyRoots.Count` DataTrigger。

## 关键教训
- WPF GridSplitter 调整的是 GridLength：面板行初始固定值（240），拖拽后变像素值；相邻行 MinHeight 防拖没。
- FakeToolExtensionProvider 的异常注入（`GetDependenciesException`）容易被忘接入方法体——声明属性后方法仍返回罐头导致错误路径测试失败。
- pip Requires 的版本说明符内部逗号（`>=1.0,<2`）与包间逗号歧义：以版本符号开头的切分段并入前项 Constraint。

## 文件清单（新建/修改）
- Core：`Models/ToolExtensionModels.cs`、`Interfaces/ToolExtensionContracts.cs`
- Runtime：`Extensions/{ToolExtensionProviderBase,PipExtensionProvider,NpmExtensionProvider,DotnetToolExtensionProvider}.cs`
- Desktop：新建 `ViewModels/ToolExtensionDependencyNode.cs`；修改 `ViewModels/ToolsViewModel.cs`、`Views/ToolsView.xaml`、`Views/ToolsView.xaml.cs`
- 测试：`Fakes/FakeServices.cs`、`PipExtensionProviderTests.cs`、`NpmExtensionProviderTests.cs`、`ToolsViewModelTests.cs`、`ToolExtensionInventoryServiceTests.cs`（GatedFakeProvider 补实现）

## 验证状态
已验证：构建 0 警告 0 错误；全量 1528 测试 0 失败（新增约 17 个依赖树测试全绿）。拖拽/树的实机交互需用户手动确认。
