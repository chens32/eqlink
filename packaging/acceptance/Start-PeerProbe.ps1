param(
    [Parameter(Mandatory=$true)][string]$VirtualIp,
    [Parameter(Mandatory=$true)][string]$OverlayCidr,
    [Parameter(Mandatory=$true)][string]$Executable,
    [ValidateRange(1024,65535)][int]$Port=36555,
    [ValidateRange(1,3600)][int]$Seconds=180,
    [ValidateSet("Echo","Traffic")][string]$Mode="Echo",
    [string]$OutputDirectory=(Join-Path $PWD ("peer-session-"+(Get-Date -Format yyyyMMdd-HHmmss)))
)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator to create scoped temporary inbound rules'}
$ip=[Net.IPAddress]::Parse($VirtualIp)
$octets=$ip.GetAddressBytes()
if($octets.Length -ne 4 -or $OverlayCidr -ne (($VirtualIp -replace '\.\d+$','.0')+'/24')){throw 'Virtual IP and /24 must match'}
$local=Get-NetIPAddress -AddressFamily IPv4 -IPAddress $VirtualIp
$adapter=Get-NetAdapter -InterfaceIndex $local.InterfaceIndex
if($adapter.InterfaceDescription -notlike '*TAP-Windows*'){throw 'Bind only to the EqLink TAP virtual adapter'}
$exe=(Resolve-Path -LiteralPath $Executable).Path
$tag='EqLink-PeerTest-'+[Guid]::NewGuid().ToString('N')
$rules=[Collections.Generic.List[string]]::new()
$process=$null
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$session=@{schemaVersion=1;version="1.0.0";startedAt=(Get-Date).ToUniversalTime().ToString("o");bind=$VirtualIp;port=$Port;mode=$Mode;cleanup=@{}}
try {
    foreach($protocol in @('TCP','UDP','ICMPv4')){
        $name=$tag+'-'+$protocol
        $ruleArgs=@{Name=$name;DisplayName=$name;Direction='Inbound';Action='Allow';Protocol=$protocol;LocalAddress=$VirtualIp;RemoteAddress=$OverlayCidr;InterfaceAlias=$adapter.Name;Profile='Any'}
        if($protocol -ne 'ICMPv4'){$ruleArgs.LocalPort=$Port;$ruleArgs.Program=$exe}else{$ruleArgs.IcmpType=8}
        New-NetFirewallRule @ruleArgs|Out-Null;$rules.Add($name)
    }
    $command=if($Mode -eq 'Traffic'){'stream-serve'}else{'serve'}
    $process=Start-Process -FilePath $exe -ArgumentList @($command,'--bind',$VirtualIp,'--port',$Port,'--seconds',$Seconds) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory 'listener.log') -RedirectStandardError (Join-Path $OutputDirectory 'listener-error.log')
    Write-Host ('READY '+$VirtualIp+':'+$Port+'; keep this session open.')
    $process.WaitForExit()
    if($process.ExitCode -ne 0){throw 'Peer server failed'}
}finally{
    if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit()}
    $failed=[Collections.Generic.List[string]]::new()
    foreach($name in $rules){try{Remove-NetFirewallRule -Name $name -ErrorAction Stop}catch{$failed.Add($name)}}
    $session.cleanup=@{processStopped=(!$process -or $process.HasExited);rulesRemoved=($failed.Count -eq 0);failedRuleNames=@($failed.ToArray())}
    $session.completedAt=(Get-Date).ToUniversalTime().ToString('o')
    $session|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputDirectory 'session.json') -Encoding UTF8
    if($process){$process.Dispose()}
}
