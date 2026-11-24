#Requires -Version 5.1
<#
.SYNOPSIS
  Build the per-user Revit 2027 MSI installer (add-in + MCP server).

.DESCRIPTION
  Produces build/msi-r27/RvtMcp.Revit2027-v<version>-win-x64.msi plus a
  .msi.sha256 sidecar. The MSI installs:
    - the Revit 2027 add-in into %APPDATA%\Autodesk\Revit\Addins\2027\RvtMcp\
    - the self-contained win-x64 MCP server into
      %LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe

  Requires the WiX v4+ CLI on PATH:  dotnet tool install --global wix

  Client (MCP app) wiring is intentionally not part of the MSI; point the
  client at the server path above, see docs/install.md.

.PARAMETER Config
  Build configuration for the plugin and server. Default: Release.

.PARAMETER RepoRoot
  Repo root path. Default: parent directory of the scripts/ folder.

.PARAMETER Version
  Package version. Default: server.json version.

.PARAMETER OutputDir
  Output directory. Default: <RepoRoot>\build\msi-r27

.PARAMETER AllowDirty
  Package an uncommitted working tree for a throwaway test build.

.EXAMPLE
  pwsh scripts/package-msi-r27.ps1 -Config Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [string]$RepoRoot,

    [string]$Version,

    [string]$OutputDir,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

$dirty = $false
$status = $null
try { $status = & git -C $RepoRoot status --porcelain 2>$null } catch { $status = $null }
if ($status) {
    if (-not $AllowDirty) { throw 'Working tree has uncommitted changes. Commit them so the package matches a commit, or pass -AllowDirty for a throwaway test package.' }
    $dirty = $true
    Write-Warning 'Packaging a dirty working tree (-AllowDirty).'
}

if (-not $Version) {
    $Version = ((Get-Content -Raw -Path (Join-Path $RepoRoot 'server.json')) | ConvertFrom-Json).version
}
if (-not $Version) {
    throw 'Could not determine package version. Pass -Version explicitly.'
}
$displayVersion = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'build\msi-r27'
}
$stageRoot = Join-Path $OutputDir 'stage'
$addinStage = Join-Path $stageRoot 'addin'
$serverStage = Join-Path $stageRoot 'server'

if (Test-Path $stageRoot) { Remove-Item -Path $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $addinStage -Force | Out-Null
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null

Write-Host "=== package-msi-r27.ps1 ==="
Write-Host ("RepoRoot : {0}" -f $RepoRoot)
Write-Host ("Version  : {0}" -f $displayVersion)
Write-Host ("Config   : {0}" -f $Config)

Write-Host ""
Write-Host "[plugin] building Revit 2027 shell without local auto-deploy"
$pluginProject = Join-Path $RepoRoot 'src\plugin-r27\RvtMcp.Plugin.R27.csproj'
& dotnet build $pluginProject -c $Config /p:RvtMcpSkipDeploy=true
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for plugin-r27 with exit code $LASTEXITCODE" }

$binDir = Join-Path $RepoRoot ("src\plugin-r27\bin\{0}\net10.0-windows7.0" -f $Config)
$pluginDll = Join-Path $binDir 'RvtMcp.Plugin.dll'
if (-not (Test-Path $pluginDll)) { throw "Missing $pluginDll" }

# Same runtime-dep globs as scripts/stage-plugin-zip.ps1 - keep in sync.
$patterns = @(
    'RvtMcp.*.dll',
    'Newtonsoft.Json.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw*.dll',
    'Microsoft.CodeAnalysis*.dll',
    'UglyToad.PdfPig*.dll',
    'System.Collections.Immutable.dll',
    'System.Reflection.Metadata.dll',
    'System.Memory.dll',
    'System.Buffers.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
)
foreach ($p in $patterns) {
    Get-ChildItem -Path $binDir -Filter $p -File -ErrorAction SilentlyContinue |
        Copy-Item -Destination $addinStage -Force
}
$nativeSqlite = Join-Path $binDir 'runtimes\win-x64\native\e_sqlite3.dll'
if (-not (Test-Path $nativeSqlite)) { throw "Native SQLite missing: $nativeSqlite" }
Copy-Item -Path $nativeSqlite -Destination $addinStage -Force
Copy-Item -Path (Join-Path $RepoRoot 'src\plugin-r27\RvtMcp.R27.addin') -Destination $addinStage -Force

Write-Host ""
Write-Host "[server] publishing self-contained win-x64 executable"
$serverProject = Join-Path $RepoRoot 'src\server\RvtMcp.Server.csproj'
& dotnet publish $serverProject -c $Config -r win-x64 --self-contained true /p:PublishSingleFile=true -o $serverStage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the server with exit code $LASTEXITCODE" }
$publishedServerExe = Join-Path $serverStage 'RvtMcp.Server.exe'
if (-not (Test-Path $publishedServerExe)) { throw "Published server executable not found: $publishedServerExe" }
Move-Item -Path $publishedServerExe -Destination (Join-Path $serverStage 'rvt-mcp.exe') -Force

Write-Host ""
Write-Host "[wix] generating component harvest and building MSI"
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) { throw 'WiX CLI not found on PATH. Install with: dotnet tool install --global wix' }

