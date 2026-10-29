[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string[]]$Components = @('Vivify'),
    [string]$BuildDirectory,
    [string]$OutputDirectory,
    [ValidateRange(1,100)][int]$LocalRevision=5,
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'This source build targets 1.45.2_28848.' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $repoRoot '.tmp/build' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'releases/bs-1.45.2' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$referencesDirectory = Join-Path $BuildDirectory 'refs'
$pluginsDirectory = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($referencesDirectory) | Out-Null
[IO.Directory]::CreateDirectory($pluginsDirectory) | Out-Null
$sourceRoot = $repoRoot
$localSuffix='+local.'+$LocalRevision
$toolsDirectory=Join-Path $repoRoot '.tmp/tools/dnlib-4.5.0'
$dnlibPath=Join-Path $toolsDirectory 'lib/net45/dnlib.dll'
if (!(Test-Path -LiteralPath $dnlibPath)) {
    [IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
    $archive=Join-Path $repoRoot '.tmp/tools/dnlib-4.5.0.zip'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/dnlib/4.5.0/dnlib.4.5.0.nupkg' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsDirectory
}
if ((Get-FileHash -LiteralPath $dnlibPath -Algorithm SHA256).Hash -ne '97EF5328D1D1703924C48D9CD1D45B7227FE3836E116DCDED623B6A0DBD52C23') { throw 'dnlib build tool checksum mismatch.' }
Add-Type -Path $dnlibPath
$projects = @{
    Vivify='Vivify/Vivify.csproj'
}
$publicizeNames = @('Main','DataModels','BeatmapCore','GameplayCore','Colors','HMLib','HMUI','HMRendering','Rendering','SaberTrail','Zenject','BGLib.AppFlow','BeatSaber.GameSettings','BeatSaber.ViewSystem','SiraUtil','SegmentedControl','Unity.TextMeshPro')
$references = @{}
foreach ($directory in @('Beat Saber_Data/Managed','Libs','Plugins')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $GameRoot $directory) -File -Filter '*.dll') {
        if ($references.ContainsKey($file.BaseName)) { continue }
        if ($file.BaseName -notin $publicizeNames) { $references[$file.BaseName]=$file.FullName; continue }
        $target = Join-Path $referencesDirectory $file.Name
        if (!(Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).LastWriteTimeUtc -lt $file.LastWriteTimeUtc -or (Get-Item -LiteralPath $target).LastWriteTimeUtc -lt (Get-Item -LiteralPath $PSCommandPath).LastWriteTimeUtc) {
            $module = [dnlib.DotNet.ModuleDefMD]::Load($file.FullName)
            try {
                foreach ($type in $module.GetTypes()) {
                    if ($type.IsNested) { $type.Visibility=[dnlib.DotNet.TypeAttributes]::NestedPublic }
                    elseif ($type.Name.String -ne '<Module>') { $type.Visibility=[dnlib.DotNet.TypeAttributes]::Public }
                    $eventNames=@($type.Events | ForEach-Object { $_.Name.String })
                    foreach ($field in $type.Fields) { if ($field.Name.String -notin $eventNames) { $field.Access=[dnlib.DotNet.FieldAttributes]::Public } }
                    foreach ($method in $type.Methods) {
                        if (!$method.IsVirtual -or (!$method.IsFamily -and !$method.IsFamilyOrAssembly)) { $method.Access=[dnlib.DotNet.MethodAttributes]::Public }
                    }
                }
                $module.Write($target)
            } finally { $module.Dispose() }
        }
        $references[$file.BaseName]=$target
    }
}
foreach ($file in Get-ChildItem -LiteralPath $pluginsDirectory -File -Filter '*.dll') { $references[$file.BaseName]=$file.FullName }
$compiler = Get-ChildItem -LiteralPath (Join-Path (Split-Path $DotnetPath -Parent) 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
foreach ($component in $Components) {
    if (!$projects.ContainsKey($component)) { throw "Unknown component: $component" }
    $project = Join-Path $sourceRoot $projects[$component]
    [xml]$definition = Get-Content -LiteralPath $project -Raw
    $source = Split-Path -Parent $project
    $work = Join-Path $BuildDirectory $component
    [IO.Directory]::CreateDirectory($work) | Out-Null
    $properties = @{}
    foreach ($group in $definition.Project.PropertyGroup) { foreach ($property in $group.ChildNodes) { $properties[$property.Name]=$property.InnerText } }
    $depends = [ordered]@{}
    foreach ($dependency in $definition.SelectNodes('//DependsOn')) { $depends[$dependency.Include]=$dependency.Version }
    if ($component -eq 'SongCore') {
        $originalManifest=Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
        $properties.PluginId=$originalManifest.id; $properties.PluginName=$originalManifest.name
        $properties.Author=$originalManifest.author; $properties.Version=$originalManifest.version
        $properties.Description=$originalManifest.description
        foreach ($property in $originalManifest.dependsOn.PSObject.Properties) { $depends[$property.Name]=$property.Value }
    }
    $manifest = [ordered]@{id=$properties.PluginId;name=$properties.PluginName;author=$properties.Author;version=($properties.Version+$localSuffix);gameVersion='1.45.2';description=$properties.Description;dependsOn=$depends;links=@{'project-source'='https://github.com/Aeroluna/'+$(if($component -in @('Heck','NoodleExtensions','Chroma')) {'Heck'} else {$component})}}
    if ($component -eq 'SongCore') { $manifest.links['project-source']='https://github.com/Kylemc1413/SongCore' }
    $manifestPath=Join-Path $work ($component+'.manifest.json')
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    $metadataPath=Join-Path $work 'BuildMetadata.cs'
    $metadata='[module: System.Security.UnverifiableCode]'+[Environment]::NewLine+'[assembly: System.Reflection.AssemblyVersion("'+$properties.Version+'.0")]'+[Environment]::NewLine+'[assembly: System.Reflection.AssemblyInformationalVersion("'+$properties.Version+$localSuffix+'")]'
    [IO.File]::WriteAllText($metadataPath,$metadata)
    $output=Join-Path $pluginsDirectory ($component+'.dll')
    $arguments=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/optimize+','/unsafe+','/nostdlib+','/deterministic+','/define:RELEASE,LATEST,V1_45_2',('/out:"'+$output+'"'),('/resource:"'+$manifestPath+'",'+$component+'.manifest.json'),('"'+$metadataPath+'"'))
    $arguments += @($references.GetEnumerator() | Where-Object { $_.Key -ne $component -and $_.Key -notin @('protobuf-net','MonoMod.Backports') } | Sort-Object Key | ForEach-Object { '/r:"'+$_.Value+'"' })
    $arguments += @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Name -ne 'NullableAttributes.cs' } | ForEach-Object { '"'+$_.FullName+'"' })
    foreach ($resource in $definition.SelectNodes('//EmbeddedResource')) {
        if ($resource.Include -match '2019') { continue }
        $pattern=Join-Path $source $resource.Include
        foreach ($file in Get-ChildItem -Path $pattern -File) {
            $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','.')
            $resourceName = if ($resource.HasAttribute('LogicalName')) { $resource.GetAttribute('LogicalName') } else { $component+'.'+$relative }
            $arguments += '/resource:"'+$file.FullName+'",'+$resourceName
        }
    }
    $response=Join-Path $work 'build.rsp'
    $arguments | Set-Content -LiteralPath $response -Encoding utf8
    & $DotnetPath $compiler ('@'+$response)
    if ($LASTEXITCODE -ne 0) { throw "Source build failed: $component" }
    $references[$component]=$output
    Write-Output ('Built '+$output)
}
