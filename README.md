# Animanager 0.4 · 可拖拽工作区原型

基于 Godot 4.7.2 Mono / .NET 8 的本地 3D 资产管理原型。

P0 已接入多 Library 管理、资产整理和基础库维护。功能范围与验收入口见 [功能迭代计划](Docs/FUNCTIONAL_ROADMAP.md)。

Nodachi 已实现固定人体预设、独立武器轨道及握持 / 接触记录，操作与范围见 [独立轨道说明](Docs/MOTION_STORAGE.md)。完整后续设计见 [动画存储规划](Docs/ANIMATION_STORAGE_PLAN.md)；跨骨架 retarget、接触 IK 和完整旧库迁移尚未实施。

已加入独立 `.uasset / ZIP` 导入：无需安装 Unreal，使用 CUE4Parse 自动识别 UE4 / UE5 标准骨架。已验证 UE4.23 原始动画、UE5.0 AnimDataModel 与 UE5.6 Sequencer Direct FK 动画；Movement 的 386 段序列及官方 Manny Idle 已与已有 UE FBX 对照通过。UE5 保留完整 161 根骨骼；其他版本与编码尚未承诺兼容。见 [导入方法与兼容范围](Docs/CUE4PARSE_IMPORT.md)。

## 启动

直接从 Godot 工程按 F5 或运行 `Launch.cmd` 时，优先打开 `--data-dir` 指定的库，其次是最近有效的库。开发环境默认库由 `project.godot` 的 `animanager/development_library` 指向 `Libraries/Nodachi-Motion`，其中有 740 条 Nodachi 动画和 4 个基准资产。开发库缺失时回退到 `user://Library`，独立发行版也使用这个默认路径。`Libraries/.gdignore` 将本地素材库排除在工程资源和打包产物之外。

人体 / 武器分轨版本可双击 `OpenNodachiMotion.cmd`，打开新库的 740 段 Nodachi 动画并定位到连招 03 转刀片段。握持和接触操作见 [独立轨道说明](Docs/MOTION_STORAGE.md)。

Nodachi 的 740 段 UE5.0 动画已导入并验证，可双击 `OpenNodachiCUE.cmd`；详见 [Nodachi 测试](Docs/NODACHI_IMPORT.md)。

UE4 / UE5 独立导入样例可双击 `OpenUEStandardCUE.cmd`，打开已验证便携版及独立样例库。

本机双击 `Launch.cmd`。脚本构建 C# 工程后启动独立应用窗口。程序首次自动创建默认库时导入 UE4 Mannequin、UE5 Manny / Quinn（Simple）及 Manny Idle 基准资产。也可用 Godot Mono 打开 `project.godot` 后运行。

## 管理素材库

- 工具栏的 **Library** 显示活动库、登记路径和最近打开时间，可以新建空库、登记已有库、切换和重新定位。布局与显示偏好在应用内共享。导入或导出期间先等待任务结束，也可以在任务面板取消后重新切换。
- 资产列表用 Ctrl 增减选择，用 Shift 选择当前页连续范围。翻页和筛选保留选中 ID，数量旁会显示其他页或筛选外的选择。通过 **整理** 重命名、批量改标签和收藏、操作集合。右键点选中项可将选中资产移入回收站，也可在回收站右键恢复。恢复会带回原 ID 和展示配置。
- **Library** 提供完整性检查、备份恢复、索引重建和转换缓存预览。备份及恢复各使用一个尚不存在的目录。已有旧库首次打开时，会将健康 SQLite 内的元数据补录到持久清单。

