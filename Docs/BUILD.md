# Animanager 0.5 Beta 便携包

## 使用

| 平台 | 启动方式 |
| --- | --- |
| Windows x64 | 完整解压 ZIP，双击 `Animanager.exe`。同目录的 PCK、运行库和 BuiltinAssets 需要一起保留。 |
| macOS Apple Silicon / Intel | 选择与处理器对应的 ZIP，解压后打开 `Animanager.app`。可以将 APP 移到「应用程序」。 |

包内附应用运行库与 CUE4Parse 导入器。素材库默认写入 Godot 的 `user://Library`。Windows 通常位于 `%APPDATA%\Godot\app_userdata\Animanager\Library`，macOS 通常位于 `~/Library/Application Support/Godot/app_userdata/Animanager/Library`。

macOS Beta 使用临时签名，未经 Apple 公证。若首次打开被系统拦截，可在「系统设置 → 隐私与安全性」中为该应用选择「仍要打开」。Cascadeur 接收插件目前面向 Windows。

FBX 导出需要另装 Blender。可设置环境变量 `BLENDER_BIN` 指向 Blender 可执行文件，或在控制台执行 `pipeline blender "可执行文件绝对路径"`。macOS 会尝试查找 `/Applications/Blender.app/Contents/MacOS/Blender`。

## 构建

开发机需要 Godot 4.7.2 Mono 及同版本 Mono 导出模板，以及 .NET SDK 8.0.422 和 10.0.404。将 `GODOT_MONO_BIN` 设为 Mono 版 Godot 的可执行文件。

```powershell
./Build.ps1 -Zip                    # Windows x64
./Build.ps1 -Platform MacArm64 -Zip  # Apple Silicon，使用 macOS 上的 PowerShell 7
./Build.ps1 -Platform MacX64 -Zip    # Intel，使用 macOS 上的 PowerShell 7
```

构建脚本在 `Builds/` 下创建带版本与时间戳的目录。独立应用冒烟报告在 `SMOKE-RESULTS.txt`，导入器自测报告在 `CUE-SELF-TEST.json`。ZIP 保留应用所需的目录与权限。

GitHub Actions 的 `Desktop Beta` 工作流从同一源码提交构建 Windows x64、macOS Apple Silicon 和 macOS Intel，逐个平台执行包内检查并保存 ZIP。

内置 Epic 人体基准来自 Unreal Engine 安装目录的官方模板，来源与 SHA-256 随包保留。有关模板示例的再分发条款见 [Unreal Engine EULA §5(b)](https://www.unrealengine.com/eula/unreal)。
