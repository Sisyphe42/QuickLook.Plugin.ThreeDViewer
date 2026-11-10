# QuickLook.Plugin.ThreeDViewer

QuickLook 4.x 的 3D 模型预览插件（.NET Framework 4.6.2 / WPF）。

## 功能

- **格式**（显式白名单，以 Assimp 实测导入集为准）：
  `.glb .gltf .fbx .obj .stl .ply .3ds .dae .off .x3d .3mf`
  明确**不**处理 `.step .iges .usdz`（Assimp 不注册这些导入器）。
- **鼠标交互**：左键拖拽旋转、右键拖拽平移、中键平移、滚轮缩放（含惯性）。
- **glb/gltf**：读取 glTF 2.0 的 PBR 因子（baseColor / metallic / roughness /
  emissive）、内嵌或外部贴图，以及节点动画（平移/旋转/缩放关键帧）。
- **主题**：背景、状态文字、ViewCube 文字跟随系统深/浅主题，并把解析出的
  主题回写给 `ContextObject.Theme`，使 QuickLook 标题栏一致。
- **性能**：`Plugin.View()` 同步只创建主题化外壳（实测 0–9ms），模型导入与
  几何构建在线程池完成；`Init()` 阶段做一次性预热（含完整 WPF 布局），把
  HelixViewport3D 模板 / WPF imaging 的首次加载开销（实测约 2 秒）移出首帧。
- **大模型**：面数 > 500,000 时自动按全局均匀抽样降到 ≤120,000 个三角面，
  并显示提示条。

## 架构要点（均为本机实测结论，非推测）

| 主题 | 结论 |
|---|---|
| 宿主契约 | `QuickLook.Common.Plugin.IViewer`：`Priority / Init / CanHandle / Prepare / View / Cleanup`；`Priority=int.MaxValue` |
| 插件扫描 | `PluginManager` 按 `Directory.GetFiles(UserPluginPath, "QuickLook.Plugin.*.dll", AllDirectories)` 扫描，**不读 Metadata.config**；`Metadata.config` 只由 `PluginInstaller` 读取，`<Namespace>` 决定解压目录名 |
| 接管旧插件 | 用户目录的 HelixViewer 是 `Priority=int.MaxValue` 且目录名字母序在前，会抢走 `.obj/.stl/.ply/.3ds`；必须把**整个目录**移到 `UserData\DisabledPlugins\`（原地改名无效，因为递归扫描仍会命中 dll） |
| 内置 HelixViewer | 主程序目录另有一份 `Priority=-5` 的 HelixViewer，被 `int.MaxValue` 压制，无需改动 scoop 应用目录 |
| AssimpNet | 用 **5.0.0-beta1**（与 QuickLook 4.2.2 内置 HelixViewer 所带程序集字节一致）；4.1.0 的 glTF2 导入器对有效 GLB/GLTF 返回 `MeshCount=0`，无法满足 PBR/动画需求 |
| native dll | 通过 `AssimpLibrary.Instance.Resolver.SetProbingPaths()` + `LoadLibrary(全路径)` 固定到插件目录的 `x64/x86`；不使用 `SetDllDirectory`（进程级全局副作用） |
| 后处理 | 仅 `Triangulate \| FlipUVs \| GlobalScale`。`GenerateNormals` 等标志在部分 X3D 场景触发 `AccessViolationException` |
| HelixToolkit | 用 NuGet **2.24.0** 单 dll（不依赖 `HelixToolkit.dll`）；默认旋转手势是**右键**，已显式改为左键；默认 Z-up，已改为 Y-up |
| PBR | WPF Viewport3D 是固定功能管线，无法原生呈现 metalness/roughness/IBL；本插件把真实 PBR 因子映射到 `DiffuseMaterial + SpecularMaterial + EmissiveMaterial` 的近似，并解出贴图。这是"预览级近似"而非离线级 PBR |

## 构建

```powershell
pwsh -NoProfile -File scripts\build.ps1                 # 构建 + 生成 .qlplugin
pwsh -NoProfile -File scripts\build.ps1 -Deploy        # 额外复制到 UserData
pwsh -NoProfile -File scripts\build.ps1 -DisableHelixViewer -Deploy
pwsh -NoProfile -File scripts\build.ps1 -RollbackHelixViewer   # 回滚旧插件
```

产物：

- `artefacts\package\` — 平铺的插件目录
- `artefacts\QuickLook.Plugin.ThreeDViewer.qlplugin` — zip 根目录平铺，可由 QuickLook 直接安装

`.qlplugin` 内容（根目录平铺，仅 `x64/`、`x86/` 为子目录）：

```
QuickLook.Plugin.Metadata.config
QuickLook.Plugin.ThreeDViewer.dll
AssimpNet.dll
HelixToolkit.Wpf.dll
warmup.glb
x64/assimp.dll
x86/assimp.dll
```

## 部署 / 验证

1. 运行 `scripts\build.ps1 -DisableHelixViewer -Deploy`。
2. 重启 QuickLook（插件只在进程启动时加载一次）。
3. 空格预览 `test\models\` 里的任意文件。

调试日志：在插件目录放一个空文件 `ThreeDViewer.debug`（或设置环境变量
`QL3D_DEBUG`），日志写到同目录 `ThreeDViewer.log`。

## 测试

`test\models\` 内置 23 个覆盖性样本，全部经过宿主模拟器与真实 QuickLook
环境（命名管道触发）验证：

| 样本 | 验证点 |
|---|---|
| `sample.*`（11 种格式各一） | 各格式导入通路 |
| `rich.glb` | PBR 因子（金属/粗糙）、4 贴图槽、双动画、内嵌贴图 |
| `bones.glb` | 骨骼蒙皮（12 bones）+ 骨骼动画 |
| `multi-anim.glb` / `hierarchy.glb` | 动画通道 / 多级节点变换传播 |
| `glass-glow.glb` | alpha 混合与自发光 |
| `gltf-with-textures\`、`obj-with-mtl\` | 外部 .bin/.png/.mtl 依赖解析 |
| `big.ply`（33 万面） | 大模型不触发降级 |
| `huge.ply`、`heavy.stl`（>50 万面） | 降级抽样 ≤12 万面 + 提示条 |
| `twomat.obj`、`scene.3ds` | 多材质槽 |

已知正确行为：`.mtl` 等附属文件不是模型，由 QuickLook 的文本预览接管。
损坏的模型文件会进入插件的错误面板，不会崩溃宿主。

## 发布流程（QuickLook 官方约定）

1. 构建 Release 并运行 `scripts\build.ps1` 得到 `.qlplugin`。
2. GitHub Releases 上传 `.qlplugin`。
3. 在 [QuickLook wiki 的 Available Plugins 页](https://github.com/QL-Win/QuickLook/wiki/Available-Plugins)
   添加一行（Plugin 名 + 版本 + 日期 + Releases 链接 + 描述）。


## 已知限制

- glb/gltf 的 PBR 为固定管线近似，不是物理正确的 IBL/metalness 渲染；
  法线贴图、遮挡贴图、morph target、多动画混合未呈现。
- Assimp 5.0.0-beta1 为 beta，个别格式的导入/导出存在原生崩溃风险；
  已通过限定后处理标志与避开 `EmbeddedTexture.Has*Data` 的已知 null 解引用 bug 规避。
- 大模型（>50 万面）的 *导入* 仍需数百毫秒，但 `View()` 立即返回，界面不阻塞。
