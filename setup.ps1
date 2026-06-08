# setup.ps1
# Setup script to download NAudio and prepare directory structure

$ErrorActionPreference = "Stop"

Write-Host "Creating directories..."
$currentDir = Get-Location
$webDir = Join-Path $currentDir "web"
if (!(Test-Path $webDir)) {
    New-Item -ItemType Directory -Path $webDir | Out-Null
}

$tempZip = Join-Path $currentDir "naudio_temp.zip"
$tempExtract = Join-Path $currentDir "naudio_temp_extract"

Write-Host "Downloading NAudio 1.10.0 NuGet package..."
$url = "https://www.nuget.org/api/v2/package/NAudio/1.10.0"
Invoke-WebRequest -Uri $url -OutFile $tempZip

Write-Host "Extracting NAudio..."
if (Test-Path $tempExtract) {
    Remove-Item -Recurse -Force $tempExtract
}
Expand-Archive -Path $tempZip -DestinationPath $tempExtract

Write-Host "Searching for NAudio.dll in extracted files..."
# Find NAudio.dll under a .NET framework library directory
$dllFile = Get-ChildItem -Path $tempExtract -Filter "NAudio.dll" -Recurse | Where-Object { $_.FullName -like "*lib\net*" } | Select-Object -First 1

if (!$dllFile) {
    # Fallback to any NAudio.dll
    $dllFile = Get-ChildItem -Path $tempExtract -Filter "NAudio.dll" -Recurse | Select-Object -First 1
}

if ($dllFile) {
    Copy-Item -Path $dllFile.FullName -Destination $currentDir -Force
    Write-Host "NAudio.dll found at $($dllFile.FullName) and successfully copied to project root." -ForegroundColor Green
} else {
    throw "Could not find NAudio.dll in extracted files."
}

Write-Host "Cleaning up temporary files..."
Remove-Item -Path $tempZip -Force
Remove-Item -Recurse -Force $tempExtract

Write-Host "Setup complete!" -ForegroundColor Green
