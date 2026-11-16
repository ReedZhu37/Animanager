# .uasset 动画导入：独立模块方向

状态（2026-11-14 更新）：不依赖本机 Unreal 的 CUE4Parse 后端已通过 Movement Animset Pro 的 386 段动画对照验证，并接入 App / Limbo。使用方法、兼容边界与报告见 [CUE4PARSE_IMPORT.md](CUE4PARSE_IMPORT.md)。下文保留最初的架构方向及 UE 对照集制作方法；“下一阶段”条目属于原规划。

## 产品边界

Animanager 不应依赖 UE 编辑器 API 来实现资产列表、预览和任务 UI。UE 读取和转换放在独立进程，由输入清单与结果报告连接；App 只消费标准动画产物。现有桥接脚本位于 `Tools/`，未给 App 增加 UE 链接依赖。

用户目标是后续本机无需安装 Unreal。优先验证本地独立解析器；支持范围以实测的引擎版本、编辑器 / cooked 格式、动画压缩方式为准。可选的 UE 转换服务只作为另一种后端，不默认上传素材、不作为离线版本的必需条件。

## 可替换后端的契约（下一阶段）

输入：任务 ID、只读素材目录、原始包路径映射、引擎版本提示、资产选择、显式依赖映射、目标目录与取消信号。

输出：原始资产标识、类型、依赖状态、转换产物相对路径、SHA-256、骨架层级 / bind pose、动画时长 / 采样率、根运动、曲线与事件处理情况、逐项错误与日志。协议版本要独立于 UI 版本；缓存键包含源文件及依赖哈希、后端版本和转换参数。

首版中间结果使用 FBX + JSON 报告，复用当前入库与预览。未来独立解析器可直接生成 GLB / 内部动画数据，不必强制经过 UE 或 Blender。GUI 和 Limbo 调用同一个导入服务，后端由能力检查选择。

| 后端 | App 机器需要 UE | 定位 |
| --- | --- | --- |
| UE commandlet 独立进程 | 当前桥接需要 | 建立正确性基准，处理完整依赖的 AnimSequence |
| 本地独立解析器 | 不需要 | 后续首选离线方案；按兼容矩阵逐步支持 |
| 可选远程 worker | 不需要；转换机需要 | 另一种部署方式，另行实现任务传输与权限 |

## 不可省略的依赖和语义

- `.uasset` 是资产容器，可能是动画、Skeleton、Montage 或 BlendSpace，不能仅按扩展名认定为动画片段。
- 素材可能依赖 Skeleton、预览网格、基础姿势、其他动画以及旁文件。缺少依赖要报告；不可默默套一个骨架后宣称结果与原素材完全相同。
- 普通 AnimSequence 先行。Montage 的片段 / 槽 / 节奏、BlendSpace 的混合图、AimOffset 的 additive 语义需要单独定义。导出 AimOffsets 文件夹内的姿势序列，不等于支持完整 AimOffset 系统。
- Root Motion 应比较世界空间位移；不同 FBX 写入器可能把单位比例放在骨架父节点上，只比较骨架局部坐标会产生误判。
- 保留原文件和出处。转换失败不影响后续资产；已完成项可重用，重试不覆盖既有输出。

## 本次 Movement Animset Pro 验证准备

提供的 ZIP 含 392 个 `.uasset`：386 AnimSequence、6 AnimMontage。源包内部路径为 `/Game/Animation/EssesstailAnim`；引用 `/Game/Mannequin/Mesh/UE4_Mannequin_Skeleton`，但 ZIP 不包含骨架或网格。

本次只在隔离工程中，把该骨架路径显式映射到本机官方 UE4 模板骨架，使用官方 SK_Mannequin 预览，导出记录保留原资产路径和轨道列表。没有改写 ZIP；这是一项有明确基准替代的兼容测试，不代表已补回素材作者原项目中的全部依赖和设置。

转换脚本接受已经准备好依赖的隔离 `.uproject`：

```powershell
.\Tools\Convert-UassetAnimations.ps1 `
  -Project "隔离工程/Project.uproject" `
  -AssetRoot "/Game/Animation/EssesstailAnim" `
  -PreviewMesh "/Game/Mannequin/Character/Mesh/SK_Mannequin" `
  -Output "新的输出目录"
```

设置 `UNREAL_EDITOR_CMD` 或使用 `-Engine` 指定兼容的 UnrealEditor-Cmd.exe。成功后，用 App 的 `batch import "输出目录"` 导入。脚本不保存源 UE 资产；UE 本身仍会在临时工程写入缓存和日志。此工具不是任意 ZIP 的一键导入器，依赖准备目前仍在测试工具阶段。

## 验收独立解析器的依据

以本次 UE 导出作为对照集，检查每根骨骼的名称与父节点、bind pose、逐帧骨骼变换、单位 / 轴向、时长、根运动和静态姿势。不能仅以“成功读出对象名”或“生成 FBX 文件”判断兼容。先验证这批编辑器资产，再扩展其他 UE 版本和 cooked 素材。

可评估的上游：[CUE4Parse](https://github.com/FabianFG/CUE4Parse) 提供 UAnimSequence 读取及转换入口；[UAssetAPI](https://github.com/atenfyr/UAssetAPI) 提供资产容器和属性处理。两者都不能未经测试就视为覆盖本包全部语义。UE 基准使用 [FbxExportOption](https://dev.epicgames.com/documentation/unreal-engine/API/Editor/UnrealEd/UFbxExportOption) 的预览网格导出能力。
