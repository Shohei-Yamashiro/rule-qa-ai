# Prints docs\portfolio\guide.html to artifacts\RuleQA-guide.pdf with Microsoft Edge (headless).
# Returns the PDF path, or nothing when Edge is not installed.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$html = Join-Path $root "docs\portfolio\guide.html"
$pdf = Join-Path $root "artifacts\RuleQA-guide.pdf"

$edge = @(
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"),
    (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) {
    Write-Warning "Microsoft Edge was not found, so the guide PDF was not created."
    return
}

New-Item -ItemType Directory -Force (Split-Path $pdf) | Out-Null
if (Test-Path $pdf) { Remove-Item $pdf -Force }

$profileDir = Join-Path ([IO.Path]::GetTempPath()) "ruleqa-docs-edge"
$url = ([Uri]$html).AbsoluteUri
# Edge writes diagnostics to stderr; keep them from stopping the script.
# It sometimes exits before the PDF appears, so wait for it and try once more.
foreach ($attempt in 1..2) {
    $ErrorActionPreference = "Continue"
    & $edge --headless=new --disable-gpu --no-first-run --no-pdf-header-footer "--user-data-dir=$profileDir" "--print-to-pdf=$pdf" $url 2>&1 | Out-Null
    $ErrorActionPreference = "Stop"
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path $pdf) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $pdf) { break }
}

if (-not (Test-Path $pdf)) { throw "Edge did not create $pdf" }
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Created $pdf"
$pdf
