<#
.SYNOPSIS
  Writes the winget and Scoop manifests for a release from release\SHA256SUMS.txt:
    packaging\winget\<version>\Taoism.ByteAtlas*.yaml   (copy into a winget-pkgs pull request:
                                                         manifests\t\Taoism\ByteAtlas\<version>\)
    bucket\byteatlas.json                               (Scoop: scoop bucket add byteatlas https://github.com/Taoism/ByteAtlas)
  The download URLs point at the GitHub release v<version>, so publish the release before submitting.
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).SelectSingleNode('//Version').InnerText }
$sums = @{}
foreach ($line in Get-Content (Join-Path $root 'release\SHA256SUMS.txt')) {
    $hash, $name = $line -split '\s+', 2
    $sums[$name.Trim()] = $hash.ToUpperInvariant()
}
$base = "https://github.com/Taoism/ByteAtlas/releases/download/v$Version"
$setup = "ByteAtlas-$Version-setup-win-x64.exe"
$zip = "ByteAtlas-$Version-win-x64.zip"
foreach ($f in $setup, $zip) { if (-not $sums.ContainsKey($f)) { throw "$f is not in SHA256SUMS.txt" } }

# ---------- winget (manifest schema 1.9) ----------
$id = 'Taoism.ByteAtlas'
$dir = Join-Path $root "packaging\winget\$Version"
New-Item -ItemType Directory $dir -Force | Out-Null
$header = '# yaml-language-server: $schema=https://aka.ms/winget-manifest.{0}.1.9.0.schema.json'
@"
$($header -f 'version')
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8NoBOM

@"
$($header -f 'installer')
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: inno
UpgradeBehavior: install
ProductCode: '{2EBCAE69-DB6E-4FDF-BCAE-FB3D2FA80E68}_is1'
Commands:
- byteatlas-cli
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
- Architecture: x64
  Scope: user
  InstallerUrl: $base/$setup
  InstallerSha256: $($sums[$setup])
  InstallerSwitches:
    Custom: /CURRENTUSER
- Architecture: x64
  Scope: machine
  InstallerUrl: $base/$setup
  InstallerSha256: $($sums[$setup])
  InstallerSwitches:
    Custom: /ALLUSERS
ManifestType: installer
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8NoBOM

@"
$($header -f 'defaultLocale')
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Taoism
PublisherUrl: https://github.com/Taoism
PackageName: ByteAtlas
PackageUrl: https://github.com/Taoism/ByteAtlas
License: MIT
LicenseUrl: https://github.com/Taoism/ByteAtlas/blob/main/LICENSE
ShortDescription: Disk space analyzer - see what takes up the space on your drives
Description: |-
  ByteAtlas shows what fills your drives: a size tree, treemap / pie / bar charts, file types, owners, age of files,
  duplicate files and folders, history, file search, exports (Excel, PDF, HTML, CSV) and a command-line tool.
  Reads the NTFS master file table for fast scans; also SSH, S3, Azure Blob, WebDAV and disk images.
Moniker: byteatlas
Tags:
- disk-space
- disk-usage
- treemap
- duplicates
- storage
ReleaseNotesUrl: https://github.com/Taoism/ByteAtlas/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.locale.en-US.yaml") -Encoding utf8NoBOM

# ---------- Scoop ----------
$bucket = Join-Path $root 'bucket'
New-Item -ItemType Directory $bucket -Force | Out-Null
$scoop = [ordered]@{
    version     = $Version
    description = 'Disk space analyzer: size tree, treemap, file types, duplicates, history, exports, CLI'
    homepage    = 'https://github.com/Taoism/ByteAtlas'
    license     = 'MIT'
    architecture = [ordered]@{
        '64bit' = [ordered]@{
            url        = "$base/$zip"
            hash       = $sums[$zip].ToLowerInvariant()
            extract_dir = "ByteAtlas-$Version-win-x64"
        }
    }
    bin         = 'byteatlas-cli.exe'
    shortcuts   = @(, @('ByteAtlas.exe', 'ByteAtlas'))
    persist     = @()
    checkver    = [ordered]@{ github = 'https://github.com/Taoism/ByteAtlas' }
    autoupdate  = [ordered]@{
        architecture = [ordered]@{
            '64bit' = [ordered]@{
                url         = 'https://github.com/Taoism/ByteAtlas/releases/download/v$version/ByteAtlas-$version-win-x64.zip'
                extract_dir = 'ByteAtlas-$version-win-x64'
            }
        }
        hash = [ordered]@{ url = '$baseurl/SHA256SUMS.txt' }
    }
}
$scoop.Remove('persist')
$scoop | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $bucket 'byteatlas.json') -Encoding utf8NoBOM
Write-Host "manifests: packaging\winget\$Version, bucket\byteatlas.json"
