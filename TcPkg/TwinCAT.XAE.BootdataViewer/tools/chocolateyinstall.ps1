$ErrorActionPreference = 'Stop'

# The viewer is part of the IDE packages (TwinCAT.XAE.BootdataViewer.TcXaeShell64 / .VS2022 / .VS2026), which
# TcPkg installs after this package and which create the Start menu entry. Versions up to 1.2 also
# put a separate copy into %ProgramFiles%\BootdataViewer; that copy is removed here.
$legacyDir = Join-Path $env:ProgramFiles 'BootdataViewer'
$legacyExe = Join-Path $legacyDir 'BootdataViewer.exe'
if (Test-Path $legacyExe) {
    if (Get-Process BootdataViewer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $legacyExe }) {
        throw "The bootdata viewer is running from $legacyDir. Close it and run the installation again."
    }
    Remove-Item $legacyDir -Recurse -Force
    Write-Host "Removed the separate copy in $legacyDir; the viewer now lives in the IDE extension folders."
}
$link = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TwinCAT Bootdata Viewer.lnk'
if ((Test-Path $link) -and -not (Test-Path $legacyExe)) {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath
    if ($target -like "$legacyDir\*") { Remove-Item $link -Force }
}
