#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v?[0-9][A-Za-z0-9.-]{0,40}$')]
    [string]$Version,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$CertificateSha256,
    [string]$Compiler = 'C:\msys64\mingw32\bin\g++.exe',
    [string]$RecorderDirectory
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (!$RecorderDirectory) {
    $RecorderDirectory = Join-Path $root 'recorder'
    if (!(Test-Path -LiteralPath $RecorderDirectory -PathType Container)) {
        $RecorderDirectory = Join-Path (Split-Path -Parent $root) 'ElanciaRecorder'
    }
}
$sdk = Join-Path $root '.dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk -PathType Leaf)) { $sdk = (Get-Command dotnet -CommandType Application).Source }
$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$output = (New-Item -ItemType Directory -Path (Join-Path $root "artifacts/releases/$tag") -Force).FullName
$build = (New-Item -ItemType Directory -Path (Join-Path $root "obj/release-$([guid]::NewGuid().ToString('N'))") -Force).FullName
$server = (New-Item -ItemType Directory -Path (Join-Path $build 'server/ElanciaLocalServer') -Force).FullName
$manager = (New-Item -ItemType Directory -Path (Join-Path $build 'manager/ElanciaAccountManager') -Force).FullName
$tools = (New-Item -ItemType Directory -Path (Join-Path $build 'tools') -Force).FullName
$recorder = (New-Item -ItemType Directory -Path (Join-Path $tools 'ElanciaRecorder') -Force).FullName
$mcp = (New-Item -ItemType Directory -Path (Join-Path $tools 'ElanciaLocalServer/tools') -Force).FullName
$evidence = (New-Item -ItemType Directory -Path (Join-Path $tools 'ElanciaLocalServer/evidence') -Force).FullName
$source = Join-Path $build 'source/ElanciaLocalServer'
$sourceRecorder = Join-Path $source 'recorder'

function Copy-RelativeFile([string]$From, [string]$Destination, [string]$Relative) {
    $target = Join-Path $Destination $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $From $Relative) -Destination $target
}