运行 `Test.ps1 -Suite LibraryP0` 验证 P0，测试在隔离目录运行。本地有 Nodachi 和 Movement 两个样例库时也会执行完整切库、备份和恢复流程。命令行入口见 [Limbo Console](Docs/CONSOLE.md#素材库与资产整理)。

**0.4 已实现可拖拽工作区、统一控件、小窗口标签布局和 DPI 缩放。** 操作说明与当前边界见 [Docs/WORKSPACE.md](Docs/WORKSPACE.md)，原始路线图见 [Docs/INTERFACE_PLAN.md](Docs/INTERFACE_PLAN.md)。Limbo Console 完整保留：按 F12 或反引号打开，`app commands` 查看入口。完整命令样例见 [Docs/CONSOLE.md](Docs/CONSOLE.md)。

骨骼检查已接入可停靠 UI：“面板 → 骨骼”打开，“工作区 → 动画检查”并排查看骨骼树和预览。支持名称搜索、点击选中高亮、聚焦、轨迹开关、世界 / 参考骨骼空间、30 / 60 次每秒采样，并与 Console 双向同步。骨骼树现支持 Ctrl / Shift 多选，固定轨迹列表提供逐条显隐和移除，Root Motion 区域提供根骨骼选择及显示开关；与 C# API / Console 共用状态。操作见 [骨骼面板说明](Docs/WORKSPACE.md#骨骼与轨迹面板)，命令和兼容边界见 [骨骼与轨迹说明](Docs/CONSOLE.md#骨骼树多选与固定轨迹)。

独立 Windows 包解压后双击 `Animanager.exe`，保留同目录的 PCK、运行库和 BuiltinAssets；不需要用工程启动。FBX 导出需要单独安装 Blender，其他基础操作不需要。运行 `Build.ps1 -Zip` 生成带独立程序冒烟验证的便携包，详见 [打包说明](Docs/BUILD.md)。

新增批量工具：递归导入、JSON 文件清单、FBX 导出计划、全搜索结果 / 收藏批量导出、顺序任务队列、逐文件日志、取消和失败项重试。计划与报告保存在库目录的 `jobs/` 中，重启后仍可查询。批量导出当前输出 FBX。

```text
batch import_list "res://Examples/import-list.json"
job status last
```

导入完成后，按搜索条件生成并执行导出计划：

```text
export plan "UE5" "D:/_Projects/Animanager/Exports/UE5"
export show last
export run last
job status last
```

源码开发依赖：Godot **Mono** 4.7.2、.NET SDK 8.0.422（或同系列更新补丁）、NuGet Microsoft.Data.Sqlite 8.0.10。本机已有依赖缓存。其他开发机器需要还原 NuGet 包，并设置 `GODOT_MONO_BIN` 或调整启动脚本。

## 第一版可用流程

1. 点击导入文件 / 文件夹，或把文件 / 文件夹拖入窗口。递归扫描 GLB、glTF、FBX；后台复制入库，计算 SHA-256 去重。可取消，已完成的条目保留。
2. 在资产列表选择模型，使用右键旋转、中键平移、滚轮缩放、F 重置相机。
3. 动画支持播放 / 暂停、片段切换、循环、0.5–2 倍速、时间轴定位。独立 `.motion` 动画按源帧率步进，其他资产按 1/30 秒步进。空格播放 / 暂停，Ctrl+F 搜索。
4. 按名称或标签搜索，切换动画 / 模型 / 收藏分类。标签和收藏持久化到 SQLite；列表分页，每页 60 项。
5. 直接使用默认导出目录，或输入 / 浏览选择目录，再点击“导出”：支持 Godot `.res` AnimationLibrary（全部片段）、完整 `.glb` 和 Blender 后台 `.fbx`。同名文件自动编号，不覆盖已有文件。

## 数据与兼容范围

- 默认数据位置为 Godot `user://Library`，Windows 通常位于 `%APPDATA%\Godot\app_userdata\Animanager\Library`。界面左下角可打开实际目录。可用用户参数 `--data-dir=<绝对路径>` 指定独立库。
- 元数据只保存库内相对路径；原始资产不修改。SQLite 索引使用 FTS5，中文 / 子串搜索以参数化 LIKE 补充。
- glTF 的外部 buffers / images 会一起复制；依赖必须位于 glTF 所在目录内。缺失依赖或越界依赖会明确报错，推荐先打包为 GLB。
- FBX 使用 Godot 原生 FBXDocument 导入，Blender 5.2 后台导出。本次已验证标准人体的骨骼、动画及尺度往返；尚未在 UE 中回导验收，外部纹理打包和复杂资产兼容仍需后续测试。
- `.res` 保存源动画轨道，需要匹配的节点路径和骨架，不进行重定向。标准 baseline 来源与 SHA-256 见 `BuiltinAssets/Baselines/provenance.json`；StudioBot 只保留为小型确定性测试 fixture。
- 重定向 / BoneMap、专门面向 UE 的复杂异构骨骼烘焙、S3 / MinIO、批量缩略图尚未实现。独立 Windows 便携原型已加入构建流程；安装器、签名、自动更新与正式发布尚未实现。
- 文件扫描 / 复制 / 哈希在后台执行。Godot 场景解析当前仍在主线程，大型模型加载可能短暂停顿。当前按首个 AnimationPlayer 提取动画，复杂多动画播放器场景需后续扩展。
- 启用 Compatibility 渲染器和低处理器占用模式；预览暂停时仅在视角、尺寸或姿态变化后请求重绘。实际占用依赖模型与驱动，尚未完成性能基准测试。

## 验证与开发

2026-11-14 回归：构建零警告、零错误；26 项原型检查、18 项实际 Limbo / Blender / FBX 检查、28 项批处理检查、37 项界面检查全部通过，共 109 项。界面检查覆盖布局归一化、保存恢复、停靠时保留场景、720×400 小窗口、125%–200% 缩放、Console 焦点恢复。真实窗口另行检查鼠标拖拽和字体渲染。UE4 baseline 为 68 骨骼，UE5 Simple 为 89 骨骼；FBX 往返保留 root / IK 骨骼、动画与尺度。

多轨迹 API 回归（2026-11-14）：构建零警告、零错误；新增 63 项多选 / 固定轨迹 / Root Motion 专项检查，以及 55 项骨骼、42 项骨骼 UI、26 项原型回归全部通过。另已运行图形模式并检查实际预览截图。覆盖独立参考空间、切换片段、取消过期任务、采样预算、根运动数值与 GLB 导出隔离。

多轨迹 UI 回归（2026-11-14）：新增 49 项交互检查通过，42 项既有骨骼面板和 63 项多轨迹 API 回归通过。已在实际 Windows 窗口验证 Ctrl / Shift 多选、固定列表显隐 / 移除、根骨骼选择、Root Motion 开关、停靠、720×400 和 200% 缩放，并检查并排预览截图。

运行 `Test.ps1` 验证基础流程，`Test.ps1 -Suite All` 运行全部八组测试，`-Suite Interface` 单独验证工作区，`-Suite Bones` 验证骨骼与轨迹，`-Suite BoneUI` 验证骨骼面板交互，`-Suite MultiBones` 验证多选、固定轨迹与 Root Motion API，`-Suite MultiBoneUI` 验证对应面板交互。管线与批处理测试需要 Blender。或构建后运行：

```powershell
& $env:GODOT_MONO_BIN --headless --path . -- --self-test --data-dir=D:/_Projects/Animanager/.runtime/test-startup
```

测试在 `TestResults/run-<时间>/` 创建隔离数据库、资产与导出文件，报告写入 `results.txt`。示例可用 `python Tools/generate_samples.py` 重建，不依赖外部素材。

结构：`Core/Models` 数据模型、`Core/Storage` 存储与索引、`Core/Pipeline` 导入导出、`Core/Jobs` 计划与任务队列、`Core/Console` Limbo 命令、`UI` 界面与预览、`Tests` 集成验证。

实现参考：[Godot GLTFDocument](https://docs.godotengine.org/en/stable/classes/class_gltfdocument.html)、[Godot FBXDocument](https://docs.godotengine.org/en/stable/classes/class_fbxdocument.html)。
