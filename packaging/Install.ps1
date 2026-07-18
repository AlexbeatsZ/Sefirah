param(
    [switch]$SkipOfficialDataMigration
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($SkipOfficialDataMigration) {
        $arguments += '-SkipOfficialDataMigration'
    }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$running = Get-Process -Name 'Sefirah' -ErrorAction SilentlyContinue
if ($running) {
    throw 'Sefirah is running. Close both the Microsoft Store and fork versions before installing.'
}

$packages = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.msixbundle' -File)
$certificates = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cer' -File)
if ($packages.Count -ne 1 -or $certificates.Count -ne 1) {
    throw 'The installer folder must contain exactly one .msixbundle and one .cer file.'
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificates[0].FullName)
$trusted = Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' |
    Where-Object Thumbprint -EQ $certificate.Thumbprint
if (-not $trusted) {
    Import-Certificate -FilePath $certificates[0].FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Host "Trusted the local fork signing certificate for this computer: $($certificate.Thumbprint)"
}

Add-AppxPackage -Path $packages[0].FullName
Write-Host "Installed: $($packages[0].Name)"

if (-not $SkipOfficialDataMigration) {
    $source = Join-Path $env:LOCALAPPDATA 'Packages\shrimqy.Seki-PhoneLink_9yhjgvpvzzxz2\LocalState'
    $forkPackage = Get-AppxPackage -Name 'Meta.Sefirah.Fork'
    if ($forkPackage -and (Test-Path -LiteralPath $source -PathType Container)) {
        $destination = Join-Path $env:LOCALAPPDATA "Packages\$($forkPackage.PackageFamilyName)\LocalState"
        $existingIdentity = @('sefirah.db', 'Sefirah.pfx') |
            ForEach-Object { Join-Path $destination $_ } |
            Where-Object { Test-Path -LiteralPath $_ }
        if ($existingIdentity) {
            Write-Host 'Fork pairing data already exists; official data migration was skipped.'
        }
        else {
            & (Join-Path $PSScriptRoot 'Migrate-OfficialData.ps1') -DestinationLocalState $destination
            Write-Host 'Microsoft Store pairing data and settings were migrated into the fork.'
        }
    }
}

Write-Host 'Installation complete. You can now start Sefirah Fork from the Start menu.'
