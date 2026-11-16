param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
& $Dotnet publish "$PSScriptRoot\StarStringsUpdater\StarStringsUpdater.csproj" -c Release -r win-x64 -o "$PSScriptRoot\dist"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Write-Host "Built $PSScriptRoot\dist\StarStringsUpdater.exe"
