# Nodachi 导入测试

2026-11-14，原始 RAR 在工作区解压后通过 App 的实际 Limbo `uasset import` 队列入库，全程未启动 Unreal。

- 输入：`C:/Users/Reed/Downloads/Nodachi.rar`，约 377.4 MiB；817 个 .uasset、1 个演示地图。
- 实际保存版本：UE **5.0.3-20979098**。
- **740 / 740 个动画序列全部导入**；75 个非动画资源跳过，两份 Skeleton 作为依赖识别，地图未导入。
- 全部动画使用包内 UE5 Skeleton，**162 根骨骼**，含 `Nodachi_Weapon_R`。737 段有 90 条骨骼轨道，3 段有 89 条；保留无动画骨骼的绑定姿态。
- 使用官方 Manny Simple 预览网格。未导入原武器模型、材质、角色控制器、地图、事件 / 曲线或重定向系统。

## 验证

逐段通过 App 加载器检查网格、完整骨架、片段时长，并在每段的起点、四分点、中点、四分之三点及终点（短片去重）比较原 DataModel 局部关键帧，共 **329,745 次骨骼采样**。

最大位置差 **0.000008091 米**、旋转差 **0.055953°**、比例差 **0.000000458**。740 段全部通过。最大根位移约 **18.293 米**。这些是原始源姿势 / 根轨道，不代表已实现 UE 运行时 Root Motion 提取。

整包重复导入：**740 个重复跳过，0 个新增**。额外对待机、连招 01 和位移最大的连招 03 做 FBX 导出 / 重载，各保留 162 根骨骼；三个时刻的全骨骼对比通过 0.003 米 / 0.5° 阈值。连招 01 最大误差为约 0.0008001 米 / 0.2238°；另外两段位置差低于 0.000009 米。

实际图形模式播放并检查待机、攻击、翻滚，截图在样例库 `pack-validation/`。原有 18 项 FBX / Limbo 回归及 22 项骨架映射边界检查通过。UE4.23 / UE5.6 代表 GLB 与此前验证产物的哈希保持一致。

**对照边界：本包没有已有 UE 导出 FBX 参考。本次比较的是原始属性数组与 App 读取结果，并做了 FBX 往返和渲染检查；没有宣称与 UE 运行时或复杂动画蓝图完全等价。**

## 本次兼容改动

1. 增加 UE5.0 `AnimDataModel.BoneAnimationTracks` 读取。对固定 CUE4Parse 提交应用一个可复现的小补丁：在 UE5 自定义版本早于 `RawAnimSequenceTrackSerializer` 时，使用带标签的属性数组读取，保留后续版本原有 native bulk 路径。补丁随 helper 附带，构建检查可重复执行。
2. 18 个 Offset 姿势的时间轴声明 3 帧、实际数组有 2 份完全相同的值。只在每一个值严格相同时折叠为常量；不拉伸有运动的异常轨道，也不猜测帧率。
3. 修复 Blender 对 GLB 默认帧率采样造成的快速连招 FBX 误差。带 `animanagerSourceFps` 的资产按源采样率导入 / 烘焙，保存时长；保留既有无该元数据资产的行为。

RAR 当前由本次任务使用系统解压器解压；App 内仍选择解压目录或 ZIP / .uasset，尚未增加 RAR 拖入入口。

独立样例库：`Libraries/Nodachi-CUE4Parse`。根目录启动脚本 `OpenNodachiCUE.cmd` 直接打开该库与连招示例；F12 保留 Limbo Console。原 RAR 和旧样例库未修改，商业素材未放进应用发布包。

[摘要与原包哈希](Validation/Nodachi-summary.json) · [逐片结果](Validation/Nodachi-clip-results.json)

相关格式依据：[Epic FRawAnimSequenceTrack](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/Animation/FRawAnimSequenceTrack?application_version=5.5)、[固定 CUE4Parse 源码](https://github.com/FabianFG/CUE4Parse/tree/d05e5859ad240be10c9d5c8344f5de7c072123d0)。

发布验证：`Animanager-win-x64-20261114-131132` 通过 6 项独立打包检查，并在便携版中重做待机 / 连招 / 翻滚播放与截图。最终 helper 对四个代表素材生成的 GLB 哈希与整包验证产物一致，见 [发布核对](Validation/Nodachi-release.json)。
