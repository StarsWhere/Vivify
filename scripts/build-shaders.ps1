[CmdletBinding()]
param(
    [string]$UnityEditor='C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Unity.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$source=Join-Path $repoRoot 'ShaderBuild'
$taskDirectory=Join-Path $repoRoot '.tmp/shader-build'
$project=Join-Path $taskDirectory 'project'
$output=Join-Path $taskDirectory 'output'
[IO.Directory]::CreateDirectory((Join-Path $project 'ProjectSettings'))|Out-Null
$assetSource=Join-Path $source 'Assets'
foreach($file in Get-ChildItem -LiteralPath $assetSource -Recurse -File){
    $target=Join-Path (Join-Path $project 'Assets') ([IO.Path]::GetRelativePath($assetSource,$file.FullName))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target))|Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
[IO.File]::WriteAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'),'m_EditorVersion: 2022.3.22f1')
$previous=[Environment]::GetEnvironmentVariable('VIVIFY_SHADER_OUTPUT','Process')
$process=$null
try {
    [Environment]::SetEnvironmentVariable('VIVIFY_SHADER_OUTPUT',$output,'Process')
    $process=Start-Process -FilePath $UnityEditor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','BuildLegacyShaders.Build','-logFile',('"'+(Join-Path $taskDirectory 'build.log')+'"')) -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddMinutes(10)
    while(!$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Seconds 1;$process.Refresh()}
    if(!$process.HasExited -or $process.ExitCode -ne 0){throw 'Unity shader compilation failed; inspect .tmp/shader-build/build.log.'}
    $bundle=Join-Path $output 'legacyshadowcaster2022'
    if(!(Test-Path -LiteralPath $bundle)){throw 'Unity did not produce the shader bundle.'}
    Write-Output ('Built shader bundles in '+$output)
} finally {
    [Environment]::SetEnvironmentVariable('VIVIFY_SHADER_OUTPUT',$previous,'Process')
    if($process){$process.Refresh();if(!$process.HasExited){Stop-Process -Id $process.Id};$process.Dispose()}
}
