# Animanager hacking API · v0.4

工作区与 Console 共用功能 API。按 **F12** 或反引号打开实际的 **Limbo Console 0.8.0**，支持命令帮助、Tab 补全与历史。打开后键盘输入由 Console 接收，关闭后恢复原控件焦点。插件来源：本机已有的 upstream 插件副本；[上游仓库](https://github.com/limbonaut/limbo_console)，MIT 许可位于 `addons/limbo_console/LICENSE.md`。未复制其社区 C# wrapper，直接使用 Godot Callable 注册带类型的 C# 方法。

## 素材库与资产整理

```text
library current
library list
library create "D:/AnimationLibraries/Study" "动作练习"
library register "D:/AnimationLibraries/Movement"
library open "D:/AnimationLibraries/Movement"
library relocate <库ID> "D:/AnimationLibraries/MovementMoved"
library check
library backup "D:/Backups/Movement-2026-11-16"
library restore "D:/Backups/Movement-2026-11-16" "D:/AnimationLibraries/MovementRestored"
library rebuild "D:/AnimationLibraries/MovementRestored"
library cache_preview
library cache_clear true
```

`library open` 接受路径或登记表里的库 ID。路径失效时用 `library relocate` 更新登记。备份和恢复的目标需要是尚不存在的目录，恢复验证通过后会登记并切换到新库。缓存清理使用当前会话最后一次预览的范围，执行时再次验证候选文件。

```text
asset search "walk"
asset rename <资产ID> "步行循环"
asset tags_add <ID1,ID2> "步行 humanoid"
asset tags_remove <ID1,ID2> "humanoid"
asset favorites <ID1,ID2> true
collection create "步行动作"
collection list
collection add <集合ID> <ID1,ID2>
collection search <集合ID> *
collection remove <集合ID> <ID2>
collection rename <集合ID> "移动动作"
collection delete <集合ID>
asset trash <ID1,ID2>
asset trash_list *
asset restore <ID1>
asset purge <ID2> true
```

批量参数使用逗号分隔的完整资产 ID。永久删除只处理回收站里的资产，结果会列出保留的文件及原因。

```text
app commands
app status
baseline import
asset list 0
asset search "Manny"
asset inspect UE5_Manny_Idle
preview load UE5_Manny_Idle
preview play
preview pause
preview seek 0.5
preview speed 0.5
preview bones
preview material clay
asset tags UE5_Manny "baseline humanoid"
asset favorite UE5_Manny true
export fbx UE5_Manny_Idle "D:/_Projects/Animanager/TestResults/ManualExport"
export scene "D:/_Projects/Animanager/TestResults/ManualExport"
export animations "D:/_Projects/Animanager/TestResults/ManualExport"
```

路径使用正斜杠，有空格时加双引号。资产参数为**完整 ID 或精确且唯一的名称**；同名冲突时使用 `asset search` 返回的 ID。`preview clip "Unreal Take"` 切换动画。`help export fbx` 查看参数。

预览默认采用灰模以便检查蒙皮。`preview material original` 查看源材质，`preview material clay` 恢复灰模；该开关不会改变导出材质。

## Nodachi 独立武器轨道

新导入的 Nodachi 使用固定人体预设与独立道具轨道，共享时间轴。`motion inspect <asset>` 查看来源、预设哈希和握持 / 接触状态；`motion grip <asset> <x,y,z> <degrees_x,y,z>` 保存模型偏移；`motion contact <asset> <start> <end> <unknown|right|left|both|free>` 记录接触区间。`motion sample <seconds>` 检查当前武器姿态，`motion visible <asset> <bool>` 保存显隐，`motion reset <asset>` 恢复展示默认值。

接触记录目前不执行 IK，握持默认未校准，不改变源转刀动作。完整保存方式、示例与验证边界见 [独立轨道说明](MOTION_STORAGE.md)。

## 骨骼树、多选与固定轨迹

也可通过“面板 → 骨骼”使用可停靠骨骼树，或选择“工作区 → 动画检查”；面板和 Console 共用同一份选择及轨迹设置，详见 [面板操作](WORKSPACE.md#骨骼与轨迹面板)。使用 Console 时先等待 `preview load` 打印 Loaded，再逐条执行后面的命令：

```text
preview load UE5_Manny_Idle
preview bones
bone tree foot
bone select foot_l
bone show true
bone xray true
bone info
trail show true
preview seek 0.5
preview play
```

`preview bones` 显示所有骨架及其骨骼树。`bone tree <query>` 不区分大小写搜索名称，保留匹配项的祖先路径；`bone tree "*"` 显示全部。输出中的 `0:12` 表示骨架 0 的骨骼 12。`bone select` 支持精确名称或这个编号；多个骨架中存在同名骨骼时必须使用编号。编号仅在当前资产加载期间有效。

| 命令 | 行为 |
| --- | --- |
| `bone select <name_or_id>` | 用一个骨骼替换当前选择；显示关节、父骨段和名称高亮 |
| `bone select_many "LeftFoot,RightFoot"` | 原子替换为多个骨骼，按逗号分隔，去重；任意参数无效则不改变选择 |
| `bone add/remove <name_or_id>` | 增加 / 移除一个骨骼，保留其他选择 |
| `bone selection` | 列出所有选中项；`>` 为主选骨骼，`+` 为其他选择 |
| `bone info` | 查看所属骨架、父骨骼、当前时间、相对父骨骼与世界空间的位置 / Euler XYZ 角度 |
| `bone focus` | 将相机中心移到选中骨骼，保留观察距离 |
| `bone show true/false` | 显示 / 隐藏整副骨架线；不清除当前选择 |
| `bone xray true/false` | 是否让骨骼、轨迹及标记穿透模型显示，默认开启 |
| `bone clear` | 清除选择、高亮及跟随选择的轨迹；固定轨迹和 Root Motion 保留 |
| `trail show true/false` | 显示 / 隐藏所有当前选中骨骼的轨迹；不影响固定轨迹 |
| `trail world` | 使用预览世界坐标，保留角色整体移动；默认模式 |
| `trail relative <name_or_id>` | 每个采样时刻乘以参考骨骼世界变换的逆，抵消其位移、旋转和缩放 |
| `trail rate 30/60` | 设置每秒采样数，默认 30；这不是源文件的帧率 |
| `trail status` | 查看计算状态、参考空间、片段、采样数、起终点时间与坐标 |
| `trail pin` | 固定全部当前选择，同时记录各自的参考骨骼和采样率；无需先开启 `trail show` |
| `trail pins` | 列出 pin ID、骨骼、参考空间、采样率和可见性 |
| `trail visible <pin_id> true/false` | 显示 / 隐藏一个固定轨迹，保留其配置 |
| `trail unpin <pin_id>` | 移除一个固定轨迹 |
| `trail clear_pins` | 移除全部固定轨迹；当前选择的轨迹和 Root Motion 不受影响 |

例如 `trail relative root` 查看相对根骨骼的运动；StudioBot 的根骨骼名为 `Hips`。参考骨骼必须明确指定，不按名字猜测。相对空间轨迹绘制在该参考骨骼的当前坐标系中，随其一起运动，白色当前标记仍与当前骨骼重合。轨迹起点为绿色、终点为红色，每个骨骼使用稳定的路径颜色；循环动画不额外连接终点与起点。起终点重合的原地循环动画可能显示重合标记。

固定的是骨骼与采样配置，切换选择不会移除固定轨迹，修改 `trail relative/world/rate` 也不会改变已有 pin。相同骨骼、参考空间和采样率重复固定会复用 pin；同一骨骼可以分别固定世界空间和相对空间轨迹。固定轨迹与选择轨迹配置完全相同时只绘制一条。因此隐藏 pin 后，如果该配置仍由 `trail show` 显示，它仍然可见。pin ID 在当前程序会话中不复用，切换资产后旧 ID 无效。

```text
preview load StudioBot_Walk
bone select_many "LeftFoot,RightFoot"
trail show true
trail pin
bone select LeftHand
trail pin
trail pins
bone clear
rootmotion source Hips
rootmotion show true
```

轨迹覆盖当前完整片段，包含精确终点。多骨骼共用一个隔离场景，在同一时刻求值后读取所需骨骼；采样分批执行，不暂停或跳转当前预览。完成后 Console 自动打印 Ready。切换片段保留多选、pin 配置和 Root Motion 来源，重新计算当前片段，**不保留旧片段的轨迹快照**。切换资产清空这些状态；停靠、最大化、相机操作和播放倍速不会丢失轨迹。每条上限 120,001 个采样点，活动集合与缓存上限为 1,000,000 个骨骼姿态样本；参考骨骼也占样本预算。显示点数另有 1,000,000 点总上限，避免共享姿态却生成过多参考空间路径。超限会显示 Unavailable；隐藏 / 移除部分 pin，或用较低采样率重新固定，即可重算。辅助几何不进入导出模型。

当前采样范围为导入的 `AnimationPlayer` 位置 / 旋转 / 缩放轨道及其父子变换，沿用预览的首个 AnimationPlayer。静态骨架可以选中但没有动画轨迹。脚本、物理、IK / SkeletonModifier 驱动、动画混合树以及多播放器组合求值不在本阶段范围内。尚未加入曲线图或骨骼编辑。骨骼面板已接入多选、固定轨迹列表和 Root Motion 控件，与 C# API / Console 共用同一状态；操作方式见 [骨骼面板说明](WORKSPACE.md#骨骼与轨迹面板)。

### Root Motion

| 命令 | 行为 |
| --- | --- |
| `rootmotion source <name_or_id>` | 明确指定当前骨架中的无父骨骼作为来源，不按名称自动猜测 |
| `rootmotion show true/false` | 显示 / 隐藏世界空间路径与朝向箭头，独立于骨骼选择和 pin |
| `rootmotion status` | 来源、采样状态、片段、终点位移与旋转增量 |
| `rootmotion sample <seconds>` | 查询片段内指定时刻，位置线性插值，旋转四元数球面插值 |

Root Motion 使用 `trail rate` 的采样率，始终显示世界空间路径，不受 `trail relative` 影响。它包含根骨骼及模型 / 骨架祖先节点的运动，保留完整 XYZ。`WorldPosition` / `WorldRotation` 为世界姿态；`Displacement` 为相对首帧原点、以首帧根骨骼朝向表达的位移（保留世界距离，不除以初始缩放）；`RotationDelta` 为相对首帧朝向的旋转四元数。旋转取世界变换基的正交化结果，退化或镜像的根变换会明确报错。纯旋转片段也会显示朝向箭头。

这是**显示和查询**接口，不修改源动画、预览播放或 Godot 的 RootMotionTrack；不会提取并应用角色移动、转换原地动画、投影到地面或跨循环累计位移。导出仍使用原模型动画。

### C# API

入口为 `Preview3DViewport.Bones`（`Core/Preview/BoneInspection.cs`），在 Godot 主线程调用：

```csharp
var bones = preview.Bones;
bones.SetSelection(["LeftFoot", "RightFoot"]); // 也可用 "0:12" 这样的 ID
bones.ShowTrail(true);
var pins = bones.PinSelected();
bones.SetRootMotionSource("Hips");
bones.ShowRootMotion(true);
await bones.PendingSampling; // 等待最后一次配置触发的计算
if (bones.Status != "Ready") throw new InvalidOperationException(bones.Status);
var tracks = bones.Trajectories;
var motion = bones.RootMotionAt(0.5);
bones.ClearSelection(); // 固定轨迹与 Root Motion 继续显示
bones.SetPinVisible(pins[0].Id, false);
```

`SelectedBones`、`Pins`、`Trajectories` 和 `RootMotion.Samples` 提供只读集合；`Selected` 与 `Trajectory` 保留为主选骨骼的兼容接口。`SetSelection(IEnumerable<string>)` 原子替换并去重，最后一个唯一项为主选骨骼；`AddSelection` / `RemoveSelection` 增删一个项，`Select` 替换为单选。`PinSelected` 返回 pin 描述，`Unpin` / `ClearPins` 移除配置。`Changed` 通知选择、配置、采样状态变化，`Notice` 提供可读报告。异步失败通过 `Status` 的 `Failed:` / `Unavailable:` 和通知报告；等待任务后应检查状态。设置变化会取消旧计算，过期结果不会覆盖新状态。

`Test.ps1 -Suite Bones` 运行骨骼与轨迹检查；`-Suite All` 包含该套测试。构建后使用 Godot Mono `--headless --path <项目目录> -- --bone-test --data-dir=<隔离目录>` 可单独执行；去掉 `--headless` 会额外保存实际预览截图。报告位于 `TestResults/bones-<时间>/`。

`Test.ps1 -Suite MultiBones` / `--multi-bone-test` 运行多选、固定轨迹和 Root Motion 专项检查，报告在 `TestResults/multi-bones-<时间>/`；图形模式另外保存实际预览截图。`-Suite MultiBoneUI` / `--multi-bone-ui-test` 另测对应 UI 交互，报告在 `TestResults/multi-bone-ui-<时间>/`。

`export fbx` 导出指定库资产的完整源动画，不受预览当前播放位置、倍速和循环设置影响。`export scene` / `export animations` 导出当前加载的预览场景。FBX 后台任务期间仍可操作控制台；`job cancel` 取消控制台导入或转换，完成的导入条目保留。输入非法路径、缺少 Blender 或转换失败会报错，不显示虚假成功。

设置 Blender：`pipeline blender "D:/Blender/5.2/blender.exe"`（本次会话），或启动前设置 `BLENDER_BIN`（后续启动生效）。本机默认发现 Blender 5.2。转换使用 Process.ArgumentList，不经过 shell 拼接；8 分钟超时，取消会终止进程树；产物完成后才移入输出目录，同名文件自动编号。

核心入口为 `Core/WorkspaceApi.cs`：搜索、定位资产、导入、标签、收藏、FBX 导出。UI 与 `Core/Console/ConsoleCommands.cs` 共用实例。预览控制目前仍由 Godot 视口提供，未来 UI 可替换，不必改存储与转换 API。

## FBX 导出工作流

先生成计划，核对资产范围，再执行：

```text
export plan "UE5" "D:/_Projects/Animanager/Exports/UE5"
export show last
export run last
job current
job status last
job report last
```

- `export plan <query> <folder>` 按名称 / 标签检索并保存完整资产 ID 快照，不受列表每页 60 项限制。`"*"` 代表全部资产。生成计划不会创建导出目录，也不会启动 Blender。无匹配项时报错。
- `export show <plan_id_or_last>` 显示前 20 项和计划 JSON 路径，完整范围在文件中。`export run` 读取保存的计划加入队列；之后库中新增的资产不会混入该计划。
- `export fbx <name_or_id> <folder>` 单文件导出也走同一队列。
- `batch export <query_or_*> <folder>` 直接生成并执行计划；`batch export_favorites <folder>` 批量导出当前收藏。
- 每个文件调用独立 Blender 后台进程，任务及文件顺序执行。一个文件失败后继续处理下一项。
- 使用目标磁盘上的 `.animanager-staging/<随机ID>/` 临时目录；进程退出、成功标记和二进制 FBX 头检查通过后才发布文件。同名文件自动加 `_2`、`_3`，已有文件不覆盖。正常完成 / 失败 / 取消后清理该临时目录，空的 `.animanager-staging` 根目录保留。
- 导出整份库资产，包含源动画；当前不提供裁剪片段、重采样率、轴向或仅动画等参数，批量 GLB / RES 导出尚未提供。

计划 / 任务 ID 为完整 32 字符 ID。`last` 在计划命令中指本会话最近使用的计划，在任务命令中指队列里最近创建的任务。重启后从保存文件取得计划 ID，通过 `job list` 取得任务 ID。

## 批量导入

```text
batch scan "D:/Assets/Characters"
batch run last
job status last
batch import "D:/Assets/Animations"
batch import_list "res://Examples/import-list.json"
```

`batch scan` 仅扫描、报告和保存导入计划；`batch run` 执行保存的导入或导出计划。`batch import` 一步完成递归扫描和入队，`asset import` 是同一入口。支持 `.fbx`、`.glb`、`.gltf`，其他附属文件计为 ignored；glTF 引用的依赖仍会随资产复制。显式指定的缺失文件、不支持格式和无法访问的目录记录为失败项。目录链接不遍历并报告，避免递归循环。

`batch import_list` 接受 JSON 字符串数组，可混合多个文件与文件夹，支持绝对路径或相对 **JSON 所在目录** 的路径。重叠路径去重，资产内容继续按 SHA-256 去重并报告 skipped。例如：

```json
[
  "../BuiltinAssets/Baselines",
  "../BuiltinAssets/Samples"
]
```

工作区提供可直接运行的 `Examples/import-list.json`。导入只负责入库，不会完整解码模型，因此坏 FBX / GLB 可能在预览或导出时才被发现。后续导出计划必须在导入完成后生成；Limbo 连续执行命令不会等待前一个异步任务完成。

## 任务、取消与重试

| 命令 | 行为 |
| --- | --- |
| `job current` | 当前活动任务和最近进度消息 |
| `job list` | 最近 20 个任务，含历史状态和 ID |
| `job status <id_or_last_or_current>` | 计数、报告路径、当前消息和前 10 项错误 |
| `job report <id_or_last>` | 完整 JSON，含每项状态、输出文件、错误和日志路径 |
| `job cancel` | 取消活动扫描 / 预览加载 / 队列任务；有扫描时优先取消扫描 |
| `job cancel_id <id_or_last_or_current>` | 取消指定排队或运行任务 |
| `job retry <id_or_last>` | 创建新任务，只处理失败、取消或中断项 |

成功 / skipped 项不会因 retry 再处理。取消运行任务会终止 Blender 进程树，保留已完成结果；取消排队任务不会创建导出文件。`Finished` 是已处理或取消的总数，需结合 `Succeeded` / `Failed` / `Cancelled` 判断结果。`CompletedWithErrors` 表示有文件失败。

计划位于 `<库目录>/jobs/plans/<plan_id>.json`，任务报告位于 `jobs/reports/<job_id>.json`，每项日志为 `<job_id>_<序号>.log`。导出成功项的 `Output` 是实际落盘文件路径。进度不写入 SQLite 资产表。

重启时，未结束的任务标记为 `Interrupted`，不会自动继续；用 `job retry <id>` 恢复未完成项。若程序被强制终止，可能留下临时目录；如果终止恰好发生在文件发布后、报告写入前，重试可能生成编号副本。先核对输出与报告，再决定是否重试。损坏的报告保留在磁盘，不阻止库启动。

## 独立 .uasset 导入

`uasset status` 查看后端状态，`uasset inspect "路径"` 检查 ZIP / .uasset / 目录，`uasset import "路径"` 批量入库。`batch import` 也支持 ZIP / .uasset 文件。自动识别 UE4 / UE5 标准骨架，无需 Unreal；支持 UE4.23 原始轨道、UE5.0 AnimDataModel、UE5.6 Sequencer Direct FK 源动画，建议把 Skeleton 依赖放在同一目录 / ZIP。检查输出列出各骨架家族数量及缺失 Skeleton 替代数；支持范围和验证报告见 [CUE4PARSE_IMPORT.md](CUE4PARSE_IMPORT.md)。`job status last` 显示新增 / 重复 / 不支持数，`job report last` 提供详细报告路径，现有取消和重试命令可复用。

## FBX 支持范围

- 原生 FBX 导入和预览；Blender FBX → FBX、GLB / glTF → FBX 导出。
- UE 标准人体的网格、骨架、IK 骨骼、Idle 动画与尺度已实测。
- Blender 把 UE `root` 表示为骨架对象时，导出流程恢复显式 root 骨骼并迁移动画变换；不会自动重定向到其他骨架。
- 本次验证为 Godot/Blender 往返；**尚未在 UE 中重新导入输出 FBX 做验收**。多骨架、多 action、复杂根运动、Morph、约束、材质纹理的全面兼容性仍需专项样例验证。
- 原始 FBX 外部纹理仍不自动收集入库。使用自包含贴图或在未来材质打包阶段处理；官方 baseline 使用中性材质。

## 验证

`Test.ps1 -Suite All` 执行原型、管线、批处理、界面、骨骼、骨骼 UI、多轨迹及多轨迹 UI 八套检查；`-Suite Pipeline` 为 18 项官方人体 / FBX 检查，`-Suite Batch` 为 28 项批处理检查。也可构建后运行：

```powershell
& $env:GODOT_MONO_BIN --headless --path . -- --pipeline-test --data-dir=D:/_Projects/Animanager/.runtime/pipeline-library
& $env:GODOT_MONO_BIN --headless --path . -- --batch-test --data-dir=D:/_Projects/Animanager/.runtime/batch-library
```

该测试从真正的 Limbo 文本命令开始，验证类型参数、引用路径、入库、控制骨骼动画和 Blender 导出；每次使用隔离数据库。报告在 `TestResults/pipeline-<时间>/results.txt`。目前为 18 项检查。测试使用官方 UE4（68 骨骼）及 UE5 Simple（89 骨骼）标准人体。

批处理报告在 `TestResults/batch-<时间>/results.txt`，覆盖中文路径、JSON 清单、递归扫描、缺失文件、去重、计划快照、73 项跨分页选择、顺序执行、失败继续、碰撞保护、日志、重试、重启恢复、实际进程取消、部分文件清理和 FBX 输出格式检查。每次测试使用独立库，不修改正常应用库。
