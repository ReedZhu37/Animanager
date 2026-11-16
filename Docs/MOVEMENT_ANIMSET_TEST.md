# Movement Animset Pro 导入测试

日期：2026-11-14。输入：`C:/Users/Reed/Downloads/Movement Animset Pro.zip`，原 ZIP 未修改。

后续更新：CUE4Parse 独立后端也已完成同一套素材的 386 段对照验证，并接入直接 ZIP / .uasset 导入。无需本机 Unreal；详见 [CUE4PARSE_IMPORT.md](CUE4PARSE_IMPORT.md)。本文以下记录最初建立 UE 参考集的过程。

## 结果

| 资产组 | ZIP 内数量 | 成功转换并导入 | 单独列为未支持 |
| --- | ---: | ---: | ---: |
| AimOffsets | 9 | 9 | 0 |
| InPlace | 190 | 188 | 2 Montage |
| RootMotion | 193 | 189 | 4 Montage |
| 合计 | 392 | **386** | **6 Montage** |

386 个 FBX 全部经 Animanager 的真实 Limbo `batch import` 队列入库，逐个通过实际 `preview load` 解码：全部有 1 个网格、68 根骨骼和动画片段，导入后的时长与源动画匹配。15 项流程检查通过。App 全量验证约 78 秒，不含 UE 启动及转换时间；不是通用性能基准。

- 重复导入：386 项全部按内容哈希跳过，没有新增副本。
- 名称按 `InPlace__名称` / `RootMotion__名称` / `AimOffsets__名称` 保留分组，避免两组同名动画混淆；附加可搜索分组标签。
- 原地 WalkFwdLoop 根位移 0；RootMotion WalkFwdLoop 一个周期约 1.741695 米。
- 原地行走、根运动行走和上望姿势分别经 App 的 Blender FBX 导出队列回读，骨架数量与时长保留。根运动导出后约 1.7416949 米。
- AimOffsets 中 8 个方向样本是单帧静态姿势，静止为正常结果。姿势序列可预览，不代表 App 已支持 AimOffset 混合或 additive 运行语义。
- 实际 Windows 窗口已验证搜索 `WalkFwdLoop` 返回 8 项、点击加载、显示 68 骨骼、1 秒时长并正常播放；Limbo Console 保留。

## 依赖处理和边界

ZIP 全部为 `.uasset`，没有 FBX、Skeleton 或预览网格。素材内部引用 `/Game/Mannequin/Mesh/UE4_Mannequin_Skeleton`。本次在隔离临时 UE 工程里显式映射到官方 UE4 模板骨架，使用官方 SK_Mannequin 预览并转出 FBX。没有对用户原包进行改写，也没有执行重定向到 UE5 Manny。

6 个 Montage 未转换：`InPlace/Fists_Hit_Left_Montage`、`InPlace/Fists_Hit_Right_Montage`、`RootMotion/Build_Montage`、`RootMotion/Climb1m_Montage`、`RootMotion/Climb2m_Montage`、`RootMotion/Vault1m_Montage`。其编排、槽、通知和混合行为不能以普通 FBX 动画等同表达。本次没有声称完整恢复原 UE 工程行为或全部动画曲线 / 事件。

本参考集使用本机 UE 5.8.4 制作；随后独立 CUE4Parse 后端已通过与该参考集的对照，不再需要本机 UE。架构见 [UASSET_IMPORT.md](UASSET_IMPORT.md)。

## 打开与复现

双击工程根目录 `OpenMovementAnimset.cmd` 可用已打包的 Animanager 打开这次素材库；再次打开前先关闭正在访问同一库的测试窗口。此次已经在应用窗口中打开并播放行走样本。

库目录：`Libraries/MovementAnimsetPro`。首次普通启动额外加入 4 个官方基准资产，因此界面总数为 **390**；其中 386 个属于本次动画包。

完整来源 / 输出哈希及转换结果：`Libraries/MovementAnimsetPro/source-conversion-report.json`。逐个片段的验证结果：`Libraries/MovementAnimsetPro/pack-validation/results.json`；代表性回读文件在 `pack-validation/roundtrip/`。这些用户素材留在本机库，未加入应用发行 ZIP。

可复用 UE 参考转换入口：`Tools/Convert-UassetAnimations.ps1` 与 `Tools/export_uasset_animations.py`。参考集验证入口：构建后以 `-- --asset-pack-test=<包含 conversion-report.json 的 FBX 目录> --data-dir=<测试库>` 运行 Godot Mono 项目。该旧工具要求已准备依赖的隔离工程；新的独立导入入口见 CUE4Parse 文档。
