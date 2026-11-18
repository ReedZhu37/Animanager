# Animanager UI 规则（组件化版）

状态：**已接入正式主入口，并恢复可调整的停靠布局**（2026-11-17）。`MainShell` 保留业务控制，`ShellLayout` 使用新组件风格，内部通过 `DockWorkspace` 处理拖放、分区和尺寸保存。

代码在 `UI/Shell/`，三个入口：

| 场景 | 用途 |
|---|---|
| `res://UI/MainShell.tscn` | 默认主场景，使用组件化 UI 与真实业务 |
| `res://UI/Shell/ShellWindow.tscn` | 开发入口，继承同一 `MainShell`，避免两套业务实现 |
| `res://UI/Shell/GalleryWindow.tscn` | 组件画廊，逐状态核对每个控件 |

资产视图有两套，共用一份数据和一个选中模型：

| 视图 | 用途 | 特点 |
|---|---|---|
| 网格 `AssetGrid` | 认图、扫一眼 | 156×174 卡片，缩略图为主 |
| 列表 `AssetList` | 比对、按属性筛 | 30px 行高，可排序表头，列按优先级自动增减 |

```powershell
# 主界面（跟随系统 DPI 或保存的缩放设置）
dotnet build --no-restore
& $env:GODOT_MONO_BIN --path .

# 组件画廊
& $env:GODOT_MONO_BIN --path . res://UI/Shell/GalleryWindow.tscn

# 生成参考截图：隔离库、内置样例，结果写入 .runtime/shell-validation
& $env:GODOT_MONO_BIN --path . -- --shell-visual-test `
  "--data-dir=$PWD\.runtime\shell-visual-library"
```

---

## 1. 目录结构

```
UI/Shell/
  Design/            设计层：没有任何控件逻辑
    Tokens.cs          颜色 / 圆角 / 尺寸 / 间距 / 卡片度量
    Typography.cs      字体族、字阶、Text.Fit 截断
    Icons.cs           31 个矢量图标，程序化绘制
    ThemeFactory.cs    StyleBox 构造 + Godot 原生控件主题
  Widgets/           原子控件
    Primitives.cs      IconView / StatusDot / BadgePill / CapButton / IconButton
                       / SearchField / Hairline
    InspectorParts.cs  InspectorSection / FieldRow / ClipList
  Layout/            布局组件
    AppHeader.cs       顶栏 + BrandMark + StatusBar
    SideBar.cs         侧栏（展开 210 / 收起 50）
    NavRail.cs         NavRow / NavSection
    AssetGrid.cs       虚拟化网格 + AssetCardModel
    AssetList.cs       虚拟化列表 + 可排序表头 + 列优先级
    AssetSelection.cs  两个视图共用的选中模型
    AssetBrowser.cs    视图宿主：切换时保留选中、锚点、滚动位置
    Inspector.cs       右侧检查器
    PreviewStage.cs    预览台（3D + 浮层 + transport）
    SelectionBar.cs    SelectionBar / FilterChip / FilterBar
  ShellLayout.cs     停靠布局接线、抽屉、窄屏导航、Toast
  ShellWindow.cs     开发场景别名，继承 MainShell
  GalleryWindow.cs   组件画廊（不随应用发布）
