# 组件 UI 布局与停靠验收

2026-11-17 · Windows · Godot 4.7.2 Mono (.NET)

本次在组件迁移基础上恢复停靠与尺寸调整。默认上方为导航、中央 viewport、属性／骨骼／重定向工具，下方为动画资产卡片区；播放控制仍在 viewport 内。

面板标签可拖到其他面板中央合并，拖到边缘分区；横纵分隔条可调节比例，双击标签最大化／恢复，Esc 取消拖动。布局保存在 `WorkspacePreferences.DockLayout`，首次使用新默认布局，后续恢复用户布局；顶部更多菜单可恢复默认布局。

按钮、工具栏、面板和抽屉统一使用组件主题间距。卡片分页移入资产工具栏，为底部卡片保留高度。窄窗口自动切换面板访问方式，不覆盖桌面停靠树。资源库管理内容改为可滚动，避免换行操作栏把原生弹窗撑出屏幕。

## 检查结果

`dotnet build --no-restore --nologo`：0 警告、0 错误。`git diff --check`：通过。

| 套件 | 通过项 | 验证内容 |
|---|---:|---|
| Interface（headless 与 Windows 渲染各一轮） | 64 | 默认布局、按钮 padding、鼠标拖动合并／分区、横纵分隔条、Esc 取消、布局保存、预览实例及播放位置保留、100%／200% 资源库弹窗边界 |
| AssetBrowse | 29 | 资产浏览、排序、缓存和选择 |
| BoneUI | 42 | 骨骼控制、滚动、缩放和选择保留 |
| MultiBoneUI | 49 | 多骨骼选择和轨迹 |
| ExportOptions | 36 | 导出选项 |
| Retarget | 75 | 重定向预览、配置和导出 |

上述最终运行均通过。界面拖动测试通过 `Input.ParseInputEvent` 派发输入；Windows 渲染运行同时同步原生鼠标位置，验证实际控件输入路径。

`--shell-visual-test` 生成 21 张截图，覆盖 100%、125%、150%、200% 缩放、1600×1000 和实际 1024×700 窗口、长中文名、列表多选、抽屉及重定向表单。

- [桌面默认布局 125%](UI-dock-desktop-125.png)
- [1024×700、200% 预览](UI-dock-compact-200.png)
- [导出抽屉](UI-dock-export.png)

日志位于 `.runtime/dock-*.log`，完整截图位于 `.runtime/shell-validation`。复现入口与规则见 [UI_RULES.md](../UI_RULES.md)。本次未执行发布包构建。
