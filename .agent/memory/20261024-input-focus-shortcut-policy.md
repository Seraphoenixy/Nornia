# 2026-10-24 输入框聚焦时的快捷键归属（VS Code 语义）

## 现象
"界面上输入框对快捷键的支持不全面"。分清两种相反的理解后由用户裁决为 **A：按 VS Code 语义放开**
（输入框只让出与文本编辑冲突的按键，其余应用快捷键照常生效），而不是 B（输入框彻底让位）。

## 根因（代码可证）
默认快捷键表把 `!inputFocus` 当成了"输入框里什么都别触发"的通用开关，于是焦点落进任何 TextBox 后：

- **静默失效**（带 `!inputFocus`，且这些键在 TextBox 里没有任何原生含义）：`Ctrl+W` 关闭活动编辑器、
  `Ctrl+Tab`/`Ctrl+Shift+Tab` 最近编辑器、`Ctrl+PgUp/PgDn` 上下一个编辑器、`Ctrl+\`/`Ctrl+Shift+\` 拆分、
  `Ctrl+K Ctrl+←/→` 切换编辑器组。失效名单与"是否与文本编辑冲突"无关，纯属随手加守卫。
- **仍然生效**（无守卫）：`Ctrl+B`/`Ctrl+J`/`Ctrl+1..8`/`F5`/`Ctrl+\``/`Ctrl+/`/`Ctrl+P`/`Ctrl+Shift+P`/
  `Ctrl+Shift+F`/`Ctrl+,` —— 正好是 VS Code 也允许在输入框里生效的那一批。
- 真正与文本编辑冲突的只有 `Ctrl+A`（全选）与 `Ctrl+C`（复制），`docs/design.md` §15.3 也只声明了这两个。
- 附带缺陷：`when` 同时被当作 `Enablement`，而命令面板是独立 Popup（窗口的 `PreviewKeyDown` 不在其中运行，
  `inputFocus` 保持为进入面板前的旧值）→ 面板里"复制所选内容/全选当前列表"会因焦点上下文而变灰。

## 修复
- 上述七个导航/拆分快捷键改成 `!terminalFocus`（终端是按键汇，Ctrl+Tab/Ctrl+\ 等必须留给 shell），
  `Ctrl+W`/`Ctrl+PgUp`/`Ctrl+PgDn` 不再需要守卫（它们原本就用 `|| terminalFocus` 允许终端路由）。
- `Ctrl+A`/`Ctrl+C` 保留 `!inputFocus`：分发器返回 false，交给 TextBox/AvalonEdit/终端各自的原生行为。
- `Add(...)` 增加 `dispatchOnly`：`copyFocused`/`selectAllFocused` 的焦点条件只用于分发、不写进
  `Enablement`（否则面板里永远灰着；它们的执行体本来就按当前焦点元素决定是否动作）。

## 证据
`CommandShortcutFocusPolicyTests`（新增，用**生产注册表 + 生产绑定表**，不另抄一份 when 子句）：
输入框聚焦时 `Ctrl+W`/`Ctrl+PgUp/PgDn`/`Ctrl+Tab`/`Ctrl+Shift+Tab`/`Ctrl+\`/`Ctrl+Shift+\`/`Ctrl+K Ctrl+→`
必须仍被消费（其中 `Ctrl+W` 断言真的关掉活动标签）、`Ctrl+A`/`Ctrl+C` 必须不被消费（列表聚焦时则被消费）、
`terminalFocus` 时导航/拆分/全选/复制都不被劫持而 `Ctrl+W`/`Ctrl+PgDn` 仍路由到终端、
焦点条件不使命令在面板里被拒绝执行。

## 被排除的另一半（B 方向，未采纳）
"输入框聚焦时不劫持任何应用快捷键"会让 `Ctrl+B`/`Ctrl+J`/`Ctrl+1..8`/`F5`/`Ctrl+\`` 也失效，
与 VS Code 行为相反（用户在输入框里按 Ctrl+B 期望折叠侧栏），并且对"打字被打断"毫无帮助
——这些键在 TextBox 里本来就没有原生含义。

## 验证状态
`dotnet build Nornia.sln` 0 错误 0 警告；`CommandShortcutFocusPolicyTests` + 命令平台相关用例 60 通过；
全量 `dotnet test --filter Category!=Integration` 见本轮记录。未做真机 GUI 走查（WPF 窗口无法在会话内交互）。

## 提升状态
已提升到 `docs/design.md` §15.3（新增"输入框聚焦时的快捷键归属"一段，含证据）与
`docs/development.md`（命令平台条目）。本文件保留为经验层（两种理解的取舍理由与失效清单）。
