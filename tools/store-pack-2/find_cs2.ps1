# Used by compile_pack.bat. Prints "CS2=<Counter-Strike Global Offensive folder>" and, when the Workshop Tools'
# resource compiler is found anywhere under its game folder, "RC=<full path of resourcecompiler.exe>".
# Searches the folder given as the first argument (if any) and every Steam library.
param([string]$Hint = '')
$ErrorActionPreference = 'SilentlyContinue'
$libs = @()
foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
    $item = Get-ItemProperty $key
    if ($item.SteamPath) { $libs += $item.SteamPath }
    if ($item.InstallPath) { $libs += $item.InstallPath }
}
$libs += 'C:\Program Files (x86)\Steam'
foreach ($s in @($libs)) {
    $vdf = Join-Path ($s -replace '/', '\') 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') }
    }
}
$games = @()
if ($Hint) { $games += $Hint.Trim('"').TrimEnd('\') }
$games += $libs | ForEach-Object { Join-Path ($_ -replace '/', '\') 'steamapps\common\Counter-Strike Global Offensive' }
$cs2 = $null
$rc = $null
foreach ($g in ($games | Select-Object -Unique)) {
    $game = Join-Path $g 'game'
    if (-not (Test-Path $game)) { continue }
    if (-not $cs2) { $cs2 = $g }
    $known = Join-Path $game 'bin\win64\resourcecompiler.exe'
    if (Test-Path $known) { $cs2 = $g; $rc = $known; break }
    $found = Get-ChildItem -Path $game -Filter 'resourcecompiler*.exe' -Recurse -Depth 4 -File | Select-Object -First 1
    if ($found) { $cs2 = $g; $rc = $found.FullName; break }
}
if ($cs2) { Write-Output ('CS2=' + $cs2) }
if ($rc) { Write-Output ('RC=' + $rc) }
