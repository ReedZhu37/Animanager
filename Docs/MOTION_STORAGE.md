# Nodachi 人体与武器独立轨道

2026-11-14。本轮实现 [动画存储规划](ANIMATION_STORAGE_PLAN.md) 的 Nodachi 首期：固定 UE5 人体、独立刚性武器轨道、握持配置和接触记录。标准 UE4 / UE5 跨骨架重定向现已接入，见 [Retarget](RETARGET.md)；IK 接触求解和自动识别松手区间尚未实现。

## 保存与播放

`uasset import` 识别到已验证的 Nodachi 骨架规则后，生成一个 `.motion.json` 资产。普通 UE4 / UE5 导入继续使用既有路径。

- 人体使用固定的 161 根 UE5 标准骨骼，`Nodachi_Weapon_R` 不进入人体 Skeleton。
- Manny Simple 的原 GLB 文件按哈希共享，文件字节不变。其顶点、索引、权重和 89 个 Skin 绑定不变；未加权的标准骨骼来自独立的固定 rig 定义。
- 人体、源动画、武器轨道分别保存在不含 Mesh 或 Skin 的 GLB。独立武器通过同一个 AnimationPlayer 与人体同步播放，附着于 `hand_r`。
- 源 Skeleton、AnimSequence 文件及解码源轨道一并归档，动画来源、引擎版本、导入器版本与原文件哈希写入清单。不读取或导入素材包的角色、武器 Mesh。
- 预览中的金色参考刀是 App 自有的简单诊断道具，用于观察运动。其形状与握柄位置不代表原包武器；初始握持基准为源武器骨骼原点，标记为未校准。

740 条动画共享同一份人物 Mesh 和标准 rig。737 条包含武器轨道，另外 3 条明确使用源参考姿势，不制造缺失的武器动作。

实际首期目录如下，继续使用现有 SQLite 资产列表与查询：

```text
Library/
  index.sqlite
  motion/
    <hash>.motion.json                 # 不可变的组合动画清单
    <hash>.motion.json.asset.json      # 旧库的资产元数据副本
    <hash>.motion.json.presentation.json
                                      # 当前展示配置
    objects/<hash>.<extension>         # 共享预设、rig、轨道及源文件
    presentations/<manifest-name>/
      <revision-id>.json               # 握持、接触、显隐修订历史
  uasset/<job-id>/                     # 转换与入库对照报告
  jobs/                               # 队列报告
```

所有必需对象使用相对路径、字节数和 SHA-256 校验。保存握持或接触配置时保留原始轨道、人物文件及展示修订历史。P0 库在根目录的 `asset-catalog.json` 保存资产元数据，可从该文件重建索引。搬迁或复制库可使用 Library 的完整备份和恢复。单资产 `.animpack` 和旧库全面迁移属于后续阶段。

## 握持与接触

握持配置是施加在独立武器轨道之后的模型偏移，包含米制位移和旋转。源武器运动保持不变。保存校正后标记为用户已校准，重置可恢复默认。

接触区间支持 `unknown`、`right`、`left`、`both`、`free`。区间连续覆盖片段，采用左闭右开，最后一段包含终点。修改区间时保留两侧原记录。

导入时全部接触区间为 `unknown`：当前 AnimSequence 数据没有明确的手部接触语义。骨骼挂在右手下面，只能确定坐标参考，不能证明刀一直握在右手。所有接触信息目前是 `observe-only`，用于检查与后续重定向；它不会触发 IK、锁住手或改变原始转刀。

## App 和 Limbo Console

从本工程按 F5 或运行 `Launch.cmd` 也会打开该库；在 App 资产列表搜索 `Nodachi` 可查看全部 740 条动画。工程里的 `Libraries/.gdignore` 用于排除 Godot 资源扫描和打包，因此这些库资产不出现在编辑器左侧文件系统面板。

