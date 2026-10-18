param(
    [Parameter(Mandatory=$true)][string]$OldExecutable,
    [Parameter(Mandatory=$true)][string]$NewExecutable,
    [Parameter(Mandatory=$true)][string]$WixDll,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
# Tests MSI version/costing and transaction behavior in a dedicated directory.
# It never installs a service, OpenVPN, or any WFP rule. Full product lifecycle remains a VM gate.
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required'}
$folder=[IO.Path]::GetFullPath($OutputDirectory);New-Item -ItemType Directory -Path $folder -Force|Out-Null
$tag='EqLink-MsiFixture-'+[Guid]::NewGuid().ToString('N')
$upgrade=[Guid]::NewGuid().ToString();$component=[Guid]::NewGuid().ToString()
$codes=@{};$installed=$null
$policy=[xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot '../EqLink.wxs'))
$setting=$policy.Wix.Package.SetProperty.OuterXml
if($setting -notmatch 'REINSTALLMODE' -or $setting -notmatch 'amus'){throw 'Production MSI upgrade rule missing'}
$report=[ordered]@{schemaVersion=1;scope='Dedicated MSI fixture; excludes product services, WFP and OpenVPN';result='failed';checks=@()}
function Msi([string[]]$Options,[string]$Log){
    $process=Start-Process msiexec.exe -ArgumentList ($Options+@('/qn','/norestart','/l*v',('"'+$Log+'"'))) -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(60000)){throw 'Fixture installer timeout'}
    return $process.ExitCode
}
try {
    foreach($version in @('0.1.2','0.2.0','0.3.0')){
        $codes[$version]=[Guid]::NewGuid().ToString()
        $source=if($version -eq '0.1.2'){(Resolve-Path -LiteralPath $OldExecutable).Path}else{(Resolve-Path -LiteralPath $NewExecutable).Path}
        $escape=[Security.SecurityElement]::Escape($source)
        $fail=if($version -eq '0.3.0'){'<CustomAction Id="FailFixture" Directory="SystemFolder" ExeCommand="cmd.exe /c exit 1" Execute="deferred" Impersonate="no" Return="check"/><InstallExecuteSequence><Custom Action="FailFixture" Before="InstallFiles" Condition="1"/></InstallExecuteSequence>'}else{''}
        $xml=@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Package Name="$tag" Manufacturer="EqLink Tests" Version="$version" ProductCode="$($codes[$version])" UpgradeCode="$upgrade" Scope="perMachine">
<MediaTemplate EmbedCab="yes"/>
<MajorUpgrade DowngradeErrorMessage="Newer fixture installed" Schedule="afterInstallInitialize"/>$setting$fail
<StandardDirectory Id="ProgramFiles64Folder"><Directory Id="FIXTURE" Name="$tag"><Component Id="FixtureComponent" Guid="$component"><File Id="Client" Name="client.exe" Source="$escape" KeyPath="yes"/></Component></Directory></StandardDirectory>
<Feature Id="Main" Level="1"><ComponentRef Id="FixtureComponent"/></Feature></Package></Wix>
"@
        $path=Join-Path $folder ($version+'.wxs');[IO.File]::WriteAllText($path,$xml,[Text.UTF8Encoding]::new($false))
        & dotnet $WixDll build $path -arch x64 -out (Join-Path $folder ($version+'.msi'))
        if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
    }
    $target=Join-Path $env:ProgramFiles ($tag+'\client.exe')
    foreach($version in @('0.1.2','0.2.0')){
        $code=Msi @('/i',('"'+(Join-Path $folder ($version+'.msi'))+'"')) (Join-Path $folder ($version+'.log'))
        if($code -ne 0){throw ('Fixture install failed: '+$version)};$installed=$version
        $expected=if($version -eq '0.1.2'){$OldExecutable}else{$NewExecutable}
        $match=(Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $expected).Hash
        $report.checks+=@{name=('installed-'+$version);result=$(if($match){'passed'}else{'failed'});fileVersion=(Get-Item -LiteralPath $target).VersionInfo.FileVersion}
        if(-not $match){throw 'File version migration skipped the expected file'}
    }
    $code=Msi @('/i',('"'+(Join-Path $folder '0.3.0.msi')+'"')) (Join-Path $folder 'rollback.log')
    $restored=$code -eq 1603 -and (Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $NewExecutable).Hash
    $report.checks+=@{name='transaction-failure-restores-original';result=$(if($restored){'passed'}else{'failed'});installerExit=$code}
    if(-not $restored){throw 'Fixture rollback failed'}
    $report.result='passed'
}catch{$report.error=$_.Exception.Message}
finally {
    if($installed){$code=Msi @('/x',('{'+$codes[$installed]+'}')) (Join-Path $folder 'uninstall.log');$report.cleanupExit=$code;if($code -ne 0){$report.result='failed'}}
    $report.timestamp=(Get-Date).ToUniversalTime().ToString('o')
    $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $folder 'result.json') -Encoding UTF8
}
if($report.result -ne 'passed'){exit 1}
