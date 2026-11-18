# 不依赖 Unreal 的 .uasset 导入

2026-11-14：本地 CUE4Parse 转换后端已接入 App 与 Limbo Console。导入不需要本机 Unreal、Blender 或 .NET SDK，不上传素材。FBX **导出**继续使用现有 Blender 管线。

## 使用

在「导入」中选择 `.uasset / ZIP` 文件类型，选择原始 ZIP 即可。也可以把 ZIP、单个 `.uasset` 或整个文件夹拖入窗口。「导入文件夹」现在自动发现目录中的 uasset，支持与 GLB、FBX 等文件混合；专用「导入 .uasset 文件夹」入口继续保留。扫描会把包含 uasset 的输入目录作为一个转换任务，保留 Skeleton 依赖的目录结构。

导入开始时打开任务面板，完成后显示结果窗口：新增、重复、不支持及失败任务数，按原因归类的提示，以及完整报告位置。重复文件明确提示已存在；空目录和损坏文件明确报错。搜索或筛选不会被导入自动清空，结果窗口会提示新资产可能被当前条件隐藏。

Limbo Console（F12）：

```text
uasset status
uasset inspect "C:/Users/Reed/Downloads/Movement Animset Pro.zip"
uasset import "C:/Users/Reed/Downloads/Movement Animset Pro.zip"
uasset import "D:/MyAnimations"
batch import "C:/Users/Reed/Downloads/Movement Animset Pro.zip"
job status last
job report last
job cancel
job retry last
```

`inspect` 只解析并生成兼容性报告，不入库。转换在独立子进程中运行，支持取消；成功项按产物 SHA-256 去重。混合批次中失败不会中止后续输入，重试只选择失败 / 中断项。单个 ZIP 作为一个容器任务，详细报告列出内部每一个资产。

## 已验证范围

| 输入 / 能力 | 当前行为 |
| --- | --- |
| UE 4.23 编辑器 AnimSequence 原始轨道 | 识别 UE4 Mannequin；386 段 Movement 动画已与 UE FBX 对比 |
| UE 4.27 编辑器 AnimSequence 原始轨道 | Dodge_Animations 的 58 段序列全部入库；19,720 个骨骼局部变换采样与独立读取的源数据对照通过 |
| UE 5.0 编辑器 AnimDataModel | Nodachi 全部 740 段通过源关键帧与 App 核对；详见 [NODACHI_IMPORT.md](NODACHI_IMPORT.md) |
| UE 5.6 编辑器 AnimationSequencerDataModel | 读取单轨、单区段、Direct FKControlRig 的线性源变换通道；Manny Idle 已与 UE FBX 对比 |
| 附带 Skeleton | 按骨骼名称、父子层级与绑定姿态识别 UE4 / UE5 标准家族；无固定资源路径或轨道顺序要求 |
| 附加辅助骨骼、部分动画轨道 | 有完整且兼容的 Skeleton 时保留额外骨骼；未动画骨骼保持绑定姿态 |
| 缺少 Skeleton | 仅在标准 Skeleton 名称及完整标准轨道集合匹配时替代，逐项记录 `dependencySubstituted=true`；不以部分同名骨骼猜测 |
| UE5 Skeleton 与网格 | 官方 Skeleton 有 161 根骨骼，Simple 预览网格有 89 个关节；输出保留 161 根，不以网格关节数截断 Skeleton |
| InPlace / RootMotion / 本包 AimOffset 源姿势 | 保留源骨骼变换、时长、比例和根位移；记录 additive 类型，不执行混合 |
| 其他 UE 版本 / DataModel / 自定义 ControlRig | 未验证，明确返回 unsupported；安装于 UE5.8 目录的样本实际保存版本为 UE5.6，不能据此宣称支持 UE5.8 资产 |
| 自定义比例或层级、Montage、cooked / 加密包 | 不支持；自定义比例需要单独的重定向流程 |
| ForceRootLock、非线性插值、区段裁切 / 混合 | 不执行这些语义，不作为普通序列静默导入 |
| 曲线、通知、事件、Montage 时间线 | 当前只导入骨骼源变换，不导入这些运行语义 |

识别分开检查引擎版本、动画编码和骨架兼容性。“采用 UE5 骨架”不能保证 `.uasset` 可解析；“文件可以解析”也不能保证能直接驱动标准预览网格。Quinn 等兼容网格可共享骨架，但当前 UE5 预览统一使用 Manny；不重建原材质、角色网格或面部系统。

Skeleton 依赖优先按 `/Game/...` 对应相对路径查找，再尝试唯一同名文件；多个同名候选会拒绝，避免误配。单文件输入不扫描任意外部目录，推荐选择包含依赖的目录 / ZIP。源 Skeleton 的额外骨骼写入 GLB skin，动画按名称映射；原预览关节索引不变。

