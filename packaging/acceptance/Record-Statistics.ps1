param(
    [ValidateRange(5,600)][int]$Seconds=60,
    [string]$Output=(Join-Path $PWD ('statistics-'+(Get-Date -Format yyyyMMdd-HHmmss)+'.json'))
)
$ErrorActionPreference='Stop'
$samples=[Collections.Generic.List[object]]::new()
try {
    for($i=0;$i -lt $Seconds;$i++){
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','EqLink.Control.v1',[IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(3000);$bytes=[Text.Encoding]::UTF8.GetBytes('{"command":"status"}'+[char]10);$pipe.Write($bytes,0,$bytes.Length)
            $reader=[IO.StreamReader]::new($pipe);$read=$reader.ReadLineAsync();if(-not $read.Wait(5000)){throw 'Status request timeout'}
            $response=$read.Result|ConvertFrom-Json;if(-not $response.ok){throw 'Status unavailable'}
            $s=$response.status
            $samples.Add(@{timestamp=(Get-Date).ToUniversalTime().ToString('o');phase=$s.phase;connected=$s.connected;virtualIp=$s.virtualIp;metrics=$s.metrics})
            Write-Host ('TCP={0} up={1} B/s down={2} B/s phase={3}' -f $s.metrics.activeTcpConnections,$s.metrics.uploadBytesPerSecond,$s.metrics.downloadBytesPerSecond,$s.phase)
        }finally{$pipe.Dispose()}
        Start-Sleep -Seconds 1
    }
}finally{
    @{schemaVersion=1;version='1.0.0';result='recorded';samples=@($samples.ToArray())}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $Output -Encoding UTF8
}
