<#
Builds the viewer (net481) and the Visual Studio extension, then packs the TcPkg packages:
  TwinCAT.XAE.BootdataViewer                install this one; TcPkg adds the IDE packages below per
                                    UseTcXaeShell64/UseVS20xx (removes the pre-1.3 Program Files copy)
  TwinCAT.XAE.BootdataViewer.TcXaeShell64   extension + viewer for TwinCAT XAE Shell (64-bit)
  TwinCAT.XAE.BootdataViewer.VS2022         extension + viewer for Visual Studio 2022
  TwinCAT.XAE.BootdataViewer.VS2026         extension + viewer for Visual Studio 2026
Each IDE package copies the extracted VSIX to <IDE>\Common7\IDE\Extensions\BootdataViewer\BootdataViewer
(shared\Common); the Visual Studio packages also share shared\VisualStudio and add VisualStudioTarget.ps1.
Output: TcPkg\out\*.nupkg

TcPkg verifies package signatures by default (VerifySignatures). Sign with -Pfx/-Password or
-Thumbprint; the certificate must be trusted by TcPkg ("tcpkg certificate ...") on every PC that
installs the packages.

  .\build.ps1
  .\build.ps1 -Pfx C:\certs\me.pfx -Password secret -TimestampServer http://timestamp.digicert.com
#>
param(
    [string] $Version,
    [string] $Pfx,
    [string] $Password,
    [string] $Thumbprint,
    [string] $TimestampServer = 'none'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$here = $PSScriptRoot
$tcpkg = Join-Path $env:ProgramFiles 'Beckhoff\TcPkg\TcPkg.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Workload.VisualStudioExtension -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio with the "Visual Studio extension development" workload is required.' }

# The package version follows the VSIX manifest unless given.
$manifest = Join-Path $root 'BootdataViewer.VsExtension\source.extension.vsixmanifest'
if (-not $Version) { $Version = ([xml](Get-Content $manifest)).PackageManifest.Metadata.Identity.Version }
Write-Host "== Version $Version"

Write-Host '== Building viewer (net481)'
dotnet build (Join-Path $root 'BootdataViewer') -c Release -f net481 -nologo -v q
if ($LASTEXITCODE) { throw 'Viewer build failed.' }
Write-Host '== Building Visual Studio extension'
& $msbuild (Join-Path $root 'BootdataViewer.VsExtension\BootdataViewer.VsExtension.csproj') -restore -p:Configuration=Release -nologo -v:minimal
if ($LASTEXITCODE) { throw 'Extension build failed.' }

$vsix = Join-Path $root 'BootdataViewer.VsExtension\bin\Release\BootdataViewer.VsExtension.vsix'
$license = Join-Path $root 'BootdataViewer.VsExtension\LICENSE.txt'

# Stage each package (sources + build output) and pack it.
$stage = Join-Path $here '_stage'
$out = Join-Path $here 'out'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($id in 'TwinCAT.XAE.BootdataViewer', 'TwinCAT.XAE.BootdataViewer.TcXaeShell64', 'TwinCAT.XAE.BootdataViewer.VS2022', 'TwinCAT.XAE.BootdataViewer.VS2026') {
    $dir = Join-Path $stage $id
    Copy-Item (Join-Path $here $id) $dir -Recurse
    $tools = Join-Path $dir 'tools'
    Copy-Item $license $tools
    Copy-Item (Join-Path $here 'icon\icon.png') $dir   # package icon (icon\make-icon.ps1)
    if ($id -ne 'TwinCAT.XAE.BootdataViewer') {
        # IDE packages: the extracted VSIX (extension + viewer) without the installer-only metadata.
        $ext = Join-Path $tools 'Extension'
        [IO.Compression.ZipFile]::ExtractToDirectory($vsix, $ext)
        Get-ChildItem $ext -File | Where-Object { $_.Name -in '[Content_Types].xml', 'catalog.json', 'manifest.json' } | Remove-Item
        Copy-Item (Join-Path $here 'shared\Common\*') $tools
    }
    if ($id -like 'TwinCAT.XAE.BootdataViewer.VS20*') { Copy-Item (Join-Path $here 'shared\VisualStudio\*') $tools }
    $nuspec = Join-Path $dir "$id.nuspec"
    (Get-Content $nuspec -Raw).Replace('$version$', $Version) | Set-Content $nuspec -Encoding utf8 -NoNewline
    Write-Host "== Packing $id"
    & $tcpkg pack $nuspec -o $out
    if ($LASTEXITCODE) { throw "Packing $id failed." }

    $nupkg = Join-Path $out "$id.$Version.nupkg"
    if ($Pfx -or $Thumbprint) {
        Write-Host "== Signing $id"
        $signArgs = @('sign', $nupkg, '--timestamp-server', $TimestampServer)
        if ($Pfx) { $signArgs += @('--pfx', $Pfx); if ($Password) { $signArgs += @('--password', $Password) } }
        else { $signArgs += @('--thumbprint', $Thumbprint) }
        & $tcpkg @signArgs
        if ($LASTEXITCODE) { throw "Signing $id failed." }
    }
}
Remove-Item $stage -Recurse -Force
Get-ChildItem $out -Filter "*.$Version.nupkg" | Select-Object Name, Length
