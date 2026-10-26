$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $RepoRoot

$ProjectPath = "src/PcHwCtl.Service/PcHwCtl.Service.csproj"
$OutputDir = "publish/"

Write-Host "Publishing $ProjectPath in Release mode (win-x64, framework-dependent) to $OutputDir..."

& dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Publish completed successfully."
