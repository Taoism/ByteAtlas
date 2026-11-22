<#
.SYNOPSIS
  Builds every ByteAtlas release package into .\release:
    ByteAtlas-<v>-setup-win-x64.exe     Windows installer (Inno Setup 6)
    ByteAtlas-<v>-win-x64.zip           Windows portable
    byteatlas_<v>_amd64.deb             Debian / Ubuntu
    byteatlas-<v>-1.x86_64.rpm          Fedora / RHEL
    ByteAtlas-<v>-linux-x64.tar.gz      Linux portable
    SHA256SUMS.txt
  Every package has the app (ByteAtlas) and the command-line tool (byteatlas-cli), self-contained.
  The Linux packages are made in WSL (packaging/package-linux.sh: dpkg-deb, rpmbuild in a Fedora container).
#>
param(
    [string]$Iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    [string]$WslDistro = 'Ubuntu',
    [switch]$WindowsOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = ([xml](Get-Content Directory.Build.props)).SelectSingleNode('//Version').InnerText
Write-Host "ByteAtlas $version" -ForegroundColor Cyan

$release = Join-Path $root 'release'
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory $release | Out-Null

$rids = if ($WindowsOnly) { @('win-x64') } else { @('win-x64', 'linux-x64') }
foreach ($rid in $rids) {
    Write-Host "publish $rid"
    $out = Join-Path $root "publish\$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    foreach ($p in 'src\ByteAtlas\ByteAtlas.csproj', 'src\ByteAtlas.Cli\ByteAtlas.Cli.csproj') {
        dotnet publish $p -c Release -r $rid --self-contained -p:DebugType=None -p:DebugSymbols=false -o $out -v q -nologo
        if ($LASTEXITCODE -ne 0) { throw "publish $p ($rid) failed" }
    }
    Copy-Item README.md, THIRD-PARTY-NOTICES.txt $out
}

# Windows installer + portable zip
& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$root\publish\win-x64" "/O$release" installer\byteatlas-setup.iss
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$stage = Join-Path $env:TEMP "byteatlas-zip-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$zipRoot = Join-Path $stage "ByteAtlas-$version-win-x64"
Copy-Item publish\win-x64 $zipRoot -Recurse
Copy-Item LICENSE (Join-Path $zipRoot 'LICENSE.txt')
Compress-Archive -Path $zipRoot -DestinationPath (Join-Path $release "ByteAtlas-$version-win-x64.zip") -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

# Linux packages (in WSL)
if (-not $WindowsOnly) {
    $wslRoot = (wsl.exe -d $WslDistro -- wslpath -a ($root -replace '\\', '/')).Trim()
    wsl.exe -d $WslDistro -- bash "$wslRoot/packaging/package-linux.sh" $version $wslRoot
    if ($LASTEXITCODE -ne 0) { throw 'package-linux.sh failed' }
}

# checksums
Push-Location $release
Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content SHA256SUMS.txt -Encoding ascii
Pop-Location

Get-ChildItem $release | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
