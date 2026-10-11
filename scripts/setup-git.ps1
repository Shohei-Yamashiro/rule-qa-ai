# One-time git settings for this clone: the pre-push hook and the merge driver for generated files.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

git config core.hooksPath .githooks
git config merge.ours.driver true

Write-Host 'Git settings applied: core.hooksPath=.githooks, merge.ours.driver=true'
