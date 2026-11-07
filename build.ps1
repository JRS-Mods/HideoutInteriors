param(
    [string] $SPTPath = 'F:\SPT',
    [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$name = 'HideoutInteriors'
$root = $PSScriptRoot
$client = Join-Path $root "Client\$name.csproj"

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "$SPTPath doesn't look like an SPT folder (no BepInEx\core\BepInEx.dll)."
}

# One version in the project and the plugin.
$version = ([xml](Get-Content $client)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pluginVersion = (Select-String -Path (Join-Path $root 'Client\Plugin.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
if ($version -ne $pluginVersion) { throw "Versions differ: csproj $version, Plugin.cs $pluginVersion." }

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
& $dotnet build $client -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dll = Join-Path $root "Client\bin\Release\$name.dll"
$readme = Join-Path $root 'README.md'
$license = Join-Path $root 'LICENSE.txt'
# The decor button's picture.
$pictures = @(Get-ChildItem -LiteralPath (Join-Path $root 'Client\assets') -Filter '*.png' -File -ErrorAction SilentlyContinue)
foreach ($f in $dll, $readme, $license) { if (-not (Test-Path $f)) { throw "Missing $f" } }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $add = { param($file, $entry) [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entry) | Out-Null }
    & $add $dll "BepInEx/plugins/$name/$name.dll"
    & $add $readme "BepInEx/plugins/$name/README.md"
    & $add $license "BepInEx/plugins/$name/LICENSE.txt"
    foreach ($picture in $pictures) { & $add $picture.FullName "BepInEx/plugins/$name/$($picture.Name)" }
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip"

if (-not $NoInstall) {
    $dest = Join-Path $SPTPath "BepInEx\plugins\$name"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $dll, $readme, $license $dest -Force
    if ($pictures.Count -gt 0) { Copy-Item -LiteralPath $pictures.FullName -Destination $dest -Force }
    Write-Host "Installed to $dest (restart the game)"
}