```

**分层纪律**：`Design` 定义视觉规则，`Widgets` 不知道资产库，`Layout` 不调用业务 API。`MainShell.*` 负责库、选择、预览、任务与组件接线。

---

## 2. 布局

```
┌ 顶栏 50 ──────────────────────────────────────────────────────┐
│ ◆ Animanager │ ● 库名 1,204 ▾ │ ⌕ 搜索 Ctrl+F │ 导入 │ 导出 │ 任务 │ ⋯ │
├──────────┬─────────────────────────────────────┬────────────┤
│ 导航      │ 中央预览 viewport                    │ 属性 / 骨骼 │
│ 分类/集合 │ 设置入口、片段列表、播放控件            │ / 重定向    │
├──────────┴─────────────────────────────────────┴────────────┤
│ 动画资产 · 工具栏、筛选、多选、分页                             │
│ [卡片] [卡片] [卡片] [卡片] [卡片] …                         │
├─────────────────────────────────────────────────────────────┤
│ 状态栏 28：● 就绪 · 744 个资产 · 已选 4     125% · Console F12   │
└────────────────────────────────────────────────────────────────┘
```

### 视图切换

网格和列表不是两个独立页面，是同一份数据的两套画法：

- **选中共享**：`AssetSelection` 由 `AssetBrowser` 持有，两个视图都绑定它。切换视图后选项、Shift 范围锚点、跨页未见的选项全部保留。
- **滚动位置各自记忆**：切回去时回到原来的位置，不是顶部。
- **缩略图共享**：两个视图拿到的是同一批 `AssetCardModel` 实例，一张缩略图只渲染一次，两边同时显示。

列表的列有**丢弃优先级**（`DropPriority`，数字越大越先被砍）：

| 列 | 最小宽 | 优先级 | 说明 |
|---|---|---|---|
| 名称 | 148 | — | 永不移除 |
| 导入 | 76 | 0 | 永不移除，右缘锚点 |
| 时长 | 52 | 1 | |
| 骨架 | 74 | 2 | 有弹性权重 |
| 大小 | 58 | 2 | |
| 帧率 | 44 | 3 | |
| 类型 | 44 | 4 | 最先被砍 |

点表头排序，再点同一列反向。`SortRequested` 通过 `AssetSort` 进入数据层，排序在分页前完成。支持导入时间、名称、大小、收藏、时长和源帧率；未知时长／帧率在两个方向都排末尾，同值按名称、ID 稳定排序。

表头固定在滚动区域顶部，绘制和点击区域使用相同的滚动补偿；键盘定位的行保持在表头下方。资产区提供名称／标签搜索，与顶部搜索同步，输入防抖 200ms，回车立即应用；Ctrl+F 聚焦资产区输入框。搜索覆盖当前库和分类中的全部结果，再按 60 项分页，提供首页／上一页／下一页／尾页。窄面板将分页按钮收为符号并保留悬停说明，避免工具栏占满整个列表。

### 尺寸

| 元素 | 值 | 位置 |
|---|---|---|
| 顶栏 | 50 | `Tokens.Size.HeaderHeight` |
| 状态栏 | 28 | `Tokens.Size.StatusBarHeight` |
| 侧栏 / 收起 | 210 / 50 | `Tokens.Size.Sidebar` / `SidebarRail` |
| 检查器内容最小宽 | 292 | `Tokens.Size.Inspector` |
| 预览内容最小宽 | 330 | `Tokens.Size.StageMin` |
| 常规按钮 / 下拉框内边距 | 左右 12 / 上下 6 | `ThemeFactory` |
| 工具面板内容内边距 | 12 | `DockGroupView`；已有内边距的组件不重复包裹 |
| 分隔条 / 工作区外边距 | 8 / 8 | `DockWorkspace` / `ShellLayout` |
| 卡片 | 156×174 | `Tokens.CardMetrics` |
| 卡片间距 / 外边距 | 12 / 14 · 16 / 16 | 同上 |

### 默认布局、拖放与响应式

默认布局由 `WorkspaceLayout.Studio()` 定义：上方为导航、中央 viewport 和右侧属性／骨骼／重定向，底部是整个动画资产卡片浏览区。播放与片段控件仍在预览内，不增加单独时间轴面板。分页合并到资产工具栏，给卡片保留显示高度。

- 拖动面板标题标签：中心区域合并为标签组，边缘建立左右／上下分区；青绿色覆盖层提示放置目标。
- 拖动分隔条：调整各区域大小；比例随窗口变化保持，关闭重开继续使用。
- 双击标题或面板「⋮」：最大化／恢复；菜单也提供隐藏、移动和合并操作。
- 「⋯ → 恢复默认布局」回到 viewport 在上、卡片在下的布局；隐藏的面板也可以从「⋯」重新显示。
- 空间不足时临时使用单标签组和快捷导航，放大后还原桌面分区。缩小窗口不改写已保存的桌面布局树。

面板实例在重排时复用，不重新加载模型；资产选择、播放位置和骨骼状态保持。`DockGroupView` 对本身已有滚动的组件直接填充内容区，骨骼、重定向等长表单才使用外层滚动。

导出、任务、筛选使用右侧抽屉，宽度最多 460 逻辑像素，内容内边距 16；Esc 或关闭按钮收起。很窄的窗口隐藏顶栏次要按钮，所有操作仍可从「⋯」菜单访问。预览角色和显示选项移入齿轮打开的独立设置页，播放控件按可用高度收紧。导航按钮采用直接按住拖动的交互；骨骼文字由窗口二维层绘制，避免 3D 渲染纹理缩放。

验收包含真实的 1024×700 物理窗口，以及 100%、125%、150%、200% 缩放。窗口最小尺寸不再随 DPI 膨胀到超过该验收尺寸。

---

## 3. 设计 token

```
色阶（4 级，靠明度分层，不用阴影）
bg/base    #0f1115    工作区底
bg/panel   #161a1f    侧栏、检查器
bg/raised  #1b2027    顶栏、状态栏、按钮
bg/card    #191e24    资产卡片
bg/hover   #222831    卡片悬停/选中
bg/sunken  #0a0c0f    缩略图槽、输入框
line       #232a33    分割线
line+      #333c48    控件描边

