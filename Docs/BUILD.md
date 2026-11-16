# Windows 便携原型

## 使用

完整解压生成的 ZIP，双击 `Animanager.exe`。不要只复制 EXE：PCK、data 目录和 BuiltinAssets 都是包的一部分。包包含 Godot 与 .NET 运行所需文件；普通使用不需要 Godot 编辑器或 .NET SDK。首次启动导入内置基准资产，用户库默认写入 `%APPDATA%\Godot\app_userdata\Animanager\Library`，不写入安装目录。

FBX 导出需要单独安装 Blender。通过环境变量 `BLENDER_BIN` 指定 `blender.exe`，或在 F12 Console 执行 `pipeline blender "完整路径/blender.exe"`（本次会话生效）。GLB / FBX 预览与 GLB / RES 导出不依赖 Blender。

这是一份本地便携测试原型，尚未制作安装器、数字签名和自动更新。基准人体属于 Epic 模板内容，来源记录随包保留在 `BuiltinAssets/Baselines`；此包不作为公开分发成品。

## 构建

开发机需要 Godot 4.7.2 Mono、同版本导出模板、.NET 8 SDK 和项目 NuGet 依赖。使用 PowerShell：

```powershell
.\Test.ps1 -Suite All
.\Build.ps1 -Zip
```

`Build.ps1` 在 `Builds/Animanager-win-x64-时间/` 创建新目录，构建 C#、导入资源、导出 release、附带原始资产 / Blender 脚本 / 文档和许可，再运行**导出的 EXE** 的冒烟检查。成功才输出 `BUILD_READY` 并生成 ZIP。`-SkipSmoke` 仅供开发诊断，不应将其视为经过验证的包。

内置资产与脚本通过 EXE 目录定位，与当前工作目录无关。SQLite 索引、任务报告、导出缓存和界面设置进入用户库。可以用 `Animanager.exe -- --data-dir="绝对路径"` 指定其他库；不允许两个应用进程同时写同一个库。

冒烟检查覆盖独立程序中的外部脚本定位、Console 导入内置基准、原生 FBX 预览、GLB 导出回读；已配置 Blender 时增加 FBX 导出回读。检查报告复制到包内 `SMOKE-RESULTS.txt`。完整源代码回归为 109 项。干净 Windows 虚拟机和无开发环境机器的部署验证仍待补充。
