param(
    [Parameter(Mandatory=$true)][string]$CertificatePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedSha256,
    [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
$cert=$null
$sha=[Security.Cryptography.SHA256]::Create()
$store=$null
try {
    $path=(Resolve-Path -LiteralPath $CertificatePath).Path
    $bytes=[IO.File]::ReadAllBytes($path)
    if([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($bytes) -ne [Security.Cryptography.X509Certificates.X509ContentType]::Cert){throw 'Provide a public X.509 certificate file, not a key container or certificate store.'}
    $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)
    $actual=[BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-','')
    if($actual -ne $ExpectedSha256.ToUpperInvariant()){throw 'Certificate SHA-256 differs from the administrator-verified root. No certificate was installed.'}
    if($cert.HasPrivateKey){throw 'Provide a public root certificate without a private key.'}
    $constraints=@($cert.Extensions | Where-Object {$_.Oid.Value -eq '2.5.29.19'})
    if($constraints.Count -ne 1 -or -not $constraints[0].CertificateAuthority){throw 'The selected certificate is not a CA certificate.'}
    if($cert.Subject -ne $cert.Issuer){throw 'Provide the dedicated root CA, not a server or intermediate certificate.'}
    $now=[DateTime]::UtcNow
    if($now -lt $cert.NotBefore.ToUniversalTime() -or $now -ge $cert.NotAfter.ToUniversalTime()){throw 'The root is outside its validity period. Check the certificate and local clock.'}
    $store=[Security.Cryptography.X509Certificates.X509Store]::new('Root','LocalMachine')
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $trusted=@($store.Certificates | Where-Object { [BitConverter]::ToString($sha.ComputeHash($_.RawData)).Replace('-','') -eq $actual }).Count -gt 0
    $store.Close()
    $changed=$false
    if(-not $CheckOnly -and -not $trusted){
        $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this command in an administrator PowerShell to install the verified root for the EqLink service.'}
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($cert)
        $store.Close()
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $trusted=@($store.Certificates | Where-Object { [BitConverter]::ToString($sha.ComputeHash($_.RawData)).Replace('-','') -eq $actual }).Count -gt 0
        if(-not $trusted){throw 'The root was not found in LocalMachine Root after installation.'}
        $changed=$true
    }
    [pscustomobject]@{
        schemaVersion=1
        timestamp=[DateTime]::UtcNow.ToString('o')
        certificateSha256=$actual
        subject=$cert.Subject
        notBefore=$cert.NotBefore.ToUniversalTime().ToString('o')
        notAfter=$cert.NotAfter.ToUniversalTime().ToString('o')
        certificateVerified=$true
        store='LocalMachine\Root'
        localMachineTrusted=$trusted
        changed=$changed
        checkOnly=[bool]$CheckOnly
    } | ConvertTo-Json
} finally {
    if($store){$store.Dispose()}
    if($cert){$cert.Dispose()}
    $sha.Dispose()
}