文字
text       #e7ecf2      text-2   #99a4b3      text-3   #69737f

强调与语义（各只承担一种含义）
accent      #4fd1b5   on-accent  #06231d   accent-wash  #14312b
info        #5b9dff   success    #48c78e   warn #e8b04b   danger #e56a5f
```

字阶：`19/600` 弹窗标题 · `15/600` 分区标题 · `14/600` 品牌 · `13/400` 正文 · `12/600` 名称 · `12/400` 行 · `11/400` 次要 · `10/400` 微 · `10 mono` 数字。
字体族：`Segoe UI Variable Text → Segoe UI → Microsoft YaHei UI → Noto Sans CJK SC`；等宽 `Cascadia Mono → Consolas`。CJK 回退不是装饰——缺失时中文会落到位图字体，在任何缩放下都显得业余。

圆角：`3` chip / `6` 控件 / `10` 卡片 / `14` 弹层 / `999` pill。
间距刻度：`4 8 12 16 24`。

**图标**：31 个，全部 `Icons.Draw` 程序化绘制，20×20 设计框缩放，1.6 描边。零图片资源，任意 DPI 清晰。

---

## 4. 组件规格

### 4.1 资产卡片 156×174

```
┌ 156 ────────────────┐
│ ┌ 138×94 ─────────┐ │  缩略图槽（sunken）；收藏★ 右上；动画▶ 左下
│ └─────────────────┘ │  缩略图区 y = 9..103
│ UE5_Quinn           │  y = 123（缩略图底 + 20），12/600，单行省略
│ FBX · 2.5 MB        │  y = 141（+38），10 mono，text-3
│ 2.0s · 60fps · UE5  │  y = 157（+54），10，text-3
└─────────────────────┘
选中：1px accent 描边 + 左上 16px 圆形勾
```

基线全部由 `ThumbInset + ThumbHeight` 推导，不写绝对值——这是修过一次文字溢出后的硬约束。

### 4.2 导航行 30px
`14 左缩进 | 15 图标 | 文字 12 | 计数 10 mono | 10 右缩进`
激活：左侧 2×16 accent 竖条 + 文字转 600 + 图标转 accent + 底 `active.55`。
收起态 50px：同样五个类别保留为图标，tooltip 带名称与计数。

### 4.3 检查器
分区表头 30px（12 缩进 + 12 chevron + 10/600 标题 + 可选 `+`），正文缩进 14。
字段行：标签 70px `text-3 11`，值右对齐（数字/路径用 mono 10）。

### 4.4 播放 transport（两行，永不换行）
```
⏮ ▶ ⏭ │ 31 / 61 帧 ←弹性→ [速率滑条] 1.00× ⟲
▬▬▬▬▬▬▬●▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬
```
浮层 `panel.94`，圆角 10，距边缘 12。

### 4.5 多选条
选中 ≥2 时**替换**筛选 chip 行：`已选 N 项（accent 600）←弹性→ [标签][收藏][集合][导出][删除]`
底 `accent-wash`，1px `accent.32`。删除用 danger 色，其余中性。

### 4.6 状态栏
`● 状态文字（可省略截断）| 上下文计数 | 125% | Console F12`
圆点即严重性：success / info / warn / danger。

---

## 5. Godot 陷阱（已踩，写进规则）

这一节是这次重构最贵的产出，都是实测出来的：

1. **`Button` 不是容器。** 任何"图标+文字"按钮若用 `SetAnchorsAndOffsetsPreset` 把内容贴上去，内容**不参与最小尺寸计算**，窗口一窄全行叠在一起。必须 `Button` + `MarginContainer`（锚定 FullRect）+ `HBoxContainer` + 显式 `CustomMinimumSize.X`。

2. **锚定子节点不贡献父级最小尺寸。** 顶栏曾因此被压缩到按钮重叠。解决方式：每个复合按钮自己发布宽度，库名标签也发布 `Text.Width`。

3. **`ScrollContainer` 从不把子节点撑到视口宽**，只按子节点最小尺寸放置。`Resized` 事件常在 `Size` 仍为 0 时触发，靠它镜像宽度会与布局互相打架。画廊最终改用固定文档宽度（1080）而不是流体宽度。

4. **`PanelContainer` 只从「容器类子节点」算最小尺寸。** 放一个 `Control` 进去会让面板塌成 0。库切换器因此改成 `PanelContainer` + `HBoxContainer`，再叠一个透明 `Button` 接管输入。

5. **`GridContainer` 比 `HBoxContainer` 更适合"标签 + 内容"行**。HBox 里两个 `ExpandFill` 会争夺空间把标签挤成 0；Grid 的列宽是确定的。

6. **卡片的文字基线要由几何推导**，不要写死 y。改卡片高度时写死值必然溢出。

---

## 6. 数据与生命周期约定

| 项目 | 实现 | 约束 |
|---|---|---|
| 排序 | 原生字段由 SQL 白名单排序；时长／FPS 在后台补属性后排序 | 先排序再分页，保留旧 API 重载 |
| 卡片元信息 | 未筛选路径也通过 `BrowseAssetsAsync` 补当前页属性 | 缺失数据保持未知，不用播放速度推算源 FPS |
| 缩略图 | `240×168`，`preview-cache/v2/thumbnails` | 属性仍在 v1；清理／备份同时识别旧、新缩略图 |
| 选择 | `AssetBrowser.Selection` 是唯一选择模型 | 跨页、筛选和视图切换保留 ID；仅剪除已不存在的资产 |
| 视图 | 网格／列表共享卡片对象，各自记忆滚动位置 | 等容器完成布局再恢复滚动；只绘制可视行 |
| 库切换 | 顶栏接真实库管理器 | 取消旧浏览请求，清空旧预览，过期异步结果不得回写 |
| 导入／导出／任务 | 复用已有业务、确认流程和任务队列 | 批量动作使用跨页选择；抽屉不重建预览 |
| 筛选 | chip 删除／清空接 `AssetAttributeFilter` | 导出当前筛选结果与浏览使用同一规则 |
| 预览 | 同一 `Preview3DViewport` 实例 | 源模型及重定向先离屏准备，代次校验后一次性替换；取消或失败保留当前画面。缩略图使用独立渲染器，间隔 32ms |
| 设置 | 保存缩放、窗口、资产视图、排序和 `DockLayout` | 旧 `Layout` 字段留存；首次使用新的默认布局，此后恢复用户停靠位置和分隔比例 |

---

## 7. 验收入口

| 入口 | 范围 | 输出 |
|---|---|---|
| `Test.ps1 -Suite All` | 主界面、资产库、排序筛选、导出、骨骼、重定向、角色、流水线 | `.runtime/*-test.log` 及各隔离库结果 |
| `--shell-visual-test`（需要渲染） | 100/125/150/200%、1024×700、长中文名、多选、三个抽屉 | `.runtime/shell-validation`，21 张截图 |
| `--asset-browse-render`（需要含骨架动画的隔离库） | 缩略图主体、损坏缓存重建、预览状态保持 | 库内 `asset-browse-results` |
| `GalleryWindow.tscn` | 单组件状态展示 | 开发工具，不随应用发布 |

测试直接操作真实 `AssetBrowser` 与面板，不保留隐藏的旧列表。停靠使用完整鼠标输入事件验证拖入标签组、拖到边缘分区、分隔条缩放和布局保存；`Viewport.PushInput` 不更新原生拖放读取的鼠标位置，因此拖放测试使用 `Input.ParseInputEvent`。
