# HikTab - remove o componente do Windows e as configuracoes feitas pelo install.ps1.

$hostName = 'com.hiktab.host'
$installDir = Join-Path $env:LOCALAPPDATA 'HikTab'

Get-Process HikTabHost -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
    Remove-Item -Path "HKCU:\Software\$browser\NativeMessagingHosts\$hostName" -Recurse -ErrorAction SilentlyContinue
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION' `
    -Name 'HikTabHost.exe' -ErrorAction SilentlyContinue

$ranges = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges'
Get-ChildItem $ranges -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'HikTab_*' } |
    Remove-Item -Recurse -ErrorAction SilentlyContinue

Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'HikTab removido. Remova tambem a extensao em chrome://extensions.' -ForegroundColor Green
