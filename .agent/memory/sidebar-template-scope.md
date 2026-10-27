# 侧栏模板作用域

2026-10-15：MainWindow 与 App 同时为页 VM 注册隐式 DataTemplate，窗口用于主页面、App 用于侧栏。MaterializeSidebarFor 使用窗口 TryFindResource 会优先返回主页面模板，导致侧栏显示主页面。改为显式从 Application 资源解析侧栏模板；SidebarTemplateResolutionTests 覆盖 Git 与 Projects 的视图类型。
