# Writes Scoop and winget manifests for a release of Glint.exe.
param([string]$Version, [string]$Sha256, [string]$Out = 'manifests')
$ErrorActionPreference = 'Stop'
$url = "https://github.com/Benfranklinms/Glint/releases/download/v$Version/Glint.exe"
New-Item -ItemType Directory -Force "$Out/scoop", "$Out/winget" | Out-Null

@"
{
  "version": "$Version",
  "description": "Spotlight-style search for Windows: files, apps, Settings and sums, typos forgiven.",
  "homepage": "https://github.com/Benfranklinms/Glint",
  "license": "MIT",
  "url": "$url",
  "hash": "$($Sha256.ToLower())",
  "bin": "Glint.exe",
  "shortcuts": [["Glint.exe", "Glint"]],
  "checkver": { "github": "https://github.com/Benfranklinms/Glint" },
  "autoupdate": {
    "url": "https://github.com/Benfranklinms/Glint/releases/download/v`$version/Glint.exe",
    "hash": { "url": "`$url.sha256" }
  }
}
"@ | Set-Content "$Out/scoop/glint.json"

$id = 'Benfranklinms.Glint'
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content "$Out/winget/$id.yaml"

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
- glint
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $($Sha256.ToUpper())
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content "$Out/winget/$id.installer.yaml"

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Benfranklinms
PackageName: Glint
License: MIT
ShortDescription: Spotlight-style search for Windows - files, apps, Settings and sums, typos forgiven.
PackageUrl: https://github.com/Benfranklinms/Glint
Tags:
- search
- launcher
- spotlight
- file-search
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content "$Out/winget/$id.locale.en-US.yaml"
Write-Host "Manifests for $Version written to $Out"