双击工程根目录的 `OpenNodachiMotion.cmd` 可启动已验证的独立程序，打开 `Libraries/Nodachi-Motion` 并定位到连招 03 转刀片段。首次正常启动另加入 4 个官方基准资产，库内仍为 740 条 Nodachi 动画。

App 的 `.uasset 文件夹` 入口直接支持该流程，`.motion.json` 也可以从文件选择器或拖入导入。选择资产后自动组合人物和武器，详情中显示独立轨道、握持与接触状态。F12 保留 Limbo Console。

独立动画的单帧步进使用清单记录的源帧率，Nodachi 连招为 60 FPS；人体和武器始终同步定位。

```text
uasset import "D:/_Projects/Animanager/.runtime/nodachi/source"
asset search "Combo_Attack_03_All"
preview load <asset_id>
motion inspect <asset_id>
motion sample 2.5
motion grip <asset_id> 0.01,0,0 0,45,0
motion contact <asset_id> 0.5 1.0 right
motion visible <asset_id> false
motion reset <asset_id>
```

`motion grip` 的两组三元数分别为位移 XYZ（米）与 Euler XYZ（度，按 Godot 默认 YXZ 顺序转换成四元数）。例子只是操作示范，不是 Nodachi 的已知握持校准值。

`motion sample` 定位当前动画，返回武器世界位置、旋转、参考刀尖位置及该时刻接触状态。所有保存操作经 WorkspaceApi 执行；选中的资产会自动重新加载。`motion reset` 保留源数据和历史修订，只恢复展示默认值。

导出 GLB 时临时组合人物与独立道具节点；武器不会写入库内人物骨架。FBX 路径保留承载动画的 Empty 节点，将武器临时烘焙到场景空间，并归一化 Blender 对象坐标轴、补偿子模型，确保节点姿态与可见刀片同时保留。库内仍保存相对手部的原轨道。导出动画 `.res` 仍要求匹配的组合场景和节点路径，不能用作通用 retarget 文件。

## 验证范围

离线对照覆盖 740 条动画：人体每段五个时刻，武器每个源帧及相邻帧中点；同时检查武器原点和两个偏移参考点，共 815,428 次比较。与原有 GLB 及源快照比较，最大位置差约 0.000001925 米，最大旋转差约 0.07913 度，最大参考点差约 0.000003822 米。

App 整包通过原始 DataModel 的 329,745 次局部姿势采样，最大位置差约 0.000003293 米，旋转差约 0.055953 度。每条同时校验人体标准骨架一致、源武器骨骼未加入人体、原 Mesh 数组和 Skin 绑定不变。连招 01 在实际播放器中另做全部源帧及中点的武器世界姿态检查。

原始全包测试报告与后续导出复测报告分别保留，发布摘要记录最终结果。Nodachi 没有既有 UE 原生导出参考，因此这些检查不等同于 UE 运行时验证。整个导入、播放和测试过程不启动 Unreal。

最终全包复测通过 740 条动画与 21 项集成检查；整包再次导入为 0 新增、740 重复跳过。连招 03 的专门转刀检查同时验证 FBX 节点姿态与可见刀片几何，位置差低于 0.000001 米，几何角点差约 0.000001 米。现有 UE4 / UE5 标准导入的 15 项回归通过。详见 [验证摘要](Validation/Nodachi-motion-summary.json)。

最终独立包 `Animanager-win-x64-20261114-154509` 另通过 22 项转刀集成检查（包括混合目录保留普通 GLB）、6 项打包冒烟检查、正常启动及图形模式播放。截图保存在新库的 `motion-validation/spin-1.png` 至 `spin-3.png`。

规则当前只启用于完整、兼容的 UE5 标准骨架加 `Nodachi_Weapon_R`，且该骨骼父节点为 `hand_r` 的已验证情况。其他武器规则不自动猜测，仍由原导入流程处理。旧 `Nodachi-CUE4Parse` 样例库保持原状，新结果在 `Libraries/Nodachi-Motion`。
