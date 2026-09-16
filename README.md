# Nornia

Windows 原生的低内存项目观察与开发环境工作台。Desktop 使用 WPF/MVVM，外壳仿 VS Code：左侧活动栏提供环境管理、资源管理器、搜索、源代码管理、项目管理与设置六个顶层入口；资源管理器、搜索和源代码管理共用同一个只读编辑器区（文件预览与 diff 标签并存于同一标签条），并配有低开销多 Shell 终端；不使用 Electron 或 WebView。Nornia 负责快速打开、阅读、全局搜索、Git 审阅和本机环境管理，不提供编辑器、LSP、调试器或扩展生态。

## Build

```powershell
dotnet restore Nornia.sln
dotnet build Nornia.sln
dotnet test Nornia.sln
```

## CLI

```powershell
dotnet run --project src/Nornia.CLI -- runtime list
dotnet run --project src/Nornia.CLI -- tool install dotnet 10
dotnet run --project src/Nornia.CLI -- runtime install node 22
dotnet run --project src/Nornia.CLI -- tool list
dotnet run --project src/Nornia.CLI -- package search python
dotnet run --project src/Nornia.CLI -- cache scan
dotnet run --project src/Nornia.CLI -- cache clean --id <candidate-id> --apply
dotnet run --project src/Nornia.CLI -- project init . --name Ygdria
dotnet run --project src/Nornia.CLI -- project export . .\Nornia.snapshot.yaml
dotnet run --project src/Nornia.CLI -- project import .\Nornia.snapshot.yaml .
dotnet run --project src/Nornia.CLI -- env check .
dotnet run --project src/Nornia.CLI -- env plan .
dotnet run --project src/Nornia.CLI -- env fix . --apply
```

CLI 语言跟随系统区域设置，也可用全局 `--lang` 指定，例如 `Nornia --lang en runtime list` 或 `Nornia --lang zh-Hans runtime list`。

`project init` 创建 `Nornia.yaml`。Runtime Provider 负责发现环境，安装、卸载和升级操作由软件包 Provider 完成（默认 Winget，本机装有 Scoop/Chocolatey 时按操作自动选择）。

Git 集成（仿 VS Code 源码管理，只读 diff、不提供编辑）：Desktop 的顶层“源代码管理”页在二级左侧栏展示分支/领先落后状态、暂存/取消暂存/丢弃（多选批量、拖放暂存）、提交与拆分菜单（提交/提交并推送/提交并同步）、获取/拉取/推送/安全同步、贮藏（stash/pop/drop）、传入/传出更改的净文件影响预览与分页提交历史、分支管理；更改列表支持平铺/树状布局，文件变更由监视器自动刷新。选中更改或提交文件会在共享编辑器区打开行内/分栏 diff（未修改区域折叠、字符级差异、上一个/下一个更改）。资源管理器树按仓库状态显示更改字母与文件夹圆点，二者可互相定位（“在资源管理器中显示”/“查看更改”）。安全“同步”会拒绝脏工作区，并按快进式拉取成功后再推送。

全局搜索（只读）使用 Ctrl+Shift+F 打开搜索侧栏，在当前工作区中按文本、大小写、全词、正则、包含/排除文件进行流式搜索；结果按文件夹→文件→匹配行展示，单击打开预览标签并定位到行列，双击打开固定标签。默认遵守内置排除目录、`files.exclude` 和常用 `.gitignore` 规则；结果有界，不建立全文索引、不提供跨文件替换。

顶层“项目管理”页在二级左侧栏列出已登记项目目录，主内容区提供环境检查/修复工作流：每行可“在资源管理器中打开/在编辑器中打开/移除”，并显示上次检查时间。打开项目会同步文件树、Git 仓库、终端工作目录与项目登记（唯一入口为资源管理器页的“打开项目”，或从项目目录行“打开”路由到资源管理器页）。

Desktop 的“终端”页会发现 PowerShell 7、Windows PowerShell、cmd、Git Bash 和 WSL；可添加本机自定义 Shell 路径，并以项目工作目录启动多个会话。终端输出最多保留 200,000 个字符，关闭标签或退出应用会终止对应子进程。

