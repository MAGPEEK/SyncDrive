<#
.SYNOPSIS
  Baut die Windows-Pakete des Clients: SyncDrive-Client-<Version>-<Build>-x64.msi und .exe (Setup mit der MSI darin).

.DESCRIPTION
  1. Veröffentlicht Oberfläche (syncdrive.exe mit Daemon), syncdrive_cli.exe und syncdrive-cloudreg.exe eigenständig (win-x64, mit .NET-Laufzeit)
     in einen gemeinsamen Ordner.
  2. Baut daraus die MSI (WiX 5) und das Setup-Programm.
  Die Version kommt aus Directory.Build.props, die Build-Nummer aus der Zahl der Git-Commits (oder -Build).

.EXAMPLE
  pwsh installer/build-client.ps1
  pwsh installer/build-client.ps1 -Build 42 -Output C:\Pakete
#>
param(
    [int] $Build = -1,
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\client')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($Build -lt 0) {
    $Build = [int](git -C $root rev-list --count HEAD 2>$null)
}

$name = "SyncDrive-Client-$version-$Build-x64"
$work = Join-Path $root 'artifacts\obj\client'
$publish = Join-Path $work 'app'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $Output | Out-Null

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) fehlgeschlagen ($LASTEXITCODE)" }
}

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', "-p:PublishDir=$publish\", '-p:DebugType=none', '-p:SkipCloudRegistrationCopy=true', '--nologo')
foreach ($project in 'SyncDrive.Client.CloudRegistration', 'SyncDrive.Client.Cli', 'SyncDrive.Client.Tray') {
    Write-Host "Veröffentliche $project ..."
    Invoke-Dotnet publish (Join-Path $root "src\$project\$project.csproj") @common
}

Write-Host 'Baue MSI ...'
$msiOut = Join-Path $work 'msi'
Invoke-Dotnet build (Join-Path $PSScriptRoot 'SyncDrive.Client.Msi\SyncDrive.Client.Msi.wixproj') -c Release --nologo `
    "-p:PublishDir=$publish" "-p:OutputPath=$msiOut\" "-p:BaseIntermediateOutputPath=$work\msi-obj\"
$msi = Join-Path $Output "$name.msi"
Copy-Item (Join-Path $msiOut 'SyncDrive-Client.msi') $msi -Force

Write-Host 'Baue Setup ...'
$setupOut = Join-Path $work 'setup'
Invoke-Dotnet build (Join-Path $PSScriptRoot 'SyncDrive.Client.Setup\SyncDrive.Client.Setup.wixproj') -c Release --nologo `
    "-p:MsiPath=$msi" "-p:OutputPath=$setupOut\" "-p:BaseIntermediateOutputPath=$work\setup-obj\"
Copy-Item (Join-Path $setupOut 'SyncDrive-Client-Setup.exe') (Join-Path $Output "$name.exe") -Force

Get-ChildItem $Output -Filter "$name.*" | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content (Join-Path $Output "$name.sha256") -Encoding ascii

Get-ChildItem $Output -Filter "$name.*" | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
