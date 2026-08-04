# ArkFramework 使用入口

1. 通过 **Create > ArkFramework > Framework Profile** 创建配置。
2. 通过 **Create > ArkFramework > Modules** 创建需要的模块 Installer。
3. 按依赖顺序把 Installer 加入 Profile。
4. 在启动场景创建 `FrameworkHost` 并配置 Profile。
5. 进入 Play Mode，由 Runtime 统一启动、更新和关闭模块。

模块可以通过 `FrameworkRuntime.Services.Resolve<T>()` 获取已注册服务。短生命周期
资源应及时释放对应 Lease 或 Handle；模块停机只作为最终清理保障。

## Complete Sample

从 Package Manager 导入 **Complete Sample** 后，执行
**ArkFramework > Samples > Rebuild Sample Content**。生成器会创建并注册示例所需
的 Addressables、Profile、Installer、场景、UI、配置与音频资源。随后打开
`Generated/Scenes/Bootstrap.unity` 即可运行完整流程。

也可以执行 **ArkFramework > Samples > Import Complete Sample** 调用 Package
Manager 导入 API；导入完成后仍需执行一次重建命令，以同步 Addressables 和
Build Settings。

## Platform 平台初始化

1. 创建平台预制体，并按平台需要组织一个或多个 Canvas。
2. 在任意层级的 `RectTransform` 节点上添加 `PlatformUIRoot`，为每个节点设置唯一
   ID，例如 `Normal`、`System` 或项目自定义的 `WorldPanel`。
3. 创建 `PlatformModuleInstaller`，把平台预制体赋给 `Platform Prefab`。
4. 将 Platform Installer 放入 `FrameworkProfile`，通常位于 UI Installer 之前。

UI 根节点不要求是平台预制体的第一级子物体，也不要求自身带 Canvas。框架只保存
节点引用并把窗口挂到节点下，不会重写节点坐标、锚点、缩放或 Canvas 参数，因此
用户脚本可以在 Awake、Start 或运行时调整位置。Overlay、Camera 和 World Space
三种 Canvas 渲染模式均可使用。

`UIWindowDescriptor` 的可选 `rootId` 指定目标根节点；省略时使用 `UILayer` 名称。
例如 Normal 窗口默认查找 ID 为 `Normal` 的 `PlatformUIRoot`。配表项目可以直接
增加 `RootId` 字段并传入窗口描述。EventSystem 属于平台预制体或业务项目自己的
内容，ArkFramework 不创建、不复用，也不校验 EventSystem。

平台 SDK 提供专用 Graphic Raycaster 时，可在平台预制体上添加自定义配置器：

```csharp
using System;
using ArkFramework;

public sealed class VendorRaycasterConfigurator
    : PlatformGraphicRaycasterConfigurator
{
    public override Type RaycasterType => typeof(VendorGraphicRaycaster);
}
```

`VendorGraphicRaycaster` 必须继承 `BaseRaycaster`。模块只遍历实例化的平台预制体
内部的 `Canvas`，并保证每个 Canvas 只添加一个目标组件；不会修改 Canvas 的
Render Mode、相机、排序或变换。配置器默认移除标准
`GraphicRaycaster`；重写 `ReplacesStandardGraphicRaycaster` 可让两者共存，
重写 `AppliesTo` 可只处理指定 Canvas，重写 `ConfigureRaycaster` 可写入平台参数。
运行时也可以解析 `IPlatformService`，通过 `UIRoots`、`TryGetUIRoot` 或
`GetUIRoot` 读取实例中的命名根节点。

## Rig 与场景相机同步

1. 在平台预制体中创建一个或多个 `CameraRig`，设置唯一 ID 和默认 Rig。
2. 在每个受控相机上添加 `RigCameraSlot` 并设置槽位 ID；同一 Rig 支持多个槽位。
3. 在场景相机上添加 `SceneCameraBinding`，设置目标 Rig、槽位，并按需标记一个
   `Pose Source`。
4. 创建 `RigModuleInstaller`，与 Platform、EventBus、Scene 和 Table Installer
   一起加入 Profile。

`CameraRig.PoseRoot` 决定位置同步实际移动的节点，普通相机可使用 Rig 根节点，XR
项目可指向 XR Origin。基础 Rig 模块不引用 XR SDK；XR 包可以实现并注册
`IRigComponentSynchronizer`，为特定组件提供专用复制逻辑。

