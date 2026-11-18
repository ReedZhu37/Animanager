param([ValidateSet('Prototype','Pipeline','Batch','Interface','Bones','BoneUI','MultiBones','MultiBoneUI','LibraryP0','ExportOptions','AssetBrowse','P1','Retarget','Characters','Feedback','SecondFeedback','ThirdFeedback','All')][string]$Suite = 'Prototype')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
dotnet build --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$engine = $env:GODOT_MONO_BIN
if (-not $engine -or -not (Test-Path -LiteralPath $engine)) {
    $engine = 'D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
}
$modes = switch ($Suite) {
    'Prototype' { 'self-test' }
    'Pipeline' { 'pipeline-test' }
    'Batch' { 'batch-test' }
    'Interface' { 'interface-test' }
    'Bones' { 'bone-test' }
    'BoneUI' { 'bone-ui-test' }
    'MultiBones' { 'multi-bone-test' }
    'MultiBoneUI' { 'multi-bone-ui-test' }
    'LibraryP0' { 'library-p0-test' }
    'ExportOptions' { 'export-options-test' }
    'AssetBrowse' { 'asset-browse-test' }
    'P1' { 'export-options-test'; 'asset-browse-test' }
    'Retarget' { 'retarget-test' }
    'Characters' { 'character-test' }
    'Feedback' { 'feedback-test' }
    'SecondFeedback' { 'second-feedback-test' }
    'ThirdFeedback' { 'third-feedback-test' }
    'All' { 'self-test'; 'pipeline-test'; 'batch-test'; 'interface-test'; 'bone-test'; 'bone-ui-test'; 'multi-bone-test'; 'multi-bone-ui-test'; 'library-p0-test'; 'export-options-test'; 'asset-browse-test'; 'retarget-test'; 'character-test'; 'feedback-test'; 'second-feedback-test'; 'third-feedback-test' }
}
foreach ($mode in $modes) {
    & $engine --headless --path $PSScriptRoot --log-file (Join-Path $PSScriptRoot ".runtime\$mode.log") -- "--$mode" "--data-dir=$PSScriptRoot\.runtime\$mode-library"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
