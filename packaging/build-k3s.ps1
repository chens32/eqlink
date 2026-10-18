param(
    [string]$Distribution = 'Debian',
    [switch]$ReuseRootfs
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-Linux([string[]]$Arguments) {
    & wsl -d $Distribution -u root --exec @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Linux build/validation failed.' }
}
$linuxRepo = (& wsl -d $Distribution --exec wslpath -u $repo.Replace('\','/')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'WSL path conversion failed.' }
$pythonPath = (Get-Command python).Source
$linuxPython = (& wsl -d $Distribution --exec wslpath -u $pythonPath.Replace('\','/')).Trim()
& (Join-Path $PSScriptRoot 'build-server.ps1') -Arch arm64
if ($LASTEXITCODE -ne 0) { throw 'ARM64 publish failed.' }
$downloads = Join-Path $repo 'artifacts\downloads'
dotnet publish (Join-Path $repo 'src\EqLink.Tests\EqLink.Tests.csproj') -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o (Join-Path $repo 'artifacts\k3s\test-app')
if ($LASTEXITCODE -ne 0) { throw 'ARM64 test runner publish failed.' }
if (-not $ReuseRootfs) {
    Invoke-Linux -Arguments @('env', "EQLINK_WINDOWS_PYTHON=$linuxPython", 'bash', "$linuxRepo/packaging/k3s/build-rootfs.sh", $linuxRepo)
}
$rootfs = (Get-Content (Join-Path $repo 'artifacts\k3s\rootfs-path.txt') -Raw).Trim()
Invoke-Linux -Arguments @('python3', "$linuxRepo/packaging/k3s/make-image.py", '--repo', $linuxRepo, '--rootfs', $rootfs)
Invoke-Linux -Arguments @('bash', "$linuxRepo/packaging/k3s/run-smoke.sh", $linuxRepo)
python (Join-Path $PSScriptRoot 'k3s\validate-build.py')
if ($LASTEXITCODE -ne 0) { throw 'Package validation failed.' }
python (Join-Path $PSScriptRoot 'k3s\pack.py')
if ($LASTEXITCODE -ne 0) { throw 'Archive packaging failed.' }
