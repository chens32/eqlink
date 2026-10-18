param(
    [ValidateSet('Baseline','Recovery','StableTcp','RouteConflict','ServiceCrash','All')][string]$Scenario='Baseline',
    [ValidateRange(1,20)][int]$Repeat=5,
    [string]$ServerIp,
    [int]$HealthPort=1195,
    [int]$OpenVpnPort=1194,
    [int]$ConflictInterfaceIndex=0,
    [switch]$InjectFaults,
    [switch]$Capture,
    [string]$OutputDirectory=(Join-Path $PWD ('eqlink-evidence-'+(Get-Date -Format yyyyMMdd-HHmmss)))
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$suspended=@{}; $ownRoute=$null; $captureStarted=$false; $filters=[Collections.Generic.List[string]]::new()
$report=[ordered]@{schemaVersion=1;version='0.2.0';startedAt=(Get-Date).ToUniversalTime().ToString('o');scenario=$Scenario;checks=@();timeline=@();evidence=@();cleanup=@{}}
$timeline=[Collections.Generic.List[object]]::new()
$original=$null
function Save {
    $report.checks=@($checks.ToArray());$report.timeline=@($timeline.ToArray())
    $report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
}
function Add-Check([string]$Name,[string]$Result,$Evidence) {
    $checks.Add(@{name=$Name;result=$Result;evidence=$Evidence;timestamp=(Get-Date).ToUniversalTime().ToString('o')});Save
}
function Control([hashtable]$Request) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','EqLink.Control.v1',[IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000);$data=[Text.Encoding]::UTF8.GetBytes(($Request|ConvertTo-Json -Compress)+[char]10)
        $pipe.Write($data,0,$data.Length);$pipe.Flush();$reader=[IO.StreamReader]::new($pipe)
        $read=$reader.ReadLineAsync();if(-not $read.Wait(10000)){throw 'IPC timeout'}
        $response=$read.Result|ConvertFrom-Json;if(-not $response.ok){throw $response.error};return $response
    } finally {$pipe.Dispose()}
}
function Ping-Ip([string]$Ip,[int]$TimeoutMs=1000) {
    $ping=[Net.NetworkInformation.Ping]::new()
    try {return $ping.Send($Ip,$TimeoutMs).Status -eq [Net.NetworkInformation.IPStatus]::Success}
    catch {return $false} finally {$ping.Dispose()}
}
function Wait-Network([int]$Path,[int]$Seconds=60,[string]$Code='') {
    $watch=[Diagnostics.Stopwatch]::StartNew();$trace=[Collections.Generic.List[object]]::new();$ok=$false
    do {
        $s=(Control @{command='status'}).status;$ping=Ping-Ip $ServerIp
        $row=@{at=(Get-Date).ToUniversalTime().ToString('o');seconds=[math]::Round($watch.Elapsed.TotalSeconds,2);connected=$s.connected;path=$s.path;ping=$ping;phase=$s.phase;code=$s.errorCode;allowing=$s.protection.allowing}
        $trace.Add($row);$timeline.Add($row)
        $ok=if($Path -eq 0){-not $s.connected -and -not $ping -and -not $s.protection.allowing}else{$s.connected -and [int]$s.path -eq $Path -and $ping -and $s.protection.installed -and $s.protection.allowing}
        if($Code -and $s.errorCode -ne $Code){$ok=$false}
        if($ok){break};Start-Sleep -Milliseconds 400
    }while($watch.Elapsed.TotalSeconds -lt $Seconds)
    return @{result=$(if($ok){'passed'}else{'failed'});elapsedSeconds=[math]::Round($watch.Elapsed.TotalSeconds,2);trace=@($trace.ToArray());status=$s}
}
function Connect-Mode([string]$Mode,[int]$Path) {
    Control @{command='disconnect'}|Out-Null;Control @{command='mode';mode=$Mode}|Out-Null
    Control @{command='connect'}|Out-Null;$r=Wait-Network $Path
    if($r.result -ne 'passed'){throw ('Cannot prepare virtual communication in '+$Mode)}
    return $r
}
function Owned-Sidecar([string]$Protocol) {
    $svc=Get-CimInstance Win32_Service -Filter "Name='EqLink'"
    $serviceProcess=Get-CimInstance Win32_Process -Filter ('ProcessId='+$svc.ProcessId)
    $directory=Split-Path $serviceProcess.ExecutablePath
    $name=if($Protocol -eq 'UDP'){'hysteria.exe'}else{'xray.exe'}
    $candidates=@(Get-CimInstance Win32_Process -Filter ("Name='"+$name+"'")|Where-Object {$_.ParentProcessId -eq $svc.ProcessId -and $_.ExecutablePath -eq (Join-Path $directory ('bin\'+$name))})
    if($candidates.Count -ne 1){throw 'Cannot identify exactly one EqLink-owned sidecar'}
    return Get-Process -Id $candidates[0].ProcessId
}
function Test-Outer([string]$Protocol) {
    $port=if($Protocol -eq 'UDP'){18081}else{18082};$client=[Net.Sockets.TcpClient]::new()
    try {
        if(-not $client.ConnectAsync('127.0.0.1',$port).Wait(1000)){return $false}
        $client.ReceiveTimeout=1200;$client.SendTimeout=1200;$stream=$client.GetStream()
        $data=[Text.Encoding]::ASCII.GetBytes("CONNECT 127.0.0.1:$HealthPort HTTP/1.1`r`nHost: 127.0.0.1:$HealthPort`r`n`r`n");$stream.Write($data,0,$data.Length)
        $header='';while(-not $header.EndsWith("`r`n`r`n") -and $header.Length -lt 8192){$b=$stream.ReadByte();if($b -lt 0){return $false};$header+=[char]$b}
        if($header -notmatch '^HTTP/1\.[01] 200 '){return $false}
        $data=[Text.Encoding]::ASCII.GetBytes("PING`n");$stream.Write($data,0,$data.Length)
        $answer=[byte[]]::new(5);$read=0;while($read -lt 5){$n=$stream.Read($answer,$read,5-$read);if($n -eq 0){return $false};$read+=$n}
        return [Text.Encoding]::ASCII.GetString($answer) -eq "PONG`n"
    }catch{return $false}finally{$client.Dispose()}
}
function Suspend-Outer([string]$Protocol) {
    if(-not (Test-Outer $Protocol)){throw 'Fault invalid: selected path was not healthy before injection'}
    $p=Owned-Sidecar $Protocol
    if([EqLinkAcceptanceNative]::NtSuspendProcess($p.Handle) -ne 0){$p.Dispose();throw 'Fault unavailable: suspension failed'}
    $suspended[$Protocol]=$p
    if(Test-Outer $Protocol){throw 'Fault invalid: authenticated health still succeeds after suspension'}
}
function Resume-Outer([string]$Protocol) {
    if($suspended.ContainsKey($Protocol)){
        $p=$suspended[$Protocol]
        try {if(-not $p.HasExited -and [EqLinkAcceptanceNative]::NtResumeProcess($p.Handle) -ne 0){throw 'Could not resume owned process'}}finally{$p.Dispose();$suspended.Remove($Protocol)}
    }
}
function Recovery([string]$Name,[string]$Protocol,[bool]$Kill,[int]$Target) {
    $setup=if($Protocol -eq 'UDP'){Connect-Mode 'Auto' 1}else{Connect-Mode 'TcpOnly' 2}
    if($Protocol -eq 'TCP'){Control @{command='mode';mode='Auto'}|Out-Null}
    $watch=[Diagnostics.Stopwatch]::StartNew();$valid=$false
    try {
        if($Kill){$p=Owned-Sidecar $Protocol;try{$p.Kill();$valid=$p.WaitForExit(3000)}finally{$p.Dispose()}}
        else {Suspend-Outer $Protocol;$valid=$true}
        if(-not $valid){Add-Check $Name 'unavailable' @{faultValid=$false};return}
        $r=Wait-Network $Target 60;$r.faultValid=$true;$r.fromFaultSeconds=[math]::Round($watch.Elapsed.TotalSeconds,2)
        $r.within30Seconds=($r.result -eq 'passed' -and $r.fromFaultSeconds -le 30)
        $r.within60Seconds=($r.result -eq 'passed' -and $r.fromFaultSeconds -le 60)
        if(-not $r.within60Seconds){$r.result='failed'}
        Add-Check $Name $r.result $r
        if(-not $Kill -and $Protocol -eq 'UDP' -and $r.result -eq 'passed'){
            Resume-Outer 'UDP';$stable=$true;$samples=[Collections.Generic.List[object]]::new()
            for($sample=0;$sample -lt 7;$sample++){
                Start-Sleep -Seconds 2;$s=(Control @{command='status'}).status;$probe=[Diagnostics.Stopwatch]::StartNew();$ping=Ping-Ip $ServerIp 3000
                $row=@{timestamp=(Get-Date).ToUniversalTime().ToString('o');path=$s.path;connected=$s.connected;ping=$ping;pingMs=[math]::Round($probe.Elapsed.TotalMilliseconds,1);phase=$s.phase;code=$s.errorCode}
                $samples.Add($row);$timeline.Add($row)
                if(-not $s.connected -or $s.path -ne 2 -or -not $ping){$stable=$false}
            }
            Add-Check ($Name+'-UDP-return-stable-TCP') $(if($stable){'passed'}else{'failed'}) @{seconds=14;probeTimeoutMs=3000;samples=@($samples.ToArray())}
        }
    }catch{Add-Check $Name $(if($valid){'failed'}else{'unavailable'}) @{faultValid=$valid;reason=$_.Exception.Message}}
    finally {Resume-Outer $Protocol}
}
try {
    $original=(Control @{command='status'}).status
    if(-not $original.imported){throw 'Import a device before running acceptance'}
    if(-not $original.protection.enabled){throw 'Enable EqLink subnet protection before acceptance'}
    if(-not $ServerIp){$ServerIp=($original.virtualIp -replace '\.\d+$','.1')}
    $report.serverIp=$ServerIp;$report.originalStatus=$original
    $metaBefore=@(Get-Process -ErrorAction SilentlyContinue | Where-Object {$_.ProcessName -like 'mihomo*'}|ForEach-Object {$_.Id})
    $report.metaProcessIdsBefore=$metaBefore
    if($Capture -and -not $original.connected){Connect-Mode 'Auto' 1|Out-Null}
    $diagnostics=(Control @{command='diagnostics'}).diagnostics
    $diagnostics|ConvertTo-Json -Depth 16|Set-Content -LiteralPath (Join-Path $output 'diagnostics-before.json') -Encoding UTF8
    if($Capture){
        # Refuse to change filters when another PktMon session exists.
        $sessions=(& logman.exe query -ets 2>&1|Out-String)
        if($LASTEXITCODE -ne 0 -or $sessions -match '(?im)^\s*PktMon\b'){throw 'Capture unavailable: existing session or ETW inventory inaccessible'}
        $tag='EqLink'+[Guid]::NewGuid().ToString('N').Substring(0,8)
        foreach($spec in @(@{suffix='Overlay';args=@('-i',$diagnostics.endpoint.overlayCidr)},@{suffix='Inner';args=@('-p',"$OpenVpnPort")},@{suffix='Outer';args=@('-i',$diagnostics.targetAddress)})){
            if(-not $spec.args[-1]){continue};$name=$tag+$spec.suffix
            & pktmon.exe filter add $name @($spec.args)|Out-Null;if($LASTEXITCODE -ne 0){throw 'Capture filter failed'};$filters.Add($name)
        }
        & pktmon.exe list | Set-Content -LiteralPath (Join-Path $output 'capture-components.txt') -Encoding UTF8
        & pktmon.exe filter list | Set-Content -LiteralPath (Join-Path $output 'capture-filters.txt') -Encoding UTF8
        & pktmon.exe start --capture --comp nics --pkt-size 96 --file-name (Join-Path $output 'nics.etl') --file-size 512|Out-Null
        if($LASTEXITCODE -ne 0){throw 'Capture could not start'};$captureStarted=$true
        $report.capture=@{startedAt=(Get-Date).ToUniversalTime().ToString('o');packetBytes=96;fileSizeMb=512;components='nics'}
    }
    if($Scenario -ne 'Baseline' -and -not $InjectFaults){throw 'Selected scenario requires -InjectFaults; fault injection was not performed'}
    if($InjectFaults){
        if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run fault tests as administrator'}
        Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class EqLinkAcceptanceNative{[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);}'
    }
    foreach($mode in @(@{name='Auto';path=1},@{name='UdpOnly';path=1},@{name='TcpOnly';path=2})){
        $r=Connect-Mode $mode.name $mode.path;Add-Check ('mode-'+$mode.name) $r.result $r
    }
    Add-Check 'own-virtual-IP-preserved' $(if(Ping-Ip $original.virtualIp){'passed'}else{'failed'}) @{}
    Control @{command='disconnect'}|Out-Null
    $r=Wait-Network 0 5;Add-Check 'manual-disconnect-blocks' $r.result $r
    if($Scenario -eq 'StableTcp'){
        for($i=1;$i -le $Repeat;$i++){Recovery "UDP-blackhole-to-TCP-$i" 'UDP' $false 2}
    }
    if($Scenario -in @('Recovery','All')){
        for($i=1;$i -le $Repeat;$i++){
            Recovery "UDP-blackhole-to-TCP-$i" 'UDP' $false 2
            Recovery "TCP-blackhole-to-UDP-$i" 'TCP' $false 1
            Recovery "HY2-exit-to-TCP-$i" 'UDP' $true 2
            Recovery "Xray-exit-to-UDP-$i" 'TCP' $true 1
        }
        foreach($test in @(@{mode='UdpOnly';protocol='UDP';path=1},@{mode='TcpOnly';protocol='TCP';path=2})){
            Connect-Mode $test.mode $test.path|Out-Null
            try{Suspend-Outer $test.protocol;$r=Wait-Network 0 30;if(@($r.trace|Where-Object {$_.path -ne 0 -and $_.path -ne $test.path}).Count){$r.result='failed'};Add-Check ($test.mode+'-no-fallback') $r.result $r}finally{Resume-Outer $test.protocol}
        }
        Connect-Mode 'Auto' 1|Out-Null
        try{Suspend-Outer 'UDP';Suspend-Outer 'TCP';$r=Wait-Network 0 30;Add-Check 'both-paths-unresponsive-blocked' $r.result $r}finally{Resume-Outer 'UDP';Resume-Outer 'TCP'}
    }
    if($Scenario -in @('RouteConflict','All')){
        if($ConflictInterfaceIndex -le 0){Add-Check 'route-competition' 'unavailable' @{reason='Provide -ConflictInterfaceIndex (Meta adapter)'}}else{
            Connect-Mode 'Auto' 1|Out-Null;$prefix="$ServerIp/32"
            if(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix $prefix -InterfaceIndex $ConflictInterfaceIndex -ErrorAction SilentlyContinue){throw 'Existing test route found; preserved'}
            try{
                $ownRoute=New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $ConflictInterfaceIndex -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore
                $r=Wait-Network 0 10 'ROUTE_CONFLICT';Add-Check 'more-specific-route-blocked' $r.result $r
            }finally{if($ownRoute){$ownRoute|Remove-NetRoute -Confirm:$false;$ownRoute=$null}}
            $r=Wait-Network 1 60;Add-Check 'route-conflict-removed-recovery' $r.result $r
        }
    }
    if($Scenario -in @('ServiceCrash','All')){
        Connect-Mode 'Auto' 1|Out-Null
        $svc=Get-CimInstance Win32_Service -Filter "Name='EqLink'"
        $children=@(Get-CimInstance Win32_Process|Where-Object ParentProcessId -eq $svc.ProcessId|ForEach-Object{Get-Process -Id $_.ProcessId})
        try {
            Stop-Process -Id $svc.ProcessId -Force;Start-Sleep -Seconds 3
            $dead=@($children|Where-Object{-not $_.HasExited}).Count -eq 0
            Add-Check 'service-crash-denies-and-kills-owned-children' $(if($dead -and -not (Ping-Ip $ServerIp)){'passed'}else{'failed'}) @{allOwnedChildrenExited=$dead}
        }finally{Start-Service EqLink;Start-Sleep -Seconds 2;foreach($p in $children){$p.Dispose()}}
        $r=Wait-Network 0 5;Add-Check 'service-restart-stays-disconnected' $r.result $r
    }
    foreach($gate in @('second-Windows-client-interconnect-revocation','Windows-install-uninstall-rollback-reboot','BFE-unavailable-and-firewall-profile-matrix','physical-NIC-address-index-change','DNS-timeout-address-change','target-k3s-upgrade')){
        Add-Check $gate 'skipped' @{reason='Requires a separate device, isolated environment, or remote administrator; execute the documented gate'}
    }
}catch{Add-Check 'runner' 'unavailable' @{reason=$_.Exception.Message}}
finally {
    foreach($protocol in @($suspended.Keys)){try{Resume-Outer $protocol}catch{$report.cleanup.resumeError=$_.Exception.Message}}
    if($ownRoute){try{$ownRoute|Remove-NetRoute -Confirm:$false}catch{$report.cleanup.routeError=$_.Exception.Message}}
    try{
        if($original){
            Control @{command='disconnect'}|Out-Null
            $mode=@('Auto','UdpOnly','TcpOnly')[[int]$original.mode];Control @{command='mode';mode=$mode}|Out-Null
            if($original.connected){Control @{command='connect'}|Out-Null;$restored=Wait-Network $(if($mode -eq 'TcpOnly'){2}else{1});$report.cleanup.restored=$restored.result}else{$report.cleanup.restored='passed'}
        }
        (Control @{command='diagnostics'}).diagnostics|ConvertTo-Json -Depth 16|Set-Content -LiteralPath (Join-Path $output 'diagnostics-after.json') -Encoding UTF8
    }catch{$report.cleanup.restoreError=$_.Exception.Message}
    if($captureStarted){
        & pktmon.exe stop | Set-Content -LiteralPath (Join-Path $output 'capture-stop.txt') -Encoding UTF8
        & pktmon.exe etl2pcap (Join-Path $output 'nics.etl') --out (Join-Path $output 'nics.pcapng')|Out-Null
        & pktmon.exe etl2txt (Join-Path $output 'nics.etl') --out (Join-Path $output 'nics.txt')|Out-Null
        $report.capture.stoppedAt=(Get-Date).ToUniversalTime().ToString('o')
    }
    foreach($name in $filters){& pktmon.exe filter remove $name|Out-Null}
    $metaAfter=@(Get-Process -ErrorAction SilentlyContinue|Where-Object{$_.ProcessName -like 'mihomo*'}|ForEach-Object{$_.Id})
    $report.cleanup.metaProcessIdsAfter=$metaAfter
    if($original){$report.cleanup.metaProcessesPreserved=(@(Compare-Object $metaBefore $metaAfter).Count -eq 0)}
    $report.completedAt=(Get-Date).ToUniversalTime().ToString('o')
    $report.evidence=@(Get-ChildItem -LiteralPath $output -File|Where-Object Name -ne 'result.json'|ForEach-Object{@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    Save
}
if(@($checks|Where-Object result -eq 'failed').Count -or $report.cleanup.restoreError -or @($checks|Where-Object name -eq 'runner').Count){exit 1}
Write-Output (Join-Path $output 'result.json')