场景资源可以放入 StreamingAssets 中的 `SceneTableRow` 配表，并在
`SceneModuleInstaller.SceneTablePath` 配置相对路径。之后按 ID 切换：

```csharp
ISceneService scenes = runtime.Services.Resolve<ISceneService>();
await scenes.LoadByIdAsync("Game.Main");
```

每行可独立控制是否同步 Rig 位置、`Camera.CopyFrom` 参数、指定完整类型名的组件，
以及同步后是否禁用匹配的场景相机。清空 `RigId` 并关闭全部同步开关，即可只进行
场景切换。

## 包测试

核心 EditMode 与 PlayMode 测试位于包内 `Tests` 目录。目标项目需要在
`Packages/manifest.json` 顶层加入
`"testables": ["com.kmax.arkframework"]` 才会编译并显示包测试。Sample 集成
测试位于 `Samples~/Complete Sample/Tests`，仅在导入 **Complete Sample** 后编译。

## CSV 配表

1. 创建 `Assets/StreamingAssets/Tables/Items.csv`。
2. 使用 UTF-8 保存，并在文件开头声明目标类、字段和类型：

   ```csv
   #class,MyGame.Tables.ItemRow
   #output,Assets/Generated/Tables
   #fields,Id,Name,Price,Enabled,Tags,Quality
   #types,int,string,float,bool,string[],MyGame.Tables.ItemQuality
   #key,Id
   #comments,编号,名称,价格,是否启用,标签,品质
   ,1,"Sword, Basic",12.5,1,weapon|starter,Rare
   ,2,Shield,8.25,false,,Common
   ```

3. 选中 CSV，执行 **ArkFramework > Tables > Generate Selected Classes**。
   **Generate All Classes** 会处理 `Assets/StreamingAssets` 下的全部 CSV。
4. 创建 `TableModuleInstaller` 并加入 `FrameworkProfile`。
5. 运行时按相对于 StreamingAssets 的路径读取：

   ```csharp
   ITableService tables = runtime.Services.Resolve<ITableService>();
   TableData<MyGame.Tables.ItemRow> items =
       await tables.LoadAsync<MyGame.Tables.ItemRow>("Tables/Items.csv");
   MyGame.Tables.ItemRow item = items.Get(1);
   ```

`#class`、`#fields`、`#types` 必填；`#output`、`#key`、`#comments` 可选。
省略 `#output` 时生成到 `Assets/Generated/Tables`。字段可以是公开属性或公开字段；
生成器默认创建带公开属性的 `[Serializable]` 类。

为了在 Excel 等表格软件中让字段、类型、注释和数据纵向对齐，数据行可以在最前面
增加一个空单元格，例如 `,1,Sword`。该空白 A 列只用于对齐 `#fields` 等指令，
解析时会自动忽略；不需要表格对齐时仍可写成 `1,Sword`。

任意位置都可以添加整行注释：在 A 列以 `//` 开头即可。`//,1,Sword` 可以临时
禁用一条完整数据，`// 这里是说明` 可以写普通说明；注释可以放在指令之前、指令
之间、数据之间或表尾。`#comments` 仍表示生成类的字段文档，不是整行注释。
如果第一项真实数据本身以 `//` 开头，请使用对齐格式并保留空白 A 列，例如
`,//server/share,Network`。

支持 `string`、`bool`、所有常用整数和浮点类型、`char`、`Guid`、`DateTime`、
枚举、Nullable（如 `int?`）和一维数组（如 `string[]`）。数组单元格以 `|`
分隔。CSV 遵循双引号规则：逗号或换行可以包含在引号内，双引号写成 `""`。
布尔值接受 `true`、`false`、`1`、`0`。数值按固定文化解析。

服务会按“目标类型 + 相对路径”缓存结果；`forceReload: true` 可强制重载，
也可以使用 `TryGetLoaded`、`Unload` 和 `Clear` 管理缓存。Android、WebGL 等平台
的 StreamingAssets 由 `UnityWebRequest` 读取，本地文件平台使用 UTF-8 文件读取。

Complete Sample 重建时会生成
`Assets/StreamingAssets/ArkFrameworkSample/UI.csv` 和 `Scenes.csv`。示例通过
`Id` 主键读取窗口
Addressables 地址、层级、模式和交互参数，调用
`ISampleUIService.OpenAsync(tableId)` 打开窗口，可作为业务 UI 配表接入参考。
