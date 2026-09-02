# Single source of truth for what this mod ships. The client mirror (client deploy path) and
# the server zip are both produced from the one staged output below, so they can never drift
# from each other the way two independent exclude lists eventually would.
[CmdletBinding()]
param(
    [string]$Dll = "",
    [string]$Pdb = "",
    [string]$DeployPath = "",
    [string]$StagingPath = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Split-Path -Parent $MyInvocation.MyCommand.Path).TrimEnd('\')
$modInfoPath = Join-Path $repoRoot "modinfo.json"
$modInfo = Get-Content $modInfoPath -Raw | ConvertFrom-Json
$modId = $modInfo.modid
$version = $modInfo.version

$excludeGlobs = @(
    "assets\dietsetup\config\diets\dev-*.json"
)

$DeployPath = $DeployPath.TrimEnd('\')
$StagingPath = $StagingPath.TrimEnd('\')

$stageDir = Join-Path $repoRoot "obj\package\stage"
if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item $modInfoPath (Join-Path $stageDir "modinfo.json") -Force

if ($Dll -ne "" -and (Test-Path $Dll)) {
    Copy-Item $Dll (Join-Path $stageDir (Split-Path -Leaf $Dll)) -Force
}
# Excluded from Release: $excludeGlobs only filters the assets/ walk below, not this copy, and
# a pdb has no reason to ship in the server/client zip.
if ($Pdb -ne "" -and (Test-Path $Pdb) -and $Configuration -ne "Release") {
    Copy-Item $Pdb (Join-Path $stageDir (Split-Path -Leaf $Pdb)) -Force
}

$assetsSrc = Join-Path $repoRoot "assets"
$stagedCount = 0
$excludedCount = 0
if (Test-Path $assetsSrc) {
    $assetFiles = Get-ChildItem -Path $assetsSrc -Recurse -File
    foreach ($file in $assetFiles) {
        $relPath = $file.FullName.Substring($repoRoot.Length + 1)
        $excluded = $false
        foreach ($glob in $excludeGlobs) {
            if ($relPath -like $glob) { $excluded = $true; break }
        }
        if ($excluded) {
            $excludedCount++
            Write-Host "[$modId] Excluded from package: $relPath"
            continue
        }
        $destPath = Join-Path $stageDir $relPath
        $destDir = Split-Path -Parent $destPath
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item $file.FullName $destPath -Force
        $stagedCount++
    }
}
Write-Host "[$modId] Staged $stagedCount asset file(s), excluded $excludedCount"

if ($DeployPath -ne "") {
    if (-not (Test-Path $DeployPath)) { New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null }

    $stagedFiles = Get-ChildItem -Path $stageDir -Recurse -File
    $stagedRelPaths = @{}
    foreach ($file in $stagedFiles) {
        $relPath = $file.FullName.Substring($stageDir.Length + 1)
        $stagedRelPaths[$relPath] = $true
        $destPath = Join-Path $DeployPath $relPath
        $destDir = Split-Path -Parent $destPath
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item $file.FullName $destPath -Force
    }

    $deployedFiles = Get-ChildItem -Path $DeployPath -Recurse -File
    $staleCount = 0
    foreach ($file in $deployedFiles) {
        $relPath = $file.FullName.Substring($DeployPath.Length + 1)
        if (-not $stagedRelPaths.ContainsKey($relPath)) {
            Remove-Item $file.FullName -Force
            $staleCount++
        }
    }
    Write-Host "[$modId] Deployed to $DeployPath : $($stagedFiles.Count) copied, $staleCount stale removed"
} else {
    Write-Host "[$modId] DeployPath not set, skipping client mirror"
}

if ($Configuration -eq "Release") {
    $artifactsDir = Join-Path $repoRoot "artifacts"
    if (-not (Test-Path $artifactsDir)) { New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null }

    $zipPath = Join-Path $artifactsDir "${modId}_${version}.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    # Compress-Archive under Windows PowerShell 5.1 writes \ as the entry separator, which Linux
    # treats as a literal filename character -- the game's asset VFS then finds nothing under
    # assets/<domain>/... Writing entries by hand keeps the separator explicit.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    Get-ChildItem -Path $stageDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stageDir.Length).TrimStart('\','/').Replace('\','/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel)
    }
    $zip.Dispose()
    Write-Host "[$modId] Packaged $zipPath"

    # Catches a regression of the Compress-Archive backslash bug (proven against
    # tools/zip-packaging-fixtures/known-bad-backslash.zip) plus the other way this fails: a wrong
    # staging root producing a zip with no assets/<modid>/ entries at all. Runs before the staging
    # copy so a bad zip never reaches the server.
    $verifyZip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = $verifyZip.Entries | ForEach-Object { $_.FullName }
    } finally {
        $verifyZip.Dispose()
    }

    $zipErrors = @()
    $backslashEntries = $entryNames | Where-Object { $_.Contains('\') }
    if ($backslashEntries) { $zipErrors += "backslash in entry name(s): $($backslashEntries -join ', ')" }
    $leadingSlashEntries = $entryNames | Where-Object { $_.StartsWith('/') }
    if ($leadingSlashEntries) { $zipErrors += "leading slash in entry name(s): $($leadingSlashEntries -join ', ')" }
    $dupeEntries = $entryNames | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
    if ($dupeEntries) { $zipErrors += "duplicate entry name(s): $($dupeEntries -join ', ')" }
    $assetsPrefix = "assets/$modId/"
    if (-not ($entryNames | Where-Object { $_.StartsWith($assetsPrefix) })) {
        $zipErrors += "no entry starts with '$assetsPrefix' -- wrong staging root?"
    }
    if ($zipErrors.Count -gt 0) {
        Write-Error "[$modId] Packaged zip failed entry-name validation:`n$($zipErrors -join "`n")"
        exit 1
    }

    if ($StagingPath -ne "") {
        if (-not (Test-Path $StagingPath)) { New-Item -ItemType Directory -Path $StagingPath -Force | Out-Null }
        Copy-Item $zipPath (Join-Path $StagingPath (Split-Path -Leaf $zipPath)) -Force
        Write-Host "[$modId] Copied zip to staging path $StagingPath"
    } else {
        Write-Host "[$modId] MOD_STAGING_PATH not set, skipping server-staging copy"
    }
}

exit 0
