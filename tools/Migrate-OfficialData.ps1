param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationLocalState
)

$ErrorActionPreference = 'Stop'

$sourceLocalState = Join-Path $env:LOCALAPPDATA 'Packages\shrimqy.Seki-PhoneLink_9yhjgvpvzzxz2\LocalState'
$destination = [System.IO.Path]::GetFullPath($DestinationLocalState)
$source = [System.IO.Path]::GetFullPath($sourceLocalState)

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Official Sefirah LocalState was not found at $source"
}

if ($destination -eq $source) {
    throw 'Source and destination must be different directories.'
}

$running = Get-Process -Name 'Sefirah' -ErrorAction SilentlyContinue
if ($running) {
    throw 'Sefirah is running. Close both the Store and fork builds before migrating data.'
}

$existingIdentityFiles = @('sefirah.db', 'Sefirah.pfx') | ForEach-Object {
    Join-Path $destination $_
} | Where-Object { Test-Path -LiteralPath $_ }
if ($existingIdentityFiles) {
    throw "Destination already contains pairing identity data: $($existingIdentityFiles -join ', ')"
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupRoot = Join-Path $env:LOCALAPPDATA "Temp\.agents\Sefirah\official-data-import\$timestamp"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $backupRoot -Recurse -Force

function Get-RelativeHashes([string]$root) {
    $rootPath = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
    $result = @{}
    Get-ChildItem -LiteralPath $rootPath -File -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($rootPath.Length).TrimStart('\')
        $result[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $result
}

$sourceHashes = Get-RelativeHashes $source
$backupHashes = Get-RelativeHashes $backupRoot
if ($sourceHashes.Count -ne $backupHashes.Count) {
    throw 'Backup validation failed: file counts differ.'
}
foreach ($relative in $sourceHashes.Keys) {
    if ($backupHashes[$relative] -ne $sourceHashes[$relative]) {
        throw "Backup validation failed for $relative"
    }
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $backupRoot -Force | Copy-Item -Destination $destination -Recurse -Force

$destinationHashes = Get-RelativeHashes $destination
foreach ($relative in $backupHashes.Keys) {
    if ($destinationHashes[$relative] -ne $backupHashes[$relative]) {
        throw "Destination validation failed for $relative"
    }
}

Write-Output "Migration completed. Validated backup: $backupRoot"
