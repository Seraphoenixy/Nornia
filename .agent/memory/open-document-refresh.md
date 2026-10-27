# 打开文档自动刷新

2026-10-11：EditorAreaViewModel 原先仅为 FilePreviewTab 注册 IFileContentWatcher，DiffTab 只依赖仓库事件。现在非历史提交 Diff 按 RepositoryPath + Request.Path 注册文件监听，与源码标签共享注册生命周期；文件事件按路径刷新两个类型的所有打开标签，不区分预览/正式。历史提交 Diff 不监听工作区文件；index/HEAD 变化仍沿用仓库事件。

FilePreviewReloadTests 新增预览/正式 Diff 与源码共享路径时同步刷新、关闭一个标签不取消监听、最后标签关闭才取消监听的覆盖。
