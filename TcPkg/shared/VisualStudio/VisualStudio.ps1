# Shared by the Visual Studio packages (VS2022, VS2026); build.ps1 copies it into each one.
# VisualStudioTarget.ps1 in the package sets $VsTarget (display name, vswhere version range).
# TcPkg passes the instance it is configured for (UseVS2022 / UseVS2026) as package parameter InstanceId.
. (Join-Path $PSScriptRoot 'VisualStudioTarget.ps1')
. (Join-Path $PSScriptRoot 'BootdataViewer.Install.ps1')

function Get-TargetVisualStudio([string] $instanceId) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "$($VsTarget.Name) is not installed (vswhere.exe not found)." }
    $all = & $vswhere -version $VsTarget.Range -products * -prerelease -format json | ConvertFrom-Json
    $vs = if ($instanceId) { $all | Where-Object { $_.instanceId -eq $instanceId } | Select-Object -First 1 }
          else { $all | Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1 }
    if (-not $vs) { throw "$($VsTarget.Name)$(if ($instanceId) { " instance $instanceId" }) was not found." }
    $ide = Join-Path $vs.installationPath 'Common7\IDE'
    [pscustomobject]@{
        InstanceId = $vs.instanceId; Name = $vs.displayName; IdeDir = $ide
        DevEnv = Join-Path $ide 'devenv.exe'; Installer = Join-Path $ide 'VSIXInstaller.exe'
    }
}

# Versions up to 1.2 were installed with VSIXInstaller into a folder with a random name; remove
# that copy (and its registration) before the extension is placed in its fixed folder.
function Remove-VsixInstallerCopy($vs) {
    if (-not (Get-OtherExtensionCopies $vs.IdeDir)) { return }
    Write-Host 'Removing the copy installed earlier by VSIXInstaller...'
    $log = Join-Path $env:TEMP "$PackageId.vsix-uninstall.log"
    $p = Start-Process -FilePath $vs.Installer -ArgumentList "/q /a /instanceIds:$($vs.InstanceId) /logFile:`"$log`" /u:$ExtensionId" -Wait -PassThru
    if ($p.ExitCode -notin 0, 1002) { Write-Warning "VSIXInstaller /u returned $($p.ExitCode). Log: $log" }
    foreach ($dir in Get-OtherExtensionCopies $vs.IdeDir) {
        try { Remove-Item $dir -Recurse -Force; Write-Host "Removed $dir" }
        catch { throw "The old copy in $dir is in use. Close Visual Studio and run the installation again." }
    }
}

$PackageId = if ($env:ChocolateyPackageName) { $env:ChocolateyPackageName } else { "TwinCAT.XAE.BootdataViewer.$($VsTarget.Tag)" }
