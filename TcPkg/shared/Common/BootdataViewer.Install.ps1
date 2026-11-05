# Shared by the IDE packages (TcXaeShell64, VS2022, VS2026); build.ps1 copies it into each one.
# The extension, with the viewer inside, is copied to <IDE>\Common7\IDE\Extensions\BootdataViewer\BootdataViewer
# (like Beckhoff's MSIs deploy into TcXaeShell). There is no separate copy of the viewer; the
# Start menu entry points to one of the installed extension folders.

$ExtensionId = 'BootdataViewer.VsExtension.6f0d3b4a-2c1e-4f7a-9b8d-5e2a1c3f4d60'
$StartMenuLink = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TwinCAT XAE Bootdata Viewer.lnk'
# Name of the Start menu entry up to 1.3; replaced by $StartMenuLink.
$OldStartMenuLink = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TwinCAT Bootdata Viewer.lnk'

function Get-ExtensionFolder([string] $ideDir) { Join-Path $ideDir 'Extensions\BootdataViewer\BootdataViewer' }

function Install-ExtensionFolder([string] $ideDir, [string] $sourceDir) {
    $target = Get-ExtensionFolder $ideDir
    if (Test-Path $target) {
        try { Remove-Item $target -Recurse -Force }
        catch { throw "The previous version in $target is in use. Close the IDE (and the bootdata viewer) and run the installation again." }
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $target -Recurse -Force
    Write-Host "Extension installed to $target"
}

function Remove-ExtensionFolder([string] $ideDir) {
    $root = Split-Path -Parent (Get-ExtensionFolder $ideDir)
    if (Test-Path $root) {
        try { Remove-Item $root -Recurse -Force }
        catch { throw "The extension in $root is in use. Close the IDE (and the bootdata viewer) and run the uninstallation again." }
        Write-Host "Extension removed from $root"
    }
}

# Other copies of this extension in the IDE's Extensions folder, e.g. one installed earlier by
# VSIXInstaller (random folder name); loading two copies would register the package twice.
function Get-OtherExtensionCopies([string] $ideDir) {
    $fixed = Get-ExtensionFolder $ideDir
    Get-ChildItem (Join-Path $ideDir 'Extensions') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ChildItem $_.FullName -Filter extension.vsixmanifest -Recurse -Depth 2 -ErrorAction SilentlyContinue
    } | Where-Object { $_.DirectoryName -ne $fixed -and (Select-String -Path $_.FullName -Pattern $ExtensionId -SimpleMatch -Quiet) } |
        ForEach-Object { $_.DirectoryName }
}

# IDE folders (…\Common7\IDE) that can hold the extension: TcXaeShell (64-bit) and Visual Studio 2022/2026.
function Get-IdeDirectories {
    $dirs = @(Join-Path $env:ProgramFiles 'Beckhoff\TcXaeShell\Common7\IDE')
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $dirs += & $vswhere -version '[17.0,19.0)' -products * -prerelease -property installationPath |
            ForEach-Object { Join-Path $_ 'Common7\IDE' }
    }
    $dirs
}

# Points the Start menu entry to an installed viewer (TcXaeShell first), or removes it if none is left.
function Update-StartMenuShortcut {
    if (Test-Path $OldStartMenuLink) { Remove-Item $OldStartMenuLink -Force }
    $exe = Get-IdeDirectories | ForEach-Object { Join-Path (Get-ExtensionFolder $_) 'Viewer\BootdataViewer.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($exe) {
        Install-ChocolateyShortcut -ShortcutFilePath $StartMenuLink -TargetPath $exe -WorkingDirectory (Split-Path -Parent $exe) `
            -Description 'Open, check, edit and restore TwinCAT persistent data (bootdata)'
        Write-Host "Start menu entry points to $exe"
    } elseif (Test-Path $StartMenuLink) {
        Remove-Item $StartMenuLink -Force
        Write-Host 'Start menu entry removed (no viewer installed any more).'
    }
}

# Rebuilds the IDE's extension configuration, unless TcPkg does it once for all packages
# (it passes DISABLEUPDATECONFIGURATION to packages tagged UpdateConfigurationOption).
function Update-IdeConfiguration([string] $exe, $packageParameters) {
    if ($packageParameters['DISABLEUPDATECONFIGURATION']) {
        Write-Host "Skipping $(Split-Path -Leaf $exe) /updateconfiguration (DISABLEUPDATECONFIGURATION)."
        return
    }
    Write-Host "Updating the configuration of $(Split-Path -Leaf $exe)..."
    $p = Start-Process -FilePath $exe -ArgumentList '/updateconfiguration' -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { Write-Warning "$(Split-Path -Leaf $exe) /updateconfiguration returned $($p.ExitCode)." }
}
