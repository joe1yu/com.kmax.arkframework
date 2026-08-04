# ArkFramework Complete Sample

该示例演示 ArkFramework 的完整启动与页面切换流程，包含 Platform、EventBus、
Resource、Pool、Config、Table、FSM、Scene、UI、Audio、ActionKit、Procedure 和
运行时诊断。

## 运行

1. 如果示例来自 Package Manager，先点击 **Import**。
2. 执行 **ArkFramework > Samples > Rebuild Sample Content**。该命令会在当前
   示例目录的 `Generated` 下生成 Profile、Installer、场景、UI、音频与配置，
   同时生成 `Assets/StreamingAssets/ArkFrameworkSample/UI.csv`，并注册
   Addressables 和 Build Settings。
3. 打开 `Generated/Scenes/Bootstrap.unity`。
4. 进入 Play Mode，通过主菜单和 Gameplay HUD 的按钮往返切换。

页面按钮通过 ActionKit Fluent API 在下一帧启动异步 Procedure 切换；加载窗口、
Addressables 场景/UI、背景音乐和配置读取均由框架模块协同完成。

生成的 `PlatformRoot.prefab` 定义平台级 Canvas 和唯一的 `EventSystem`，并由
`PlatformModuleInstaller` 在框架启动时实例化。项目接入具体平台 SDK 时，可以在
该预制体上挂载 `PlatformGraphicRaycasterConfigurator` 子类，为所有 UI Canvas
自动安装平台专用 Graphic Raycaster。

三个窗口的 Addressables 地址、层级、窗口模式和交互开关都来自 `UI.csv`。
`SampleUIService.OpenAsync(tableId)` 先按配表主键查询，再调用对应窗口类型，
Procedure 不再硬编码 `UIWindowDescriptor` 或泛型打开入口。CSV 的 A 列保留给
`#fields/#types/#comments` 指令，数据从 B 列开始，以便在表格软件中纵向对齐。
在 A 列写 `//` 可以注释任意一行；UI.csv 包含一条 `Sample.Disabled` 示例，
用于展示如何在不删除原始内容的情况下临时禁用配置。

重建命令可以重复执行，并会保留生成资产的 GUID。若目标项目已有同名
`ArkFramework Samples` Addressables Group，请先合并或重命名冲突项。
