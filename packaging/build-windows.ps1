param(
    [string]$WixDll = "",
    [string]$BalExtension = "",
    [string]$UtilExtension = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$downloads = Join-Path $repo 'artifacts\downloads'
$package = Join-Path $repo 'artifacts\package'
$stage = Join-Path $package 'stage'
if (-not $WixDll) { $WixDll = Join-Path $repo 'artifacts\wix\wix.5.0.2\tools\net6.0\any\wix.dll' }
if (-not $BalExtension) { $BalExtension = Join-Path $repo 'artifacts\wix\wixtoolset.bal.wixext.5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll' }
if (-not $UtilExtension) { $UtilExtension = Join-Path $repo 'artifacts\wix\wixtoolset.util.wixext.5.0.2\wixext5\WixToolset.Util.wixext.dll' }
python (Join-Path $PSScriptRoot 'fetch_dependencies.py') openvpn_windows_amd64 hysteria_windows_amd64 xray_windows_amd64 hysteria_license dotnet_runtime_win_x64 dotnet_desktop_runtime_win_x64 dotnet_aspnet_runtime_win_x64 dotnet_illink_tasks wix_cli wix_bal wix_util
if ($LASTEXITCODE -ne 0) { throw 'Dependency hash verification failed.' }
if (-not (Test-Path -LiteralPath $WixDll)) {
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $downloads 'wix.5.0.2.nupkg'), (Join-Path $repo 'artifacts\wix\wix.5.0.2'))
}
if (-not (Test-Path -LiteralPath $BalExtension)) {
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $downloads 'wixtoolset.bal.wixext.5.0.2.nupkg'), (Join-Path $repo 'artifacts\wix\wixtoolset.bal.wixext.5.0.2'))
}
if (-not (Test-Path -LiteralPath $UtilExtension)) {
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $downloads 'wixtoolset.util.wixext.5.0.2.nupkg'), (Join-Path $repo 'artifacts\wix\wixtoolset.util.wixext.5.0.2'))
}
if (-not (Test-Path -LiteralPath $WixDll) -or -not (Test-Path -LiteralPath $BalExtension) -or -not (Test-Path -LiteralPath $UtilExtension)) { throw 'WiX extraction failed.' }
$openVpnMsi = Join-Path $downloads 'OpenVPN-2.7.7-I001-amd64.msi'
$signature = Get-AuthenticodeSignature -LiteralPath $openVpnMsi
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*OpenVPN Inc.*') {
    throw 'OpenVPN MSI Authenticode verification failed.'
}

$stageFull = [IO.Path]::GetFullPath($stage)
$expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\package')) + [IO.Path]::DirectorySeparatorChar
if (-not $stageFull.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageFull | Out-Null
dotnet publish -m:1 -p:UseSharedCompilation=false (Join-Path $repo 'src\EqLink.ClientService\EqLink.ClientService.csproj') -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
dotnet publish -m:1 -p:UseSharedCompilation=false (Join-Path $repo 'src\EqLink.ClientGui\EqLink.ClientGui.csproj') -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -File | Remove-Item -Force

$bin = Join-Path $stage 'bin'
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $bin,$licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $downloads 'hysteria-windows-amd64.exe') -Destination (Join-Path $bin 'hysteria.exe') -Force
Copy-Item -LiteralPath (Join-Path $downloads 'LICENSE.md') -Destination (Join-Path $licenses 'hysteria-LICENSE.md') -Force
$xrayArchive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $downloads 'Xray-windows-64.zip'))
try {
    foreach ($entryName in @('xray.exe','LICENSE')) {
        $entry = $xrayArchive.GetEntry($entryName)
        if ($null -eq $entry) { throw "Xray archive lacks $entryName" }
        $target = if ($entryName -eq 'xray.exe') { Join-Path $bin 'xray.exe' } else { Join-Path $licenses 'xray-LICENSE' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $xrayArchive.Dispose() }

python (Join-Path $PSScriptRoot 'prepare-licenses.py') --target $stage --windows --runtime-pack dotnet_runtime_win_x64 --runtime-pack dotnet_desktop_runtime_win_x64
if ($LASTEXITCODE -ne 0) { throw 'License notice collection failed.' }

dotnet $WixDll build (Join-Path $PSScriptRoot 'EqLink.wxs') -arch x64 -bindpath "Stage=$stage" -out (Join-Path $package 'EqLink.msi')
if ($LASTEXITCODE -ne 0) { throw 'EqLink MSI build failed.' }
dotnet $WixDll build (Join-Path $PSScriptRoot 'EqLink.Bundle.wxs') -arch x64 -ext $BalExtension -ext $UtilExtension -bindpath "Dependencies=$downloads" -bindpath "Package=$package" -out (Join-Path $package 'EqLink-Setup.exe')
if ($LASTEXITCODE -ne 0) { throw 'EqLink bundle build failed.' }
Write-Host "Built $(Join-Path $package 'EqLink-Setup.exe')"
