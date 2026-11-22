# Publishes ByteAtlas (GUI + byteatlas-cli, self-contained win-x64) into one folder and adds a Start menu shortcut.
# Usage: pwsh packaging\install-windows.ps1 [-Target "D:\Program Files\ByteAtlas"]
param([string]$Target = "D:\Program Files\ByteAtlas")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "publish\win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

foreach ($p in "src\ByteAtlas\ByteAtlas.csproj", "src\ByteAtlas.Cli\ByteAtlas.Cli.csproj") {
    dotnet publish (Join-Path $root $p) -c Release -r win-x64 --self-contained true -o $out -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}
Copy-Item (Join-Path $root "README.md") $out

if (Get-Process ByteAtlas -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Target*" }) {
    throw "ByteAtlas is running from $Target - close it first."
}
New-Item -ItemType Directory -Force $Target | Out-Null
robocopy $out $Target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed ($LASTEXITCODE)" }

$lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "ByteAtlas.lnk"
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $Target "ByteAtlas.exe"
$s.WorkingDirectory = $Target
$s.Description = "ByteAtlas - where did my disk space go?"
$s.Save()
"Installed to $Target; shortcut $lnk"
