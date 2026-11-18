param([switch]$SkipSmoke, [switch]$Zip)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
$engine = $env:GODOT_MONO_BIN
if (-not $engine -or -not (Test-Path -LiteralPath $engine)) {
    $engine = 'D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
$buildRoot = Join-Path $PSScriptRoot 'Builds'
$output = Join-Path $buildRoot ('Animanager-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $buildRoot '.gdignore'), '')
dotnet build --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'C# build failed' }
$importLog=Join-Path $PSScriptRoot '.runtime\build-import.log'
$exportLog=Join-Path $PSScriptRoot '.runtime\build-export.log'
& $engine --headless --editor --path $PSScriptRoot --import --quit *> $importLog
if ($LASTEXITCODE -ne 0) { throw "Godot resource import failed: $importLog" }
& $engine --headless --editor --path $PSScriptRoot --export-release 'Windows Desktop' (Join-Path $output 'Animanager.exe') --quit *> $exportLog
if ($LASTEXITCODE -ne 0 -or (Select-String -LiteralPath $exportLog -Pattern '^ERROR:' -Quiet)) { throw "Windows export failed: $exportLog" }
if (-not (Get-ChildItem -LiteralPath $output -Filter Animanager.dll -Recurse)) { throw 'Export did not contain the application assembly' }
& (Join-Path $PSScriptRoot 'Tools\Build-CueImporter.ps1') -Output (Join-Path $output 'Importers\Cue4Parse')
# Raw files used by System.IO / Blender are intentionally shipped outside the PCK.
foreach ($folder in @('Baselines','Samples','Scripts','Retarget')) {
    $target = Join-Path $output "BuiltinAssets\$folder"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "BuiltinAssets\$folder") -File |
        Where-Object { $_.Extension -notin @('.import','.uid') } |
        Copy-Item -Destination $target
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Examples') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Docs') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
New-Item -ItemType Directory -Path (Join-Path $output 'Licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addons\limbo_console\LICENSE.md') -Destination (Join-Path $output 'Licenses\LimboConsole-MIT.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addons\limbo_console\res\fonts\LICENSE.md') -Destination (Join-Path $output 'Licenses\LimboConsole-Fonts.md')
& $engine --headless --path $PSScriptRoot --script res://Tools/write_engine_licenses.gd -- "--out-dir=$output/Licenses"
if ($LASTEXITCODE -ne 0) { throw 'Could not write engine license notices' }
if (-not $SkipSmoke) {
    $smokeRoot=Join-Path $PSScriptRoot ('.runtime\bundle-library\'+[guid]::NewGuid().ToString('N'))
    $smokeLog=Join-Path $PSScriptRoot '.runtime\bundle-smoke.log'
    $smoke=Start-Process -FilePath (Join-Path $output 'Animanager.exe') -ArgumentList ('--headless --log-file "{0}" -- --smoke-test "--data-dir={1}"' -f $smokeLog,$smokeRoot) -WindowStyle Hidden -PassThru
    if (-not $smoke.WaitForExit(60000)) { $smoke.Kill(); throw "Standalone smoke test timed out: $smokeLog" }
    if ($smoke.ExitCode -ne 0) { throw "Standalone smoke test failed: $smokeLog" }
    $reports=@(Get-ChildItem -LiteralPath $smokeRoot -Filter bundle-results.txt -Recurse)
    if ($reports.Count -ne 1 -or (Get-Content -LiteralPath $reports[0].FullName -Raw) -notmatch 'Editor binary: False') { throw 'Standalone smoke report is missing or not from a release executable' }
    Copy-Item -LiteralPath $reports[0].FullName -Destination (Join-Path $output 'SMOKE-RESULTS.txt')
}
if ($Zip) { Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output + '.zip') }
Write-Output "BUILD_READY: $output"
