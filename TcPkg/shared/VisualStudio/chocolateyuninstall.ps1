$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'VisualStudio.ps1')
$pp = Get-PackageParameters

$vs = Get-TargetVisualStudio $pp['InstanceId']
Remove-VsixInstallerCopy $vs
Remove-ExtensionFolder $vs.IdeDir
Update-IdeConfiguration $vs.DevEnv $pp
Update-StartMenuShortcut
Write-Host "TwinCAT XAE Bootdata Viewer extension removed from $($vs.Name)."
