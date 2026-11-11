param(
    [string]$ClientDir = 'C:\Nexon\Elancia',
    [ValidateSet('127.0.0.1')]
    [string]$Bind = '127.0.0.1',
    [switch]$InitializeOnly
)
$ErrorActionPreference = 'Stop'
# Use the module for the shell running this script, even when launched from another PowerShell version.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Security/Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
Push-Location -LiteralPath $PSScriptRoot
try {
    $server = (Resolve-Path -LiteralPath 'ElanciaLocalServer.exe').Path
    $secrets = (New-Item -ItemType Directory -Path 'data/public' -Force).FullName
    $certificate = Join-Path $secrets 'server.pfx'
    $passwordFile = Join-Path $secrets 'certificate-password.dpapi'
    $hasCertificate = Test-Path -LiteralPath $certificate -PathType Leaf
    $hasPassword = Test-Path -LiteralPath $passwordFile -PathType Leaf
    if ($hasCertificate -ne $hasPassword) { throw 'Restore the PFX and DPAPI password file as a pair.' }
    if (!$hasCertificate) {
        $random = [byte[]]::new(32)
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($random) } finally { $rng.Dispose() }
        $env:ELANCIA_TLS_PASSWORD = [Convert]::ToBase64String($random)
        [Array]::Clear($random,0,$random.Length)
        $pin = & $server --make-tls-cert $certificate
        if ($LASTEXITCODE -or $pin -notmatch '^[A-F0-9]{64}$') { throw 'Certificate creation failed.' }
        ConvertTo-SecureString $env:ELANCIA_TLS_PASSWORD -AsPlainText -Force |
            ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile -Encoding ascii
    } else {
        $password = Get-Content -LiteralPath $passwordFile | ConvertTo-SecureString
        try { $env:ELANCIA_TLS_PASSWORD = [Net.NetworkCredential]::new('', $password).Password }
        finally { $password.Dispose() }
        $leaf = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificate, $env:ELANCIA_TLS_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $pin = [BitConverter]::ToString($sha.ComputeHash($leaf.RawData)).Replace('-', '') }
            finally { $sha.Dispose() }
        } finally { $leaf.Dispose() }
    }
    $pin | Set-Content -LiteralPath 'certificate.sha256' -Encoding ascii
    Write-Host "Server certificate SHA-256 $pin"
    if ($InitializeOnly) { return }
    $client = (Resolve-Path -LiteralPath $ClientDir).Path
    if (!(Test-Path -LiteralPath (Join-Path $client 'Profiles/Meta.dat') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $client 'Maps') -PathType Container)) {
        throw 'The client directory must contain Profiles/Meta.dat and Maps.'
    }
    & $server --public --tls-cert $certificate --bind $Bind --client-dir $client --no-web
    if ($LASTEXITCODE) { throw "Server exited with code $LASTEXITCODE." }
} finally {
    Remove-Item Env:ELANCIA_TLS_PASSWORD -ErrorAction SilentlyContinue
    Pop-Location
}