function Publish-Executable([string]$Project, [string]$Destination, [string[]]$Properties) {
    & $sdk publish (Join-Path $root $Project) -c Release -r win-x64 --self-contained true -o $Destination `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugSymbols=false -p:DebugType=None @Properties
    if ($LASTEXITCODE) { throw "Publishing $Project failed." }
}

# Use the original client equipment slots in the standard player download.
$world = Get-Content -LiteralPath (Join-Path $root 'world-recorded.json') -Raw | ConvertFrom-Json -AsHashtable
$world.Remove('SourceSessions') | Out-Null
foreach ($profile in $world.MonsterHealth.Values) { $profile.Remove('Source') | Out-Null }
foreach ($item in $world.Items) {
    $item.Remove('LocalAppearanceWord') | Out-Null
    $item.Remove('AppearancePacket') | Out-Null
}
$worldPath = Join-Path $server 'world-recorded.json'
$world | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $worldPath -Encoding utf8NoBOM
Publish-Executable 'ElanciaLocalServer.csproj' $server @("-p:ElanciaWorldData=$worldPath", '-p:IncludeNativeMeta=false', '-p:ElanciaReleaseBundle=true')
Publish-Executable 'manager/ElanciaAccountManager.csproj' $manager @()
foreach ($file in @('start-location.json','world-npcs.json')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $server
}
foreach ($file in @('world-recorded.json','world-npcs.json')) {
    Copy-RelativeFile $server (Join-Path $tools 'ElanciaLocalServer') $file
}
foreach ($file in @('Start-Server.ps1','Start-Server.cmd')) {
    Copy-Item -LiteralPath (Join-Path $root "distribution/$file") -Destination $server
}
foreach ($file in @('recorder.py','recorder_core.py','capture.js','review_capture.py')) {
    Copy-Item -LiteralPath (Join-Path $RecorderDirectory $file) -Destination $recorder
    Copy-RelativeFile $RecorderDirectory $sourceRecorder $file
}
foreach ($file in @('client_assets.py','world_mcp.py','sync_recorded_world.py')) {
    Copy-Item -LiteralPath (Join-Path $root "tools/$file") -Destination $mcp
}
$catalog = Get-Content -LiteralPath (Join-Path $root 'evidence/packet-catalog.json') -Raw | ConvertFrom-Json -AsHashtable
$catalog.client = 'C:\Nexon\Elancia\jElancia.exe'
$catalog | ConvertTo-Json -Depth 100 -Compress |
    Set-Content -LiteralPath (Join-Path $evidence 'packet-catalog.json') -Encoding utf8NoBOM
foreach ($destination in @($server, $manager)) {
    foreach ($file in @('SETUP.md','TERMS-DRAFT.md','PRIVACY-DRAFT.md')) {
        Copy-RelativeFile (Join-Path $root 'docs') $destination $file
    }
}
Copy-Item -LiteralPath (Join-Path $root 'docs/RECORDING-MCP.md') -Destination $tools
Copy-RelativeFile (Join-Path $root 'docs') $tools 'TERMS-DRAFT.md'
Copy-RelativeFile (Join-Path $root 'docs') $tools 'PRIVACY-DRAFT.md'

# Copy a fixed list from the source tree. Runtime state and captured traffic stay out.
$sourceFiles = @(
    '.gitignore','README.md','ElanciaLocal.sln','ElanciaLocalServer.csproj','Build-Launcher.ps1','Build-Release.ps1',
    'start-location.json','world-npcs.json','manager/ElanciaAccountManager.csproj','manager/Program.cs',
    'launcher/main.cpp','launcher/tls.h','launcher/equipment_archive.cpp','launcher/equipment_archive.h',
    'distribution/Start-Server.cmd','distribution/Start-Server.ps1',
    'docs/SETUP.md','docs/RELEASE.md','docs/RELEASE-NOTES.md','docs/RECORDING-MCP.md','docs/TERMS-DRAFT.md','docs/PRIVACY-DRAFT.md',
    'tools/client_assets.py','tools/world_mcp.py','tools/sync_recorded_world.py','evidence/native-crypto-vectors.json'
)
$sourceFiles += @(Get-ChildItem -LiteralPath $root -File -Filter '*.cs' |
    Where-Object Name -NotIn @('IntegrationTests.cs','SelfTests.cs','LoginPacket.cs','PacketFixtures.cs') | ForEach-Object Name)
foreach ($file in $sourceFiles) { Copy-RelativeFile $root $source $file }
Copy-RelativeFile $server $source 'world-recorded.json'
Copy-RelativeFile (Join-Path $tools 'ElanciaLocalServer') $source 'evidence/packet-catalog.json'

$launcher = (New-Item -ItemType Directory -Path (Join-Path $build 'launcher/ElanciaLauncher') -Force).FullName
$launcherBuild = @{ Compiler = $Compiler; OutputDirectory = $launcher }
if ($CertificateSha256) { $launcherBuild.CertificateSha256 = $CertificateSha256 }
& (Join-Path $root 'Build-Launcher.ps1') @launcherBuild
Copy-RelativeFile (Join-Path $root 'docs') $launcher 'SETUP.md'
Copy-RelativeFile (Join-Path $root 'docs') $launcher 'TERMS-DRAFT.md'
foreach ($destination in @($server,$manager,$launcher,$source)) {
    Copy-RelativeFile (Join-Path $root 'docs') $destination 'THIRD-PARTY.md'
}
Copy-RelativeFile $root $source 'docs/THIRD-PARTY.md'
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $runtime = Get-ChildItem -LiteralPath (Join-Path $nuget $package) -Directory |
        Where-Object Name -Match '^10\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$runtime) { continue }
    $destinations = if ($package -like 'microsoft.netcore.*') { @($server,$manager) } else { @($manager) }
    foreach ($destination in $destinations) {
        foreach ($file in (Get-ChildItem -LiteralPath $runtime.FullName -File |
                Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')) {
            $noticeDirectory = Join-Path $destination "notices/$package"
            Copy-RelativeFile $runtime.FullName $noticeDirectory $file.Name
        }
    }
}
$compilerBin = Split-Path -Parent (Get-Command $Compiler -CommandType Application).Source
$compilerNotices = Join-Path (Split-Path -Parent $compilerBin) 'share/licenses'
foreach ($notice in @('zlib/LICENSE','gcc/COPYING.RUNTIME','gcc/COPYING3','libwinpthread/COPYING')) {
    if (Test-Path -LiteralPath (Join-Path $compilerNotices $notice) -PathType Leaf) {
        Copy-RelativeFile $compilerNotices (Join-Path $launcher 'notices') $notice
    }
}

$archives = [ordered]@{
    "Elancia-Server-$tag-win-x64.zip" = @($server)
    "Elancia-AccountManager-$tag-win-x64.zip" = @($manager)
    "Elancia-Recorder-MCP-$tag.zip" = @(Get-ChildItem -LiteralPath $tools | ForEach-Object FullName)
    "Elancia-Launcher-$tag-win-x86.zip" = @($launcher)
    "Elancia-Source-$tag.zip" = @($source)
}
foreach ($entry in $archives.GetEnumerator()) {
    Compress-Archive -LiteralPath $entry.Value -DestinationPath (Join-Path $build $entry.Key)
}
$hashes = foreach ($name in $archives.Keys) {
    $zip = Join-Path $build $name
    "$( (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() )  $name"
    Copy-Item -LiteralPath $zip -Destination (Join-Path $output $name) -Force
}
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
foreach ($file in @('RELEASE.md','RELEASE-NOTES.md','TERMS-DRAFT.md')) {
    Copy-Item -LiteralPath (Join-Path $root "docs/$file") -Destination $output -Force
}
Write-Host "Release packages $output"
