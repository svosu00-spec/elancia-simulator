param(
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$CertificateSha256,
    [string]$Compiler = 'C:\msys64\mingw32\bin\g++.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/launcher')
)
$ErrorActionPreference = 'Stop'
$compilerPath = (Get-Command $Compiler -CommandType Application).Source
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$generated = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot "obj/launcher-$([guid]::NewGuid().ToString('N'))") -Force).FullName
Set-Content -LiteralPath (Join-Path $generated 'server_config.h') -Encoding ascii -Value (
    'inline constexpr wchar_t ServerCertificateSha256[] = L"{0}";' -f ([string]$CertificateSha256).ToUpperInvariant()
)
$previousPath = $env:PATH
try {
    $env:PATH = "$(Split-Path -Parent $compilerPath);$previousPath"
    & $compilerPath -std=c++17 -O2 -Wall -Wextra -DUNICODE -D_UNICODE -D_WIN32_WINNT=0x0601 -DWINVER=0x0601 `
        -static -municode -mwindows '-Wl,--subsystem,windows:6.01' -I $generated `
        (Join-Path $PSScriptRoot 'launcher/main.cpp') (Join-Path $PSScriptRoot 'launcher/equipment_archive.cpp') `
        -o (Join-Path $output 'ElanciaLauncher.exe') -lws2_32 -lsecur32 -lcrypt32 -lcomdlg32 -lz
    if ($LASTEXITCODE) { throw 'Launcher compilation failed.' }
} finally {
    $env:PATH = $previousPath
}
