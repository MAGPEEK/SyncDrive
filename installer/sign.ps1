<#
.SYNOPSIS
  Signiert Windows-Dateien mit Azure Artifact Signing (früher Trusted Signing), damit Smart App Control und SmartScreen sie zulassen.

.DESCRIPTION
  Endpunkt, Konto und Zertifikatprofil kommen aus den Umgebungsvariablen SYNCDRIVE_SIGNING_ENDPOINT, SYNCDRIVE_SIGNING_ACCOUNT und
  SYNCDRIVE_SIGNING_PROFILE. Ohne Konto bleibt alles unsigniert (lokale Builds). Bei Azure meldet DefaultAzureCredential an:
  in GitHub Actions über azure/login, lokal über "az login". Schon gültig signierte Dateien (z. B. die .NET-Laufzeit) bleiben unverändert.
  Aufgerufen von build-client.ps1 und vom Setup-Projekt (Burn-Engine und Setup).

.EXAMPLE
  pwsh installer/sign.ps1 -Path artifacts\client\SyncDrive-Client-0.41.2-120-x64.msi
#>
param(
    [Parameter(Mandatory)] [string[]] $Path
)

$ErrorActionPreference = 'Stop'
if (-not $env:SYNCDRIVE_SIGNING_ACCOUNT) {
    Write-Host 'Nicht signiert: SYNCDRIVE_SIGNING_ACCOUNT ist nicht gesetzt.'
    return
}

$files = @($Path | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
if ($files.Count -eq 0) {
    return
}
Write-Host "Signiere $($files.Count) Datei(en) ..."

$moduleVersion = '0.5.8'
if (-not (Get-Module -ListAvailable -Name TrustedSigning | Where-Object Version -eq $moduleVersion)) {
    Install-Module -Name TrustedSigning -RequiredVersion $moduleVersion -Scope CurrentUser -Force -Repository PSGallery
}
Import-Module TrustedSigning -RequiredVersion $moduleVersion

# Liste statt Kommandozeile: der Client bringt über hundert DLLs mit
$catalog = New-TemporaryFile
try {
    Set-Content $catalog $files -Encoding utf8
    Invoke-TrustedSigning -Endpoint $env:SYNCDRIVE_SIGNING_ENDPOINT -CodeSigningAccountName $env:SYNCDRIVE_SIGNING_ACCOUNT `
        -CertificateProfileName $env:SYNCDRIVE_SIGNING_PROFILE -FilesCatalog $catalog -FileDigest SHA256 `
        -TimestampRfc3161 'http://timestamp.acs.microsoft.com' -TimestampDigest SHA256
} finally {
    Remove-Item $catalog -ErrorAction SilentlyContinue
}

$unsigned = @($files | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
if ($unsigned.Count -gt 0) {
    throw "Nicht signiert: $($unsigned -join ', ')"
}
