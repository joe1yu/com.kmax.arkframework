# ArkFramework

ArkFramework 是面向 Unity 2021.3 的模块化基础框架。包内包含运行时程序集与
通用 Editor 工具，包 ID 为 `com.kmax.arkframework`。完整 EditMode/PlayMode
不依赖 Sample 的验证测试随包保存在 `Tests` 目录中。

## 安装

- 从其他项目本地安装时，在 Package Manager 中选择
  **Add package from disk**，并选择本目录的 `package.json`。
- 从 Git 仓库安装时，可使用
  `https://github.com/joe1yu/com.kmax.arkframework.git#main`。

安装完成后，创建 `FrameworkProfile` 和需要的 `ModuleInstaller`，再把 Profile
赋给场景中的 `FrameworkHost`。完整接入说明请参阅源码仓库根目录 README。

## 示例

在 Package Manager 中选择 ArkFramework，导入 **Complete Sample**。然后执行
**ArkFramework > Samples > Rebuild Sample Content**，打开示例目录下的
`Generated/Scenes/Bootstrap.unity` 并进入 Play Mode。

也可以直接执行 **ArkFramework > Samples > Import Complete Sample** 完成导入；
再次执行时会覆盖此前导入的同版本示例。

完整示例覆盖十二个功能模块、Addressables 资源、CSV UI 配表、配置、
场景/UI/音频切换、ActionKit Fluent API 和运行时诊断。生成器会根据导入位置自动解析示例根目录，
因此不依赖固定的 `Assets` 路径。

## 平台初始化

创建 `PlatformModuleInstaller` 并指定平台预制体。模块启动时会实例化该预制体，
递归收集其中带 `PlatformUIRoot` 的命名 UI 根节点。根节点可以位于任意层级；
框架不会修改其坐标、锚点或所属 Canvas，因此用户脚本可以在 Awake、Start 或
运行时继续移动它。建议把 Platform Installer 放在 UI Installer 之前。

窗口描述的 `rootId` 用于选择根节点；省略时默认使用 `UILayer` 的名称，例如
`UILayer.Normal` 对应 ID 为 `Normal` 的根节点。Canvas 可以使用
Screen Space - Overlay、Screen Space - Camera 或 World Space。EventSystem 完全由
平台预制体或项目自行配置，框架不会创建、复用或校验它。

平台 SDK 需要自定义 UI 射线检测器时，在平台预制体上添加一个继承
`PlatformGraphicRaycasterConfigurator` 的组件，并让 `RaycasterType` 返回 SDK 的
`BaseRaycaster` 类型。平台模块会为该平台预制体实例内部的每个 `Canvas` 添加该
组件，但不会扫描场景中的其他 Canvas。默认会替换标准 `GraphicRaycaster`；需要两者共存时重写
`ReplacesStandardGraphicRaycaster` 并返回 `false`。

## 配表

将带有 `#class`、`#fields` 和 `#types` 声明的 UTF-8 CSV 放入
`Assets/StreamingAssets`。在 Project 窗口选中表格后执行
**ArkFramework > Tables > Generate Selected Classes**；也可以执行
**Generate All Classes** 批量生成。运行时把 `TableModuleInstaller` 加入 Profile，
再通过 `ITableService.LoadAsync<T>("相对 StreamingAssets 的路径")` 加载。
完整表格式与类型说明见 Package Manager 的 Documentation 页面。

## 测试

需要运行包内测试时，在目标项目 `Packages/manifest.json` 顶层的 `testables`
数组中加入 `"com.kmax.arkframework"`。Sample 集成测试保存在
`Samples~/Complete Sample/Tests`，导入 **Complete Sample** 后才会随示例编译，
因此未导入 Sample 的项目也可以独立运行核心包测试。
