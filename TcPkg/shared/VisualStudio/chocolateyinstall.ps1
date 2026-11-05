$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'VisualStudio.ps1')
$pp = Get-PackageParameters

$vs = Get-TargetVisualStudio $pp['InstanceId']
Write-Host "Installing the extension into $($vs.Name) ($($vs.InstanceId))..."
Remove-VsixInstallerCopy $vs
Install-ExtensionFolder $vs.IdeDir (Join-Path $toolsDir 'Extension')
Update-IdeConfiguration $vs.DevEnv $pp
Update-StartMenuShortcut
