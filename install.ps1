# HikTab - instalador do componente do Windows (por usuario, nao exige administrador).
# Uso: install.cmd [enderecos do DVR para Sites Confiaveis...]
#   ex.: install.cmd 192.168.0.64 192.168.0.65

param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Sites = @())

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$extId = 'gopcobokmjhfhhhifedbgdnjpgcfgaep'
$hostName = 'com.hiktab.host'
$installDir = Join-Path $env:LOCALAPPDATA 'HikTab'
$exe = Join-Path $installDir 'HikTabHost.exe'
$manifestPath = Join-Path $installDir "$hostName.json"

Write-Host '== HikTab: instalando componente ==' -ForegroundColor Cyan

# 1. Compila o host (32 bits, para plugins ActiveX como o Hikvision WebComponents)
New-Item -ItemType Directory -Force $installDir | Out-Null
Get-Process HikTabHost -ErrorAction SilentlyContinue | Stop-Process -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw '.NET Framework 4 nao encontrado (csc.exe).' }
& $csc /nologo /target:winexe /platform:x86 /optimize+ "/out:$exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    (Join-Path $root 'host\HikTabHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o HikTabHost.' }
Write-Host "Host compilado em $exe"

# 2. Manifesto do Native Messaging (UTF-8 sem BOM)
$manifest = [ordered]@{
    name            = $hostName
    description     = 'HikTab - motor do Internet Explorer para o Chrome'
    path            = $exe
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$extId/")
} | ConvertTo-Json
[IO.File]::WriteAllText($manifestPath, $manifest, (New-Object Text.UTF8Encoding $false))

foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
    $key = "HKCU:\Software\$browser\NativeMessagingHosts\$hostName"
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-Item -Path $key -Value $manifestPath
}
Write-Host 'Native Messaging registrado (Chrome e Edge).'

# 3. Motor IE11 respeitando X-UA-Compatible das paginas (11000)
$fc = 'HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION'
if (-not (Test-Path $fc)) { New-Item -Path $fc -Force | Out-Null }
New-ItemProperty -Path $fc -Name 'HikTabHost.exe' -Value 11000 -PropertyType DWord -Force | Out-Null
Write-Host 'Modo IE11 configurado.'

# 4. Opcional: adiciona os DVRs aos Sites Confiaveis (necessario para alguns plugins ActiveX)
$zoneMap = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap'
foreach ($site in $Sites) {
    $h = ($site -replace '^[a-z]+://', '') -replace '[:/].*$', ''
    if (-not $h) { continue }
    if ($h -match '^[\d\.\*]+$') {
        $key = Join-Path $zoneMap ('Ranges\HikTab_' + ($h -replace '[\.\*]', '_'))
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -Path $key -Name ':Range' -Value $h -PropertyType String -Force | Out-Null
    } else {
        $key = Join-Path $zoneMap "Domains\$h"
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    }
    foreach ($scheme in 'http', 'https') {
        New-ItemProperty -Path $key -Name $scheme -Value 2 -PropertyType DWord -Force | Out-Null
    }
    Write-Host "Sites Confiaveis: $h"
}

Write-Host ''
Write-Host 'Pronto! Agora, no Chrome:' -ForegroundColor Green
Write-Host '  1. Abra chrome://extensions e ative o "Modo do desenvolvedor".'
Write-Host '  2. Clique em "Carregar sem compactacao" e escolha a pasta:'
Write-Host "     $(Join-Path $root 'extension')"
Write-Host "  3. Confira se o ID da extensao e: $extId"
