$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_HOME=Join-Path $repo '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$downloads=Join-Path $repo 'artifacts/downloads'
$stage=Join-Path $repo 'artifacts/tools/eqlink-tests-0.2.0'
New-Item -ItemType Directory -Path $stage -Force|Out-Null
foreach($rid in @('win-x64','linux-x64','linux-arm64')){
    dotnet publish (Join-Path $repo 'src/EqLink.PeerProbe/EqLink.PeerProbe.csproj') -c Release -r $rid --self-contained true -p:PublishSingleFile=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o (Join-Path $stage $rid)
    if($LASTEXITCODE -ne 0){throw 'Peer probe publish failed'}
}
dotnet publish (Join-Path $repo 'src/EqLink.WindowsTests/EqLink.WindowsTests.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:RestoreSources=$downloads" -p:NuGetAudit=false -o (Join-Path $stage 'win-x64/native-tests')
if($LASTEXITCODE -ne 0){throw 'Native WFP test publish failed'}
Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb'|Remove-Item
Copy-Item -Path (Join-Path $PSScriptRoot 'acceptance/*') -Destination $stage -Force -Exclude '__pycache__'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $repo 'artifacts/tools/eqlink-tests-0.2.0.zip') -Force
Write-Host 'Built artifacts/tools/eqlink-tests-0.2.0.zip'
