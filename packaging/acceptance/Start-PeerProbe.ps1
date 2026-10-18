param(
    [Parameter(Mandatory=$true)][string]$VirtualIp,
    [Parameter(Mandatory=$true)][string]$OverlayCidr,
    [Parameter(Mandatory=$true)][string]$Executable,
    [ValidateRange(1024,65535)][int]$Port=36555,
    [ValidateRange(1,3600)][int]$Seconds=180
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
try {
    foreach($protocol in @('TCP','UDP','ICMPv4')){
        $name=$tag+'-'+$protocol
        $ruleArgs=@{Name=$name;DisplayName=$name;Direction='Inbound';Action='Allow';Protocol=$protocol;LocalAddress=$VirtualIp;RemoteAddress=$OverlayCidr;InterfaceAlias=$adapter.Name;Profile='Any'}
        if($protocol -ne 'ICMPv4'){$ruleArgs.LocalPort=$Port;$ruleArgs.Program=$exe}else{$ruleArgs.IcmpType=8}
        New-NetFirewallRule @ruleArgs|Out-Null;$rules.Add($name)
    }
    & $exe serve --bind $VirtualIp --port $Port --seconds $Seconds
    if($LASTEXITCODE -ne 0){throw 'Peer server failed'}
}finally{foreach($name in $rules){Remove-NetFirewallRule -Name $name}}
