param(
    [ValidateSet('x64')]
    [string]$Architecture = 'x64',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Sefirah\Sefirah.csproj'
$manifest = Join-Path $root 'src\Sefirah\Package.appxmanifest'
$signingDirectory = Join-Path $root '.signing'
$artifactDirectory = Join-Path $root 'artifacts\windows-x64'
$pfxPath = Join-Path $signingDirectory 'Sefirah-Fork-Signing.pfx'
$cerPath = Join-Path $signingDirectory 'Sefirah-Fork-Signing.cer'
$protectedPasswordPath = Join-Path $signingDirectory 'password.dpapi'
$publisher = 'CN=Meta Sefirah Fork'

New-Item -ItemType Directory -Path $signingDirectory, $artifactDirectory -Force | Out-Null

function Protect-ForCurrentUser([string]$value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($value)
    return [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function Unprotect-ForCurrentUser([byte[]]$value) {
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $value,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

if ((Test-Path -LiteralPath $pfxPath) -xor (Test-Path -LiteralPath $protectedPasswordPath)) {
    throw 'The signing key or its DPAPI password is missing. Restore both files from backup; do not silently rotate an update key.'
}

if (-not (Test-Path -LiteralPath $pfxPath)) {
    $random = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($random)
    $password = [Convert]::ToBase64String($random)

    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $publisher,
            $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $enhancedKeyUsage = [System.Security.Cryptography.OidCollection]::new()
        [void]$enhancedKeyUsage.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($enhancedKeyUsage, $true))

        $certificate = $request.CreateSelfSigned(
            [DateTimeOffset]::Now.AddDays(-1),
            [DateTimeOffset]::Now.AddYears(10))
        try {
            [System.IO.File]::WriteAllBytes(
                $pfxPath,
                $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
            [System.IO.File]::WriteAllBytes(
                $cerPath,
                $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            [System.IO.File]::WriteAllBytes($protectedPasswordPath, (Protect-ForCurrentUser $password))
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }
    Write-Host "Created project-local signing key: $pfxPath"
}

$password = Unprotect-ForCurrentUser ([System.IO.File]::ReadAllBytes($protectedPasswordPath))
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $password)
if ($certificate.Subject -ne $publisher -or -not $certificate.HasPrivateKey) {
    throw 'The signing certificate does not match the fork publisher or has no private key.'
}

[xml]$packageManifest = Get-Content -LiteralPath $manifest -Raw
$identity = $packageManifest.Package.Identity
if ($identity.Publisher -ne $publisher) {
    throw "Package publisher '$($identity.Publisher)' does not match signing certificate '$publisher'."
}
$version = $identity.Version

$publishArguments = @(
    'publish', $project,
    '-f', 'net10.0-windows10.0.26100',
    '-c', $Configuration,
    "-p:Platform=$Architecture",
    "-p:RuntimeIdentifier=win-$Architecture",
    '-p:UapAppxPackageBuildMode=Sideloading',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxBundle=Always',
    "-p:AppxBundlePlatforms=$Architecture",
    '-p:AppxPackageSigningEnabled=true',
    "-p:PackageCertificateKeyFile=$pfxPath",
    "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)"
)
$previousPassword = $env:PackageCertificatePassword
try {
    # MSBuild imports environment variables as properties. Keeping the password
    # out of the argument list prevents it from appearing in process listings.
    $env:PackageCertificatePassword = $password
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    if ($null -eq $previousPassword) {
        Remove-Item Env:\PackageCertificatePassword -ErrorAction SilentlyContinue
    }
    else {
        $env:PackageCertificatePassword = $previousPassword
    }
}

$packageRoot = Join-Path $root "src\Sefirah\AppPackages\Sefirah_${version}_Test"
$bundle = Get-ChildItem -LiteralPath $packageRoot -Filter "*_${Architecture}.msixbundle" -File |
    Select-Object -First 1
if (-not $bundle) {
    throw "Signed bundle was not found under $packageRoot"
}

$signature = Get-AuthenticodeSignature -LiteralPath $bundle.FullName
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'The generated bundle is not signed by the expected fork certificate.'
}

$staging = Join-Path $artifactDirectory "Sefirah-Fork_${version}_${Architecture}_Sideload"
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging | Out-Null
$outputBundle = Join-Path $staging "Sefirah-Fork_${version}_${Architecture}.msixbundle"
Copy-Item -LiteralPath $bundle.FullName -Destination $outputBundle
Copy-Item -LiteralPath $cerPath -Destination (Join-Path $staging 'Sefirah-Fork-Signing.cer')
Copy-Item -LiteralPath (Join-Path $root 'packaging\Install.ps1') -Destination $staging
Copy-Item -LiteralPath (Join-Path $root 'packaging\Install.cmd') -Destination $staging
Copy-Item -LiteralPath (Join-Path $root 'packaging\README.txt') -Destination $staging
Copy-Item -LiteralPath (Join-Path $root 'tools\Migrate-OfficialData.ps1') -Destination $staging

$zipPath = "$staging.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $staging -DestinationPath $zipPath -CompressionLevel Optimal

$bundleHash = Get-FileHash -LiteralPath $outputBundle -Algorithm SHA256
$zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256

$installerProject = Join-Path $root 'installer\Sefirah.Installer\Sefirah.Installer.csproj'
$installerPublishDirectory = Join-Path $artifactDirectory '.exe-publish'
if (Test-Path -LiteralPath $installerPublishDirectory) {
    Remove-Item -LiteralPath $installerPublishDirectory -Recurse -Force
}
$installerArguments = @(
    'publish', $installerProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $installerPublishDirectory,
    "-p:BundlePath=$outputBundle",
    "-p:CertificatePath=$cerPath"
)
& dotnet @installerArguments
if ($LASTEXITCODE -ne 0) {
    throw "EXE installer publish failed with exit code $LASTEXITCODE"
}

$unsignedInstaller = Join-Path $installerPublishDirectory 'Sefirah-Fork-Setup.exe'
if (-not (Test-Path -LiteralPath $unsignedInstaller -PathType Leaf)) {
    throw "EXE installer was not found at $unsignedInstaller"
}
$exePath = Join-Path $artifactDirectory "Sefirah-Fork-Setup_${version}_${Architecture}.exe"
Copy-Item -LiteralPath $unsignedInstaller -Destination $exePath -Force
$exeSignature = Set-AuthenticodeSignature -LiteralPath $exePath -Certificate $certificate -HashAlgorithm SHA256
if ($exeSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'The generated EXE installer is not signed by the expected fork certificate.'
}
$exeHash = Get-FileHash -LiteralPath $exePath -Algorithm SHA256
if (Test-Path -LiteralPath $installerPublishDirectory) {
    Remove-Item -LiteralPath $installerPublishDirectory -Recurse -Force
}

Write-Host "Bundle: $outputBundle"
Write-Host "Bundle SHA256: $($bundleHash.Hash)"
Write-Host "Installer ZIP: $zipPath"
Write-Host "ZIP SHA256: $($zipHash.Hash)"
Write-Host "Installer EXE: $exePath"
Write-Host "EXE SHA256: $($exeHash.Hash)"
Write-Host "Back up $signingDirectory before deleting or moving the project."
