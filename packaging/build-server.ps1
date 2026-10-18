param([ValidateSet('amd64','arm64')][string]$Arch = 'amd64')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$downloads = Join-Path $repo 'artifacts\downloads'
$rid = if ($Arch -eq 'amd64') { 'linux-x64' } else { 'linux-arm64' }
$suffix = if ($Arch -eq 'amd64') { 'x64' } else { 'arm64' }
$hysteriaKey = "hysteria_linux_$Arch"
$xrayKey = "xray_linux_$Arch"
$runtimeKey = "dotnet_runtime_linux_$suffix"
$hostKey = "dotnet_host_linux_$suffix"
$aspnetKey = "dotnet_aspnet_runtime_linux_$suffix"
python (Join-Path $PSScriptRoot 'fetch_dependencies.py') $hysteriaKey $xrayKey $runtimeKey $hostKey $aspnetKey dotnet_illink_tasks hysteria_license
if ($LASTEXITCODE -ne 0) { throw 'Dependency hash verification failed.' }
$stage = Join-Path $repo "artifacts\server\eqlink-server-$Arch"
$stageFull = [IO.Path]::GetFullPath($stage)
$expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\server')) + [IO.Path]::DirectorySeparatorChar
if (-not $stageFull.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageFull | Out-Null
dotnet publish (Join-Path $repo 'src\EqLink.Server\EqLink.Server.csproj') -c Release -r $rid --self-contained true -p:PublishSingleFile=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o $stageFull
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
$hysteriaAsset = if ($Arch -eq 'amd64') { 'hysteria-linux-amd64' } else { 'hysteria-linux-arm64' }
Copy-Item -LiteralPath (Join-Path $downloads $hysteriaAsset) -Destination (Join-Path $stageFull 'hysteria') -Force
$xrayAsset = if ($Arch -eq 'amd64') { 'Xray-linux-64.zip' } else { 'Xray-linux-arm64-v8a.zip' }
$archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $downloads $xrayAsset))
try {
    foreach ($name in @('xray','LICENSE')) {
        $entry = $archive.GetEntry($name)
        if ($null -eq $entry) { throw "Xray archive lacks $name" }
        $target = if ($name -eq 'xray') { Join-Path $stageFull 'xray' } else { Join-Path $stageFull 'xray-LICENSE' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath (Join-Path $downloads 'LICENSE.md') -Destination (Join-Path $stageFull 'hysteria-LICENSE.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-server.sh') -Destination (Join-Path $stageFull 'install-server.sh') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'firewall-preview.py') -Destination (Join-Path $stageFull 'firewall-preview.py') -Force
$archivePath = Join-Path $repo "artifacts\server\eqlink-server-$Arch.tar.gz"
tar -C $stageFull -czf $archivePath .
if ($LASTEXITCODE -ne 0) { throw 'Server archive failed.' }
Write-Host "Built $archivePath"
