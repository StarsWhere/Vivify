[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AuditPath,
    [string]$PythonPath='python',
    [string]$UnityPyModules,
    [string]$ShadowBundle,
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'releases/bs-1.45.2/UserData/Vivify/Compatibility'}
if(!$ShadowBundle){$ShadowBundle=Join-Path $repoRoot '.tmp/shader-build/output/legacyshadowcaster2022'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$ShadowBundle=[IO.Path]::GetFullPath($ShadowBundle)
if(!(Test-Path -LiteralPath $ShadowBundle -PathType Leaf)){throw 'Compile shaders first with scripts/build-shaders.ps1.'}
if(!$UnityPyModules){
    $UnityPyModules=Join-Path $repoRoot '.tmp/tools/unitypy-1.25.4'
    if(!(Test-Path -LiteralPath (Join-Path $UnityPyModules 'UnityPy'))){
        & $PythonPath -m pip install --target $UnityPyModules -r (Join-Path $repoRoot 'ShaderBuild/requirements-build.txt')
        if($LASTEXITCODE -ne 0){throw 'UnityPy installation failed.'}
    }
}
$audit=Get-Content -LiteralPath $AuditPath -Raw | ConvertFrom-Json
if($audit.missing.Count){throw 'The SongCore dependency audit contains missing requirements or charts.'}
$converter=Join-Path $repoRoot 'ShaderBuild/port_bundle_shadow_pass.py'
foreach($asset in $audit.assets | Group-Object checksum | ForEach-Object { $_.Group[0] }){
    if(!$asset.exists -or !$asset.checksum){throw ('Missing Vivify bundle or checksum: '+$asset.path)}
    $arguments=@($converter,'--bundle',$asset.path,'--checksum',[string]$asset.checksum,'--output',$OutputDirectory,'--shadow-bundle',$ShadowBundle)
    if($UnityPyModules){$arguments+=@('--modules',[IO.Path]::GetFullPath($UnityPyModules))}
    & $PythonPath @arguments
    if($LASTEXITCODE -ne 0){throw ('Vivify resource port failed: '+$asset.path)}
}
Write-Output ('Compatibility asset copies: '+$OutputDirectory)
