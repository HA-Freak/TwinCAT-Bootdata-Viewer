$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'BootdataViewer.Install.ps1')
$pp = Get-PackageParameters

# TcXaeShell's own VSIXInstaller is not used: the extension is copied into the shell's
# Extensions folder (like Beckhoff's MSIs do) and picked up by /updateconfiguration.
$ideDir = Join-Path $env:ProgramFiles 'Beckhoff\TcXaeShell\Common7\IDE'
$shellExe = Join-Path $ideDir 'TcXaeShell.exe'
if (-not (Test-Path $shellExe)) { throw "TwinCAT XAE Shell (64-bit) was not found in $ideDir." }

Install-ExtensionFolder $ideDir (Join-Path $toolsDir 'Extension')
Update-IdeConfiguration $shellExe $pp
Update-StartMenuShortcut
