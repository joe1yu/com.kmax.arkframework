# Changelog

本文件记录 ArkFramework UPM 包的公开变更。

## [0.1.0] - 2026-08-03

- 首次提供 UPM 包结构。
- 包含 Core、EventBus、Resource、Pool、Config、FSM、Procedure、Scene、UI、
  Audio、ActionKit、Diagnostics 与通用 Editor 工具。
- 提供可从 Package Manager 导入的 Complete Sample，覆盖 Addressables、完整模块
  Profile、ActionKit Fluent API 与可重复生成的场景流程。
- 新增 **ArkFramework > Samples > Import Complete Sample**，并支持从旧版
  `Assets/ArkFramework/Samples` Build Settings 路径自动迁移。
- 新增 Table 配表模块：支持 StreamingAssets UTF-8 CSV、Editor 类生成、运行时
  类型映射、主键索引、缓存以及跨平台读取。
- Complete Sample 的 UI 注册与打开改为读取 StreamingAssets CSV，并通过配表 ID
  定位和打开窗口。
- CSV 表支持在任意位置使用 `//` 整行注释，可保留说明或临时禁用数据。
- EditMode 与 PlayMode 测试迁入 UPM 包的 `Tests` 目录，并通过 Manifest
  `testables` 启用。
- Sample 集成测试改为随 Complete Sample 导入，避免未导入 Sample 的项目因
  缺少 `ArkFramework.Samples` 程序集而编译失败。
- 新增 Platform 平台初始化模块：支持平台预制体中的任意层级命名 UI 根节点、
  三种 Canvas 渲染模式，以及为预制体内部 Canvas 安装平台专用 Graphic Raycaster；
  EventSystem、根节点位置和 Canvas 参数均由项目自行管理。
- 为旧版已导入 Sample 保留已废弃的 EventSystem/Canvases 只读兼容入口，避免更新
  UPM 包后、重新导入 Sample 前出现源码编译错误；兼容入口不参与生命周期管理。
