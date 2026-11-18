param([string]$Output)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
$revision='d05e5859ad240be10c9d5c8344f5de7c072123d0'
$source=Join-Path $workspace '.runtime\CUE4Parse'
if(-not (Test-Path (Join-Path $source 'CUE4Parse\CUE4Parse.csproj'))){
    git clone https://github.com/FabianFG/CUE4Parse.git $source
    if($LASTEXITCODE -ne 0){throw 'CUE4Parse clone failed'}
    git -C $source checkout --detach $revision
    if($LASTEXITCODE -ne 0){throw 'CUE4Parse revision checkout failed'}
}
$actual=git -C $source rev-parse HEAD
if($actual -ne $revision){throw "CUE4Parse must be pinned to $revision; found $actual"}
$patch=Join-Path $PSScriptRoot 'patches\CUE4Parse-ue50-raw-track.patch'
git -C $source apply --reverse --check --unidiff-zero $patch 2>$null
if($LASTEXITCODE -ne 0){
    git -C $source apply --check --unidiff-zero $patch
    if($LASTEXITCODE -ne 0){throw 'CUE4Parse UE5.0 patch context differs; inspect the checkout'}
    git -C $source apply --unidiff-zero $patch
    if($LASTEXITCODE -ne 0){throw 'CUE4Parse UE5.0 patch failed'}
}
if(-not $Output){$Output=Join-Path $workspace '.runtime\cue-importer'}
$Output=[IO.Path]::GetFullPath($Output)
$env:DOTNET_CLI_HOME=Join-Path $workspace '.runtime\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=Join-Path $env:USERPROFILE '.nuget\packages'
Push-Location (Join-Path $PSScriptRoot 'CueImporter')
try {
    dotnet publish CueImporter.csproj -c Release -r win-x64 --self-contained true -p:CUE4PARSE_SKIP_NATIVE=true -o $Output --nologo -v:q
    if($LASTEXITCODE -ne 0){throw 'CUE importer publish failed'}
} finally {Pop-Location}
Copy-Item (Join-Path $source 'LICENSE') (Join-Path $Output 'CUE4Parse-LICENSE.txt')
Copy-Item (Join-Path $source 'NOTICE') (Join-Path $Output 'CUE4Parse-NOTICE.txt')
Copy-Item $patch (Join-Path $Output 'CUE4Parse-local-changes.patch')
Copy-Item (Join-Path $workspace 'BuiltinAssets\Baselines\README.md') (Join-Path $Output 'Baseline\EPIC-PROVENANCE.md')
Copy-Item (Join-Path $workspace 'BuiltinAssets\Baselines\provenance.json') (Join-Path $Output 'Baseline\epic-provenance.json')
[IO.File]::WriteAllText((Join-Path $Output 'SOURCE.txt'),"CUE4Parse https://github.com/FabianFG/CUE4Parse`nRevision: $revision`nAnimanager importer protocol 3. Nodachi rigid-prop motion schema 1. Local patch: ue50-tagged-raw-track-v1 (CUE4Parse-local-changes.patch). Validated UE4.23/4.27 raw, UE5.0 tagged AnimDataModel and UE5.6 Sequencer Direct FK source animations; automatic standard skeleton profiles.`n")
Write-Output "CUE_IMPORTER_READY: $Output"
