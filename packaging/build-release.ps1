# Builds the portable Windows package release\ByteAtlas-<version>-win-x64.zip: ByteAtlas.exe (GUI) and
# byteatlas-cli.exe side by side, self-contained (no .NET install needed).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.SelectSingleNode("//Version").InnerText
$pub = Join-Path $root "publish\win-x64"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
foreach ($p in "src\ByteAtlas\ByteAtlas.csproj", "src\ByteAtlas.Cli\ByteAtlas.Cli.csproj") {
    dotnet publish (Join-Path $root $p) -c Release -r win-x64 --self-contained true -o $pub -p:DebugType=None -p:DebugSymbols=false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "THIRD-PARTY-NOTICES.txt") $pub
Copy-Item (Join-Path $root "LICENSE") (Join-Path $pub "LICENSE.txt")
$out = Join-Path $root "release"
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "ByteAtlas-$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pub "*") -DestinationPath $zip -CompressionLevel Optimal
"{0}  {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)