这不是通用 Unreal 项目导入器。原始 ZIP 不修改，也不包含在 App 发布包内。

## UE5 真实素材对照

使用官方 `MM_Idle.uasset`（SavedByEngine 5.6.0-42730900，SHA-256 `11ee7fdeb84db89c1a66785b43b9bfc1415782122b8ac229517f4c5125d9cb0d`）和 `SK_Mannequin.uasset`。源动画没有旧式 RawAnimationData，读取其 Sequencer DataModel 中 89 组 FK 变换通道，228 帧 / 30 FPS / 7.566667 秒。

对照此前保存的 `BuiltinAssets/Baselines/UE5_Manny_Idle.fbx`，通过 App 实际加载器比较每个参考关键帧的 89 根对应骨骼，共 **20,470 次骨骼采样**：

- 世界位置最大差 **0.000002269 米**，旋转最大差 **0.055953°**，时长差约 **0.000000064 秒**；按名称核对层级。
- 网格绑定姿态单独与 `UE5_Manny.fbx` 比较，位置最大差 **0.000000169 米**。动画 FBX 自带静态参考姿态与网格有约 0.0537 米差异，因此动作和网格绑定使用各自对应参考，不放宽阈值。
- 输出保留 **161 根骨骼**，App 骨骼树与轨迹可访问；通过现有 Blender 队列导出 FBX 后仍为 161 根，四个时刻的全部骨骼最大位置差 **0.0000059 米**、旋转差 **0.0560°**。
- 15 项混合导入集成检查、22 项骨架识别 / 乱序 / 部分轨道 / 附加骨骼 / 错配拒绝检查通过。集成检查使用实际 Limbo 命令与 App 目录导入处理器。

本轮没有启动 Unreal。动画 FBX 参考是此前已有文件；CUE4Parse 导入路径只读取素材，独立 helper 及便携包均不链接 UE 编辑器。额外探测的 UE5.6 Death / Reload 可解析转换，但尚无对应 UE FBX 数值参考；带 ForceRootLock 的 Jump 明确跳过，不计入上述验收。

报告：[UE5 对照](Validation/CUE4Parse-UE5-Manny-comparison.json)、[混合导入检查](Validation/CUE4Parse-UE5-integration.json)、[骨架回归](Validation/CUE4Parse-standard-skeleton-tests.json)。

## 与 UE 对照的结果

对照此前 UE 5.8.4 转换得到的 386 个 FBX。CUE4Parse 直接读取原始 UE 4.23 资产，没有在本轮转换中启动 Unreal。

- 392 个输入：386 个成功转换、6 个 Montage 不支持、0 个解析失败。
- 在 App 的真实 FBX / GLB 加载路径中，对每个参考关键帧的全部 68 根骨骼比较世界位置和旋转，共 **5,148,620 次骨骼采样**。
- 骨骼名称 / 顺序 / 父节点一致；静止姿势世界位置最大差 **0.000000378 米**。
- 动画世界位置最大差 **0.001181 米**，世界旋转最大差 **0.111906°**。验收阈值为 0.003 米 / 0.5°；386 段全部通过。
- RootMotion WalkFwdLoop 总位移约 **1.741695 米**，对应 InPlace 位移近零。
- AimOffset 源姿势逐一通过；这不代表完整 AimOffset 系统已实现。

逐片报告：[CUE4Parse-Movement-comparison.json](Validation/CUE4Parse-Movement-comparison.json)。验证还覆盖原始 ZIP 经实际 Limbo 命令入库、全包重复导入、损坏输入后继续、失败项重试、GUI 导入处理器。端到端记录位于测试库的 `uasset-tests.json`。

Windows 打包程序另行通过 **22 项专项检查**（含运行中取消、ZIP 路径校验和 CUE → FBX 根运动往返）；原有 **164 项回归**与 **6 项打包冒烟检查**通过。打包版生成的 386 个 GLB 与逐骨骼验证使用的产物 SHA-256 全部一致。记录：[CUE4Parse-release-tests.json](Validation/CUE4Parse-release-tests.json)。本机可双击根目录 `OpenMovementAnimsetCUE.cmd` 打开已导入的独立测试库，不影响原 UE-FBX 测试库。

发现并修复了预览降采样：Godot 默认以 30 FPS 创建 GLB 动画，会破坏快速动作。转换产物的 `extras.animanagerSourceFps` 保存原采样率，App 据此创建动画；本包最高约 297.7 FPS，边界上限 600 FPS。

## 模块与数据