function ConvertTo-XmlText {
    param([Parameter(Mandatory = $true)][string]$Text)
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

function Get-ComponentGroupXml {
    param(
        [Parameter(Mandatory = $true)][string]$GroupId,
        [Parameter(Mandatory = $true)][string]$DirectoryId,
        [Parameter(Mandatory = $true)][string]$StageDir,
        [Parameter(Mandatory = $true)][string]$Prefix
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('    <ComponentGroup Id="{0}" Directory="{1}">' -f $GroupId, $DirectoryId))
    $index = 0
    foreach ($file in @(Get-ChildItem -Path $StageDir -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($StageDir.Length).TrimStart('\', '/')
        $componentId = '{0}_c{1}' -f $Prefix, $index
        $fileId = '{0}_f{1}' -f $Prefix, $index
        $source = ('$(var.StageDir)\{0}' -f ($relative -replace '/', '\'))
        $lines.Add(('      <Component Id="{0}" Guid="*" Directory="{1}">' -f $componentId, $DirectoryId))
        $lines.Add(('        <File Id="{0}" Source="{1}" KeyPath="yes" />' -f $fileId, (ConvertTo-XmlText $source)))
        $lines.Add('      </Component>')
        $index++
    }
    $lines.Add('    </ComponentGroup>')
    return ($lines -join "`r`n")
}

$harvest = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
$(Get-ComponentGroupXml -GroupId 'AddinComponents' -DirectoryId 'RvtMcpAddinsDir' -StageDir $addinStage -Prefix 'addin')
$(Get-ComponentGroupXml -GroupId 'ServerComponents' -DirectoryId 'CurrentDir' -StageDir $serverStage -Prefix 'server')
  </Fragment>
</Wix>
"@
Set-Content -Path (Join-Path $OutputDir 'harvested.wxs') -Value $harvest -Encoding UTF8

$wxsPath = Join-Path $OutputDir 'RvtMcp.Revit2027.wxs'
Copy-Item -Path (Join-Path $RepoRoot 'packaging\r27\RvtMcp.Revit2027.wxs') -Destination $wxsPath -Force

$msiPath = Join-Path $OutputDir ("RvtMcp.Revit2027-{0}-win-x64.msi" -f $displayVersion)
if (Test-Path $msiPath) { Remove-Item -Path $msiPath -Force }
& wix build -arch x64 -d "StageDir=$stageRoot" -d "PackageVersion=$Version" -o $msiPath $wxsPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

$msiHash = ((Get-FileHash -Algorithm SHA256 -Path $msiPath).Hash).ToLowerInvariant()
Set-Content -LiteralPath "$msiPath.sha256" -Value ("{0}  {1}" -f $msiHash, (Split-Path -Leaf $msiPath)) -Encoding ascii -NoNewline

Write-Host ""
Write-Host "=== Revit 2027 MSI package summary ==="
Write-Host ("Output : {0}" -f $msiPath)
Write-Host ("SHA-256: {0}" -f $msiHash)
Write-Host ("Add-in : %APPDATA%\Autodesk\Revit\Addins\2027\RvtMcp\")
Write-Host ("Server : %LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe")
if ($dirty) { Write-Host "Note   : packaged from a dirty working tree (-AllowDirty)." }
