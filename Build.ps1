param(
    [ValidateSet('Windows','MacArm64','MacX64')][string]$Platform = 'Windows',
    [switch]$SkipSmoke,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$engine = $env:GODOT_MONO_BIN
if ($Platform -eq 'Windows' -and (-not $engine -or -not (Test-Path -LiteralPath $engine))) {
    $engine = 'D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
if (-not $engine -or -not (Test-Path -LiteralPath $engine -PathType Leaf)) {
    throw 'Set GODOT_MONO_BIN to the Godot Mono executable.'
}
$preset, $suffix, $rid = switch ($Platform) {
    'Windows'  { 'Windows Desktop'; 'win-x64'; 'win-x64' }
    'MacArm64' { 'macOS Apple Silicon'; 'macos-arm64'; 'osx-arm64' }
    'MacX64'   { 'macOS Intel'; 'macos-x64'; 'osx-x64' }
}
$mac = $Platform -ne 'Windows'
if ($mac -and -not $IsMacOS) { throw 'Build the macOS bundle on macOS.' }
$config = Get-Content -LiteralPath 'project.godot' -Raw
if ($config -notmatch '(?m)^config/version="([a-zA-Z0-9.-]+)"') { throw 'Missing project version.' }
$version = $Matches[1]
$buildRoot = Join-Path $PSScriptRoot 'Builds'
$output = Join-Path $buildRoot ("Animanager-$version-$suffix-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $buildRoot '.gdignore'), '')
$export = Join-Path $output $(if ($mac) { 'Animanager.app' } else { 'Animanager.exe' })
$executable = if ($mac) { Join-Path $export 'Contents/MacOS/Animanager' } else { $export }
$resources = if ($mac) { Join-Path $export 'Contents/Resources' } else { $output }
if ($mac) {
    # The official Mono template contains a universal executable. Godot's
    # single-architecture export presets require a separately named slice.
    $architecture = if ($Platform -eq 'MacArm64') { 'arm64' } else { 'x86_64' }
    $template = Join-Path $env:HOME 'Library/Application Support/Godot/export_templates/4.7.2.stable.mono/macos.zip'
    $customTemplate = Join-Path $PSScriptRoot ".runtime/$suffix-template.zip"
    $staging = Join-Path $PSScriptRoot ('.runtime/mac-template-' + [guid]::NewGuid().ToString('N'))
    $entries = @(unzip -Z1 $template)
    if ($LASTEXITCODE -ne 0) { throw "Could not read the macOS Mono template: $template" }
    $universal = $entries | Where-Object { $_ -match '(^|/)godot_macos_release\.universal$' } | Select-Object -First 1
    if (-not $universal) { throw 'The macOS Mono template has no universal release executable' }
    $thin = $universal -replace '\.universal$', ".$architecture"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -LiteralPath $template -Destination $customTemplate -Force
    if ($thin -notin $entries) {
        unzip -q $template $universal -d $staging
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the universal Godot executable' }
        Push-Location $staging
        try {
            lipo $universal -thin $architecture -output $thin
            if ($LASTEXITCODE -ne 0) { throw "Could not extract the Godot $architecture slice" }
            zip -q -0 $customTemplate $thin
            if ($LASTEXITCODE -ne 0) { throw 'Could not update the macOS export template' }
        } finally { Pop-Location }
    }
}
dotnet build --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'C# build failed' }
$importLog=Join-Path $PSScriptRoot '.runtime\build-import.log'
$exportLog=Join-Path $PSScriptRoot '.runtime\build-export.log'
& $engine --headless --editor --path $PSScriptRoot --import --quit *> $importLog
if ($LASTEXITCODE -ne 0) { throw "Godot resource import failed: $importLog" }
& $engine --headless --editor --path $PSScriptRoot --export-release $preset $export --quit *> $exportLog
if ($LASTEXITCODE -ne 0 -or (Select-String -LiteralPath $exportLog -Pattern '^ERROR:' -Quiet)) { throw "$preset export failed: $exportLog" }
if (-not (Get-ChildItem -LiteralPath $output -Filter Animanager.dll -Recurse)) { throw 'Export did not contain the application assembly' }
$importer = Join-Path $resources 'Importers/Cue4Parse'
& (Join-Path $PSScriptRoot 'Tools/Build-CueImporter.ps1') -Output $importer -RuntimeIdentifier $rid
# Raw files used by System.IO / Blender are intentionally shipped outside the PCK.
foreach ($folder in @('Baselines','Samples','Scripts','Retarget')) {
    $target = Join-Path $resources "BuiltinAssets/$folder"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "BuiltinAssets/$folder") -File |
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
$helper = Join-Path $importer $(if ($mac) { 'CueImporter' } else { 'CueImporter.exe' })
if ($mac) {
    chmod +x $executable $helper
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the macOS executables runnable' }
    $helperLibraries = Get-ChildItem -LiteralPath $importer -Filter '*.dylib' -File
    codesign --force --sign - $helper @($helperLibraries.FullName)
    if ($LASTEXITCODE -ne 0) { throw 'Could not sign the macOS importer' }
    codesign --force --deep --sign - $export
    if ($LASTEXITCODE -ne 0) { throw 'Could not sign the macOS app' }
    codesign --verify --deep --strict $export
    if ($LASTEXITCODE -ne 0) { throw 'macOS app signature verification failed' }
}
if (-not $SkipSmoke) {
    $smokeRoot=Join-Path $PSScriptRoot ('.runtime\bundle-library\'+[guid]::NewGuid().ToString('N'))
    $smokeLog=Join-Path $PSScriptRoot '.runtime\bundle-smoke.log'
    if ($mac) {
        & $executable --headless --log-file $smokeLog -- --smoke-test "--data-dir=$smokeRoot"
        $smokeExit = $LASTEXITCODE
    } else {
        $smoke=Start-Process -FilePath $executable -ArgumentList ('--headless --log-file "{0}" -- --smoke-test "--data-dir={1}"' -f $smokeLog,$smokeRoot) -WindowStyle Hidden -PassThru
        if (-not $smoke.WaitForExit(60000)) { $smoke.Kill(); throw "Standalone smoke test timed out: $smokeLog" }
        $smokeExit = $smoke.ExitCode
    }
    if ($smokeExit -ne 0) { throw "Standalone smoke test failed: $smokeLog" }
    $reports=@(Get-ChildItem -LiteralPath $smokeRoot -Filter bundle-results.txt -Recurse)
    if ($reports.Count -ne 1 -or (Get-Content -LiteralPath $reports[0].FullName -Raw) -notmatch 'Editor binary: False') { throw 'Standalone smoke report is missing or not from a release executable' }
    Copy-Item -LiteralPath $reports[0].FullName -Destination (Join-Path $output 'SMOKE-RESULTS.txt')
    & $helper --self-test (Join-Path $output 'CUE-SELF-TEST.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CUE4Parse self-test failed' }
}
if ($Zip) {
    if ($mac) {
        ditto -c -k --sequesterRsrc --keepParent $output ($output + '.zip')
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the macOS ZIP' }
    } else {
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output + '.zip')
    }
}
Write-Output "BUILD_READY: $output"