`Tools/CueImporter` 是独立 .NET 10 CLI，通过 JSON 报告和 GLB 与 .NET 8 Godot App 通信。CUE4Parse 固定于官方提交 `d05e5859ad240be10c9d5c8344f5de7c072123d0`。CUE4Parse 负责包、原始轨道及 MovieScene 属性解析，Animanager 负责受限 FK 通道求值、骨架识别和 GLB 写入；没有链接 UE 编辑器，也没有调用 UE 转换服务。

本实现保留 UE FBX 基准的骨骼局部轴，执行 Y 轴镜像及厘米到米转换；父节点负责 Z-up 到 Y-up。直接导入原始局部平移 / 旋转 / 缩放，匹配 UE 的源动画导出，不施加 Skeleton 的运行时平移重定向。

库内 `uasset/<任务目录>/conversion-report.json` 保存源 SHA-256、解析器版本、源骨架、识别出的 profile、动画编码、Skeleton 来源 / 哈希 / 替代标记、完整骨骼数及预览关节数、逐项状态、时长、采样数及输出哈希；`import-report.json` 映射到实际库内资产 ID / 路径。成功复制入库后删除临时 GLB，转换报告里的临时输出路径只用于追溯。原始素材保留在用户输入位置。

ZIP 在内存中逐项读取而不解压到任意路径，拒绝 `..` / 绝对路径，限制单资产 64 MiB、ZIP 总展开数据 2 GiB / 10,000 项。转换进程有 10 分钟超时，取消会终止进程树。异常 / 不支持的输入不会作为空动画入库。

## 构建与复现

开发构建需要 .NET 10 SDK（CLI 使用独立 `global.json`），App 仍使用 .NET 8：

```powershell
.\Tools\Build-CueImporter.ps1
dotnet build
& $env:GODOT_MONO_BIN --headless --path . -- `
  '--uasset-test=C:/Users/Reed/Downloads/Movement Animset Pro.zip' `
  '--data-dir=D:/_Projects/Animanager/.runtime/new-cue-test-library'
```

测试请选择新库。`Build.ps1` 将自包含 helper、profile、基准 GLB、上游 LICENSE / NOTICE 和 Epic 来源文件一并放入 `Importers/Cue4Parse`。保留该目录即可在无 Unreal 的机器运行。首次开发构建会下载固定提交及 NuGet 依赖；最终产物使用已修复的 Microsoft.Bcl.Memory 10.0.4。

源基准 GLB 由已有官方 `BuiltinAssets/Baselines/UE4_Mannequin.fbx` / `UE5_Manny.fbx` 经 Godot 导出，几何与骨架未手工重做。遵循原有 Epic 素材归属，不把它声明为 Animanager 原创资产。

技术依据：[CUE4Parse 官方代码](https://github.com/FabianFG/CUE4Parse/tree/d05e5859ad240be10c9d5c8344f5de7c072123d0)、[Godot GLTFDocument](https://docs.godotengine.org/en/stable/classes/class_gltfdocument.html)。

### UE4 / UE5 混合集成测试

准备一个目录，包含 `UE4_WalkFwdLoop.uasset`（Movement 的 RootMotion/WalkFwdLoop）、官方 `MM_Idle.uasset` 与其 `SK_Mannequin.uasset`，然后：

```powershell
& $env:GODOT_MONO_BIN --headless --path . -- `
  '--standard-uasset-test=D:/MyMixedFixture' `
  '--data-dir=D:/_Projects/Animanager/.runtime/new-standard-test-library'
```

逐帧对比仍使用 `--cue-compare=<转换输出目录>`，可放入 `reference-map.json`（转换报告 name → FBX 绝对路径）及 `bind-reference-map.json`（name → 网格 FBX 绝对路径）。省略映射时使用原 Movement 参考目录。helper 的 `--self-test <新空目录/results.json>` 执行骨架映射边界检查。

Windows 便携版 `Animanager-win-x64-20261114-122753` 也通过 15 项混合集成检查与 6 项打包冒烟检查；最终 helper 的两个代表产物 SHA-256 与逐帧对照产物完全一致。见 [便携版检查](Validation/CUE4Parse-UE5-release-tests.json) 和 [哈希核对](Validation/CUE4Parse-v2-release-hashes.json)。本机双击根目录 `OpenUEStandardCUE.cmd` 可打开新样例库（UE4 行走 + UE5 Manny Idle），保留原 Movement 库。

UE5.0 补充：Nodachi 真实包已验证，包含 162 骨骼及额外武器骨骼。CUE4Parse 固定提交上附加 `Tools/patches/CUE4Parse-ue50-raw-track.patch`，完整结果与边界见 [Nodachi 测试](NODACHI_IMPORT.md)。
