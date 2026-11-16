param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$AssetRoot,
    [Parameter(Mandatory)][string]$PreviewMesh,
    [Parameter(Mandatory)][string]$Output,
    [string]$Engine=$env:UNREAL_EDITOR_CMD,
    [string[]]$Names=@()
)
$ErrorActionPreference='Stop'
if (-not $Engine) { $Engine='D:\Unreal\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe' }
if (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) { throw 'Set UNREAL_EDITOR_CMD to an installed UnrealEditor-Cmd.exe' }
$Project=(Resolve-Path -LiteralPath $Project).Path
$Output=[IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $Output) -and (Get-ChildItem -LiteralPath $Output -Force)) { throw 'Use a new, empty output directory; previous exports are never overwritten.' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$config=Join-Path $Output 'bridge-config.json'
@{asset_root=$AssetRoot;preview_mesh=$PreviewMesh;output=$Output;names=$Names} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
$previousConfig=$env:ANIMANAGER_UE_BRIDGE_CONFIG
try {
    $env:ANIMANAGER_UE_BRIDGE_CONFIG=$config
    $script=Join-Path $PSScriptRoot 'export_uasset_animations.py'
    $log=Join-Path $Output 'unreal.log'
    & $Engine $Project '-run=pythonscript' "-script=$script" -unattended -AllowCommandletRendering -RenderOffScreen -nosplash -nosound "-abslog=$log" *> (Join-Path $Output 'stdout.log')
    if ($LASTEXITCODE -ne 0) { throw "Unreal failed; inspect $log" }
    $report=Get-Content -LiteralPath (Join-Path $Output 'conversion-report.json') -Raw | ConvertFrom-Json
    if (-not $report.summary -or -not $report.summary.exported) { throw "No animations were exported; inspect $log" }
    $report.summary | ConvertTo-Json
    if ($report.summary.failed) { throw 'Some sequences failed; see conversion-report.json. Successful files remain available.' }
    Write-Output "IMPORT_FOLDER: $Output"
} finally { $env:ANIMANAGER_UE_BRIDGE_CONFIG=$previousConfig }