缓存管理会扫描 `%LOCALAPPDATA%`、`%APPDATA%` 和用户根目录中的应用及开发工具缓存；它不读取或关联已安装软件包，而是根据目录名称推测 Google Chrome、Microsoft Edge、npm、NuGet、Gradle、Maven、Cargo、Yarn、Bun、Go Modules 等应用或生态分类，将同类缓存汇总以便审查。扫描会跳过 Documents、Downloads、OneDrive 等用户数据树；清理只删除候选目录内容并保留目录本身。

环境版本可使用前缀（如 `10`、`3.13`）或比较器范围（如 `>=22 <23`）。`env plan` 只生成修复建议；只有 `env fix --apply` 或 Desktop 中确认“应用修复”后才会调用 Winget。

`Nornia.yaml` 还可声明项目启动命令、所需架构/操作系统以及非敏感环境变量；`project export/import` 用于在团队间复制经验证的环境声明。导出的文件不包含本机已安装软件、令牌或其他凭据。

扫描到的 Runtime 和已安装 Package、初始化/检查/打开过的项目、环境配置与检查绑定，以及操作日志会持久化到 `%LOCALAPPDATA%\Nornia\nornia.db`。环境清单采用"快照优先"刷新：页面打开先用持久化快照秒出首屏，快照在 TTL 内（默认 6 小时）且环境指纹（PATH、工具路径、VC++ 注册表）未变时不重跑任何扫描进程；TTL 过期、指纹变化或用户点「重新扫描/刷新」时才全量扫描。任何安装/卸载/升级之后都会强制重扫以保证列表反映变更，页头会显示「上次扫描」时间供参考。Packages 会显示 Winget 可识别的 `X86`、`X64`、`ARM64` 架构，并仅合并完全重复的包记录；目录缺失的项目会保留为历史记录，可在 Projects 页面手动移除。

`env fix` 批次逐步骤追踪（correlation_id + 修复日志表），失败可生成回滚计划：能自动撤销的步骤自动执行，Winget 降级等步骤给出手动命令提示。软件包渠道除 Winget 外，本机装有 Scoop/Chocolatey 时按操作自动选择。

设置保存在 `%LOCALAPPDATA%\Nornia\settings.jsonc`（用户）与 `<工作区>\.vscode\settings.json`（工作区，JSONC 注释保留），旧版 `settings.json` 启动时一次性迁移并备份；快捷键在 `keybindings.json` 自定义（命令面板 Ctrl+Shift+P 打开，Ctrl+P 快速打开文件）。底部面板含输出/问题/终端三页签，错误自动切到问题页签；代码编辑器提供 TextMate 语法高亮（按主题规则解析 scope，含粗/斜体等字体样式）、VS Code 风格折叠栏（chevron 列 + 折叠段半透明背景）、折叠/大纲/迷你地图、Ctrl+滚轮缩放与查找；阅读器与终端的等宽字体在设置页用下拉框选择（下拉框只列出应用支持的字体，并标注本机未安装项）。编辑器区为可拆分的多编辑器组工作台（VS Code 风格）：组内标签条沿用预览/固定/关闭族/拖拽重排，标签右键或命令（Ctrl+\ / Ctrl+Shift+\）向右/向下拆分，标签拖到其它组中央移动、拖到边缘拆分并入组，组间分割线可拖拽调宽（20%–80%）且最小宽度受保护，2×2 布局时角落分割线联动；关闭标签后回选最近使用的标签（MRU），超限时按最近最少使用（LRU）驱逐；底部面板支持最大化/恢复；关闭最后一个标签自动移除空组（始终保留至少一个组），同一工作区重启后组结构、比例、标签顺序、活动标签与 MRU 序从状态文件（schema v2，旧状态自动迁移）恢复。

Markdown 文件（`.md` / `.markdown`）打开后默认显示 WPF 原生渲染结果，可在“预览/源码”之间切换；支持 CommonMark、常用 GFM/Markdig 扩展、本地相对图片、脚注、表格、任务列表和 `$...$` / `$$...$$`、`\(...\)` / `\[...\]` 数学公式。公式使用 WpfMath 渲染，无法解析的公式保留原文；远程图片、脚本和不安全 HTML 不执行，大文件自动回退源码视图。

详细设计见 [docs/design.md](docs/design.md)，开发约定见 [docs/development.md](docs/development.md)。
