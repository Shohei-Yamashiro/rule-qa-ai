# Builds the zip for distribution: artifacts\RuleQA-win-x64.zip
# and refreshes RuleQA-latest\ at the repository root for manual checking, with the test report.
# The exe is self-contained, so the target PC needs no .NET installation.
param(
    [string]$Runtime = "win-x64",
    # Only build the zip (used by CI): skip RuleQA-latest and the test report.
    [switch]$ZipOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\RuleQA"
$zip = Join-Path $root "artifacts\RuleQA-$Runtime.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root "src\RuleQA.Web") -c Release -r $Runtime -o $out --nologo -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# The IIS in-process module is only loaded when hosted by IIS; the app always runs on Kestrel.
Remove-Item (Join-Path $out "aspnetcorev2_inprocess.dll") -ErrorAction SilentlyContinue

$dist = Join-Path $root "docs\distribution"
Copy-Item (Join-Path $root "docs\README-distribution.txt") (Join-Path $out "README.txt")
Copy-Item (Join-Path $dist "LICENSE.txt") (Join-Path $out "LICENSE.txt")

# Third-party notices: the component list plus the license texts, taken verbatim from the license files
# and from the restored packages (the .NET / ASP.NET Core runtime notices).
$assets = Get-Content (Join-Path $root "src\RuleQA.Web\obj\project.assets.json") -Raw | ConvertFrom-Json
$packages = @($assets.packageFolders.PSObject.Properties.Name)[0]
function Get-PackageFile([string]$id, [string]$file) {
    $dir = Get-ChildItem (Join-Path $packages $id) -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    $path = Join-Path $dir.FullName $file
    if (-not (Test-Path $path)) { throw "License file not found: $path" }
    [IO.File]::ReadAllText($path).TrimEnd()
}
function Section([string]$title, [string]$body) {
    "`r`n`r`n" + ("=" * 60) + "`r`n" + $title + "`r`n" + ("=" * 60) + "`r`n`r`n" + $body
}
$notices = [IO.File]::ReadAllText((Join-Path $dist "third-party-notices.txt")).TrimEnd()
$notices += Section "MIT License" ([IO.File]::ReadAllText((Join-Path $dist "licenses\MIT.txt")).TrimEnd())
$notices += Section "Apache License 2.0" ([IO.File]::ReadAllText((Join-Path $dist "licenses\Apache-2.0.txt")).TrimEnd())
$notices += Section "Appendix: .NET Runtime third-party notices" (Get-PackageFile "microsoft.netcore.app.runtime.$Runtime" "THIRD-PARTY-NOTICES.TXT")
$notices += Section "Appendix: ASP.NET Core Runtime third-party notices" (Get-PackageFile "microsoft.aspnetcore.app.runtime.$Runtime" "THIRD-PARTY-NOTICES.TXT")
[IO.File]::WriteAllText((Join-Path $out "THIRD-PARTY-NOTICES.txt"), ($notices -replace "(?<!`r)`n", "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $true))

$guide = & (Join-Path $PSScriptRoot "build-docs.ps1")
if ($guide) { Copy-Item $guide (Join-Path $out "RuleQA-guide.pdf") }

Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Created $zip ($size MB)"
if ($ZipOnly) { return }

# Copy for manual checking: RuleQA-latest\RuleQA.exe. The data folder (DB and settings) is kept across rebuilds.
$latest = Join-Path $root "RuleQA-latest"
if (Get-Process -Name RuleQA -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($latest, [StringComparison]::OrdinalIgnoreCase) }) {
    throw "RuleQA-latest\RuleQA.exe is running. Close it and run this script again."
}
New-Item -ItemType Directory -Force $latest | Out-Null
Get-ChildItem $latest | Where-Object { $_.Name -ne "data" } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $out "*") $latest -Recurse
Write-Host "Updated $latest\RuleQA.exe"

# Test specification (docs\test-spec.md) and results (RuleQA-latest\test-report.md).
dotnet run (Join-Path $root "scripts\TestReport.cs") -- $root $latest
switch ($LASTEXITCODE) {
    0 { }
    1 { Write-Warning "Some tests failed. See $latest\test-report.md" }
    default { Write-Warning "The test report could not be created." }
}
