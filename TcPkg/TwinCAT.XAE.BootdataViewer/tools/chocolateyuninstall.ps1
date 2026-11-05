$ErrorActionPreference = 'Stop'

# Nothing of its own to remove: the viewer and the Start menu entry belong to the IDE packages.
# Clean up a separate copy left by a version up to 1.2, if any.
$legacyDir = Join-Path $env:ProgramFiles 'BootdataViewer'
if (Test-Path (Join-Path $legacyDir 'BootdataViewer.exe')) { Remove-Item $legacyDir -Recurse -Force }
# Files downloaded from targets (%LOCALAPPDATA%\BootdataViewer) belong to the user and are kept.
