$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'BootdataViewer.Install.ps1')
$pp = Get-PackageParameters

$ideDir = Join-Path $env:ProgramFiles 'Beckhoff\TcXaeShell\Common7\IDE'
$shellExe = Join-Path $ideDir 'TcXaeShell.exe'
Remove-ExtensionFolder $ideDir
if (Test-Path $shellExe) { Update-IdeConfiguration $shellExe $pp }
Update-StartMenuShortcut
Write-Host 'TwinCAT XAE Bootdata Viewer extension removed from TcXaeShell.'
