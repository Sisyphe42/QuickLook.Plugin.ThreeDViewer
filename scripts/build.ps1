<#
.SYNOPSIS
  Build, package and (optionally) deploy QuickLook.Plugin.ThreeDViewer.

.DESCRIPTION
  1. dotnet build -c Release   (net462 / UseWPF, validated 0 warnings)
  2. Stage -> artefacts\package (flat root, ready to zip as .qlplugin)
  3. Zip   -> artefacts\QuickLook.Plugin.ThreeDViewer.qlplugin
  4. -Deploy: copy to UserData\QuickLook.Plugin\QuickLook.Plugin.ThreeDViewer\
  5. -DisableHelixViewer: move the OLD user-installed HelixViewer (Priority
     int.MaxValue, .obj/.stl/.ply/.3ds) out of the scan tree.
     -RollbackHelixViewer reverses this.

  Nothing is applied by default: -Deploy / -DisableHelixViewer are opt-in.
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$DisableHelixViewer,
    [switch]$RollbackHelixViewer,
    [switch]$SkipBuild,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Proj = Join-Path $Root 'src\QuickLook.Plugin.ThreeDViewer\QuickLook.Plugin.ThreeDViewer.csproj'
$Stage = Join-Path $Root 'artefacts\package'
$OutDir = Join-Path $Root 'artefacts'
$QlPlugin = Join-Path $OutDir 'QuickLook.Plugin.ThreeDViewer.qlplugin'
$UserData = 'D:\scoop\persist\quicklook\UserData'
$PluginRoot = Join-Path $UserData 'QuickLook.Plugin'
$TargetDir = Join-Path $PluginRoot 'QuickLook.Plugin.ThreeDViewer'
$DisabledRoot = Join-Path $UserData 'DisabledPlugins'
$HelixDir = Join-Path $PluginRoot 'QuickLook.Plugin.HelixViewer'
$HelixBackup = Join-Path $DisabledRoot 'QuickLook.Plugin.HelixViewer'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Invoke-Build {
    Write-Step 'Build (dotnet build -c Release)'
    & dotnet build $Proj -c Release -v:m
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
}

function Assert-Package {
    Write-Step 'Verify staged package'
    $required = @(
        'QuickLook.Plugin.ThreeDViewer.dll',
        'QuickLook.Plugin.Metadata.config',
        'AssimpNet.dll',
        'HelixToolkit.Wpf.dll',
        'x64\assimp.dll',
        'x86\assimp.dll',
        'warmup.glb'
    )
    foreach ($r in $required) {
        $p = Join-Path $Stage $r
        if (-not (Test-Path $p)) { throw "Missing staged file: $r" }
        Write-Host ("  OK  {0,-45} {1,10:N0} bytes" -f $r, (Get-Item $p).Length)
    }
    # Namespace must match the entry class namespace (PluginInstaller uses it as dir name).
    [xml]$meta = Get-Content (Join-Path $Stage 'QuickLook.Plugin.Metadata.config') -Raw
    $ns = $meta.Metadata.Namespace
    if ($ns -ne 'QuickLook.Plugin.ThreeDViewer') { throw "Unexpected Metadata Namespace: $ns" }
    Write-Host "  OK  Namespace = $ns"
}

function New-QlPlugin {
    Write-Step 'Create .qlplugin (flat zip root)'
    # Runtime diagnostics / test scratch files must never ship.
    Get-ChildItem $Stage -File -Include '*.log','*.pdb','sample.glb','*.debug' -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    if (Test-Path $QlPlugin) { Remove-Item $QlPlugin -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $QlPlugin,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($QlPlugin)
    try {
        Write-Host "  Entries: $($zip.Entries.Count)"
        $zip.Entries | ForEach-Object { Write-Host ("    {0,-48} {1,10:N0}" -f $_.FullName, $_.Length) }
        $rootMeta = $zip.GetEntry('QuickLook.Plugin.Metadata.config')
        if ($null -eq $rootMeta) { throw 'Metadata.config is not at the zip root' }
    } finally { $zip.Dispose() }
    Write-Host ("  Created: {0} ({1:N0} bytes)" -f $QlPlugin, (Get-Item $QlPlugin).Length)
}

function Invoke-Deploy {
    Write-Step 'Deploy to QuickLook UserData'
    if (-not (Test-Path $PluginRoot)) { throw "Plugin scan dir not found: $PluginRoot" }
    if (-not (Test-Path $TargetDir)) { New-Item -ItemType Directory -Path $TargetDir | Out-Null }
    Get-ChildItem $Stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Stage.Length).TrimStart('\')
        $dest = Join-Path $TargetDir $rel
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item $_.FullName -Destination $dest -Force
        Write-Host ("  {0}" -f $rel)
    }
    Write-Host "  Deployed -> $TargetDir"
    Write-Host '  NOTE: QuickLook loads plugins only at process start; restart QuickLook to activate.'
}

function Invoke-DisableHelix {
    Write-Step 'Disable old user HelixViewer'
    if (-not (Test-Path $HelixDir)) {
        Write-Host '  Already absent (nothing to do).' -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path $DisabledRoot)) { New-Item -ItemType Directory -Path $DisabledRoot | Out-Null }
    if (Test-Path $HelixBackup) { throw "Backup already exists: $HelixBackup (refusing to overwrite)" }
    # MUST move the whole directory: PluginManager uses
    # Directory.GetFiles(UserPluginPath, "QuickLook.Plugin.*.dll", AllDirectories),
    # so renaming in place still gets scanned.
    Move-Item -Path $HelixDir -Destination $HelixBackup
    Write-Host "  Moved -> $HelixBackup"
}

function Invoke-RollbackHelix {
    Write-Step 'Rollback HelixViewer'
    if (-not (Test-Path $HelixBackup)) { Write-Host '  No backup found.' -ForegroundColor Yellow; return }
    if (Test-Path $HelixDir) { throw "Target already exists: $HelixDir" }
    Move-Item -Path $HelixBackup -Destination $HelixDir
    Write-Host "  Restored -> $HelixDir"
}

if ($VerifyOnly) { Assert-Package; return }
if (-not $SkipBuild) { Invoke-Build }
Assert-Package
New-QlPlugin
if ($DisableHelixViewer) { Invoke-DisableHelix }
if ($Deploy) { Invoke-Deploy }
if ($RollbackHelixViewer) { Invoke-RollbackHelix }
Write-Host "`nDone." -ForegroundColor Green
