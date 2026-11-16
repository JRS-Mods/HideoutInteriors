param(
    [string] $SPTPath = 'F:\SPT',
    # The profile to log in as (its id): it should have a built hideout.
    [string] $ProfileId = '6af864d87d91570b6c5dbbdc',
    [int] $Minutes = 9
)
# The mod trying itself in the game, muted, with nobody at the keyboard (Client/Interiors.SelfTest.cs). The profile's
# layout files are backed up first and put back after, the release build is installed again at the end, and the results
# and pictures are left in dev/selftest-out.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot 'selftest-out'
$config = Join-Path $SPTPath 'BepInEx\config'
$plugin = Join-Path $SPTPath 'BepInEx\plugins\HideoutInteriors'
$backup = Join-Path $out 'backup'

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
& $dotnet build (Join-Path $root 'Client\HideoutInteriors.csproj') -c Release -p:SelfTest=true "-p:SPTPath=$SPTPath" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Self-test build failed.' }

# Someone may be playing: never closed from here.
if (Get-Process EscapeFromTarkov -ErrorAction SilentlyContinue) { throw 'The game is running: close it first (the test needs it to itself).' }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $backup | Out-Null
Copy-Item (Join-Path $config 'jrs.hideoutinteriors.*') $backup
$hadPhotos = Test-Path (Join-Path $plugin 'photos')
Copy-Item (Join-Path $root 'Client\bin\SelfTest\HideoutInteriors.dll') $plugin -Force

$startedServer = $false
try {
    if (-not (Get-Process SPT.Server -ErrorAction SilentlyContinue)) {
        $startedServer = $true
        $serverLog = Join-Path $SPTPath 'BepInEx\interiors_selftest_server.log'
        Start-Process (Join-Path $SPTPath 'SPT_Runtime\SPT.Server.exe') -WorkingDirectory (Join-Path $SPTPath 'SPT_Runtime') -WindowStyle Minimized -RedirectStandardOutput $serverLog
        $deadline = (Get-Date).AddMinutes(4)
        while ((Get-Date) -lt $deadline -and -not (Select-String -Path $serverLog -Pattern 'Server has started' -Quiet -ErrorAction SilentlyContinue)) { Start-Sleep 3 }
    }
    $env:HIDEOUT_INTERIORS_TEST = $out
    $cfg = '{\"BackendUrl\":\"https://127.0.0.1:6969\",\"Version\":\"live\",\"MatchingVersion\":\"live\"}'
    Start-Process (Join-Path $SPTPath 'EscapeFromTarkov.exe') -WorkingDirectory $SPTPath -ArgumentList "-token=$ProfileId -config=$cfg"
    $results = Join-Path $out 'results.txt'
    $deadline = (Get-Date).AddMinutes($Minutes)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $results) -and (Select-String -Path $results -Pattern '^DONE|run stopped' -Quiet)) { break }
        Start-Sleep 10
    }
    Start-Sleep 5
}
finally {
    Get-Process EscapeFromTarkov -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($startedServer) { Get-Process SPT.Server -ErrorAction SilentlyContinue | Stop-Process -Force }
    Start-Sleep 3
    # The profile's files as they were; what the run made, gone.
    $kept = (Get-ChildItem $backup -File).Name
    Get-ChildItem (Join-Path $config 'jrs.hideoutinteriors.*') | Where-Object { $kept -notcontains $_.Name } | Remove-Item
    Copy-Item (Join-Path $backup '*') $config -Force
    if (-not $hadPhotos) { Remove-Item (Join-Path $plugin 'photos') -Recurse -Force -ErrorAction SilentlyContinue }
    Copy-Item (Join-Path $root 'Client\bin\Release\HideoutInteriors.dll') $plugin -Force
}
if (Test-Path (Join-Path $out 'results.txt')) { Select-String -Path (Join-Path $out 'results.txt') -Pattern '^(FAIL|ERROR|SKIP|DONE)' | ForEach-Object { $_.Line } }
else { Write-Warning 'No results: the game never got to the hideout.' }
