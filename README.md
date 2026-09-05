# Volcano-FontReplace

面向 SPT/Escape from Tarkov 的 BepInEx 多语言字体切换插件。插件会读取游戏当前语言，并自动使用该语言自己的字体包和缩放设置。

## 支持的语言配置

| 配置 | 识别的 locale |
| --- | --- |
| 中文 | `ch`、`cn`、`zh`、`zh-CN` 等 |
| 英文 | `en`、`en-*` |
| 俄文 | `ru`、`ru-*` |
| 日文 | `jp`、`ja`、`ja-*` |
| 韩文 | `kr`、`ko`、`ko-*` |

每种语言都有独立的“字体切换”和“字体缩放”设置。字体包留空时，该语言保持游戏原版字体。F12 只显示当前游戏语言对应的字体选项，其分类、名称、说明和字体选择按钮也会跟随语言切换；其他语言配置仍保存在磁盘中，因此切换语言不会丢失或生成重复配置。

## 安装

1. 将 `FontReplace.dll` 放入 `BepInEx/plugins/FontReplace`。
2. 在同一目录创建 `Font` 文件夹。
3. 将包含 `TMP_FontAsset` 的 Unity AssetBundle 放入 `Font`。
4. 启动游戏，在 F12 配置管理器中为需要的语言选择字体包并点击“应用”。

字体包内优先查找与文件同名的 `TMP_FontAsset`，找不到时使用包内第一个 `TMP_FontAsset`。如果字体资产没有 `sourceFontFile`，TMP 文本仍可使用，但旧版 `UnityEngine.UI.Text` 无法应用该字体。

## GUI 排除

默认启用“排除 VolcanoSubtitle 全部界面”，覆盖以下范围：

- 屏幕字幕、弹幕和 3D 气泡；
- VolcanoSubtitle 设置窗口和预览面板；
- 台词过滤面板；
- 调试界面。

如需排除其他 Mod 的 GUI：

1. 启用“排除指定 Mod GUI”。
2. 优先在“额外排除的 GUI 根节点”填写稳定的 GameObject 根节点名。
3. 根节点无法确定时，再填写“排除的程序集或类型关键字”。插件会检查文本对象祖先上的 `MonoBehaviour` 程序集名和完整类型名。

多个值可用分号、逗号或换行分隔。程序集/类型检测比根节点匹配开销更高，因此默认关闭。

GUI 排除区的开关和匹配规则均属于进阶选项，需要在 Configuration Manager 中启用“显示进阶设置”后查看。

## 恢复与安全策略

- 语言切换、关闭模组和插件卸载都会恢复 TMP 默认字体、已修改文本及 LocaleManager 字体映射。
- 只恢复插件实际记录并替换过的文本，不会把所有文本强制改成同一个默认字体。
- 字体包路径只接受 `Font` 目录内的文件名，拒绝目录穿越路径。
- 字体包加载失败时不会提交无效资产，也不会显示成功提示。
- Harmony 动态文本监听始终覆盖运行时新建/复用的 TMP 和旧 UGUI 文本，不依赖“保留字母/数字”开关。

## 构建

目标配置：

- `Release-3.11`
- `Release-4.0`
- `Release-4.1`

本机默认开发路径存在时会自动采用；其他环境需要传入目标游戏路径：

```powershell
dotnet build FontReplace.csproj -c Release-4.1 -p:GameVersionPath="D:\SPT-4.1"
```

普通构建只生成 DLL，不会覆盖游戏安装。需要部署时显式启用：

```powershell
dotnet build FontReplace.csproj -c Release-4.1 -p:DeployOnBuild=true
```

也可以同时传入 `GamePluginPath` 或 `PackagePluginPath` 指定部署目标。

## 发布检查清单

- 三个 Release 配置均以 0 warning / 0 error 编译；
- 运行 `tools/Test-Compatibility.ps1` 检查目标程序集的 LocaleManager 契约；
- 分别测试五种语言的启动、局内切换、字体包切换和缩放；
- 测试禁用/重新启用以及错误、空、损坏字体包；
- 测试 VolcanoSubtitle 设置、过滤器、字幕、弹幕和 3D 气泡字体不受影响；
- 包含 README、CHANGELOG、LICENSE，以及所分发字体各自的许可证与署名。

## 已知限制

“保留原版字母/数字”目前按整个文本组件判定。中日韩文本中夹杂的拉丁字母或数字不会在同一组件内混排两套字体；要实现逐字符混排，需要改用专门构建的 TMP fallback 字体链。
