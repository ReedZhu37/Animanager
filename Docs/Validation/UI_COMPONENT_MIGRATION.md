# 组件 UI 迁移验收

2026-11-17 · Windows · Godot 4.7.2 Mono (.NET)

后续布局修订见 [组件 UI 布局与停靠验收](UI_DOCKING_REVISION.md)。下文保留首次迁移的验收记录；最新默认布局为中央 viewport 与底部动画卡片，支持拖动、分隔条调整和布局保存。

默认 `UI/MainShell.tscn` 已使用 `UI/Shell` 组件。`MainShell` 继续管理库、导入导出、任务、角色与重定向；`ShellLayout` 提供固定布局、属性／骨骼／重定向页签和筛选／导出／任务抽屉。`ShellWindow.tscn` 是同一实现的开发入口，组件画廊与开发入口均从发布资源中排除。

网格和列表共享选择、范围锚点及卡片数据，分别恢复滚动位置。侧栏集合和标签来自当前库。排序在分页之前执行，未筛选页也显示元信息。缩略图为 v2 的 240×168，属性缓存保留 v1，旧缓存仍可被维护工具识别。旧停靠设置保留在 JSON 中，不再应用；窗口、缩放、视图和排序偏好继续保存。

## 检查结果

`dotnet build --no-restore --nologo`：通过，0 警告、0 错误。`git diff --check`：通过。

| 套件 | 通过项 | 重点 |
|---|---:|---|
| Interface | 52 | 抽屉与页签、窗口缩放、Console 焦点、键盘范围选择、独立滚动、配置恢复 |
| AssetBrowse | 29 | 分页前排序、未知值末尾、真实标签、跨页选择、过期请求取消、缓存维护 |
| LibraryP0 | 43 | 库切换、回收站、跨页批量、真实动画库的备份恢复与离线预览 |
| ExportOptions | 36 | 导出范围、片段、时间和采样配置 |
| Bones / BoneUI | 55 / 42 | 骨骼轨迹和新面板交互 |
| MultiBones / MultiBoneUI | 63 / 49 | 多骨骼选择、固定轨迹与 Root Motion |
| Retarget | 75 | 重定向预览、保存配置、导出和 Console 同步 |
| Characters | 28 | 角色库、切换与持久化 |
| Prototype / Pipeline / Batch | 26 / 18 / 28 | 导入、预览、导出和任务回归 |
| AssetBrowseRender | 5 | 实际渲染主体、损坏缩略图重建、预览实例／播放位置／速度不变 |

上述引擎进程均以 0 退出。角色测试样例仍会输出 FBX 多绑定姿态及缺失外部纹理警告；相关 28 项断言通过。未执行发布包构建。

开发别名 `ShellWindow.tscn` 也单独启动并通过了同一组 52 项界面检查。

## 渲染基线

`--shell-visual-test` 使用隔离库和内置 StudioBot，生成 21 张截图。覆盖 1600×1000 和实际 1024×700 物理窗口，100%、125%、150%、200% 缩放，长中文名、列表多选和抽屉；同时检查预览实例及播放位置未被布局切换重置。

- [桌面 125%](UI-shell-desktop-125.png)
- [1024×700、200% 预览](UI-shell-compact-200.png)
- [列表多选](UI-shell-list-selection.png)
- [导出抽屉](UI-shell-export.png)

完整截图与日志留在 `.runtime/shell-validation`、`.runtime/final-*.log` 和 `.runtime/migration-*.log`。复现命令见 [UI_RULES.md](../UI_RULES.md)；功能套件可用 `./Test.ps1 -Suite All` 运行。
