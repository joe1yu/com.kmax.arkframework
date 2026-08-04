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
`Assets/StreamingAssets/ArkFrameworkSample/UI.csv`。示例通过 `Id` 主键读取窗口
Addressables 地址、层级、模式和交互参数，调用
`ISampleUIService.OpenAsync(tableId)` 打开窗口，可作为业务 UI 配表接入参考。
