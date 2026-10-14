# Uploads a compiled SWBS pack to the Steam Workshop WITHOUT the CS2 Workshop Tools (no GPU needed).
# Used by upload_workshop.bat. Steps:
#   1. finds CS2 (find_cs2.ps1) and the compiled addon in game\csgo_addons\<addon>
#   2. gets SteamCMD (Valve's command line Steam client) into .\steamcmd if it is not there yet
#   3. first run only: creates the Workshop item, so it has an id (kept in workshop_id.txt)
#   4. packs every compiled file (*_c) into <id>_dir.vpk - the file MultiAddonManager mounts
#   5. uploads it (Unlisted) with preview.png
param(
    [Parameter(Mandatory = $true)][string]$Addon,
    [Parameter(Mandatory = $true)][string]$Title,
    [string]$Description = "",
    [string]$Cs2 = "",
    [string]$PackOnly = ""  # testing: pack this folder into the vpk path given by -VpkOut, do not upload
    , [string]$VpkOut = ""
)
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ------------------------------------------------------------------ VPK (version 2, all data in the _dir file)
Add-Type -TypeDefinition @"
public static class SwbsCrc32 {
    static readonly uint[] Table = Make();
    static uint[] Make() {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    public static uint Compute(byte[] data) {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
"@
function Get-Crc32([byte[]]$Data) { return [SwbsCrc32]::Compute($Data) }

function Write-Vpk([string]$Root, [string]$Out) {
    $files = Get-ChildItem -Path $Root -Recurse -File | Where-Object { $_.Extension -like '*_c' }
    if (-not $files) { throw "No compiled files (*_c) under $Root" }
    # group: extension -> directory -> [names]
    $tree = @{}
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($Root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        $ext = $f.Extension.TrimStart('.').ToLowerInvariant()
        $dir = [System.IO.Path]::GetDirectoryName($rel)
        if ([string]::IsNullOrEmpty($dir)) { $dir = ' ' } else { $dir = $dir.Replace('\', '/').ToLowerInvariant() }
        $name = [System.IO.Path]::GetFileNameWithoutExtension($rel).ToLowerInvariant()
        if (-not $tree.ContainsKey($ext)) { $tree[$ext] = @{} }
        if (-not $tree[$ext].ContainsKey($dir)) { $tree[$ext][$dir] = New-Object System.Collections.ArrayList }
        [void]$tree[$ext][$dir].Add(@($name, $f.FullName))
    }
    $enc = [System.Text.Encoding]::UTF8
    $treeMs = New-Object System.IO.MemoryStream
    $tw = New-Object System.IO.BinaryWriter($treeMs)
    $dataMs = New-Object System.IO.MemoryStream
    foreach ($ext in ($tree.Keys | Sort-Object)) {
        $tw.Write($enc.GetBytes($ext)); $tw.Write([byte]0)
        foreach ($dir in ($tree[$ext].Keys | Sort-Object)) {
            $tw.Write($enc.GetBytes($dir)); $tw.Write([byte]0)
            foreach ($entry in $tree[$ext][$dir]) {
                $bytes = [System.IO.File]::ReadAllBytes($entry[1])
                $tw.Write($enc.GetBytes($entry[0])); $tw.Write([byte]0)
                $tw.Write([uint32](Get-Crc32 $bytes))
                $tw.Write([uint16]0)            # preload bytes
                $tw.Write([uint16]0x7FFF)       # archive index: data is in this _dir file
                $tw.Write([uint32]$dataMs.Position)
                $tw.Write([uint32]$bytes.Length)
                $tw.Write([uint16]0xFFFF)       # terminator
                $dataMs.Write($bytes, 0, $bytes.Length)
            }
            $tw.Write([byte]0)
        }
        $tw.Write([byte]0)
    }
    $tw.Write([byte]0)
    $tw.Flush()
    $treeBytes = $treeMs.ToArray()
    $dataBytes = $dataMs.ToArray()
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $outMs = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($outMs)
    $w.Write([uint32]0x55AA1234); $w.Write([uint32]2)
    $w.Write([uint32]$treeBytes.Length); $w.Write([uint32]$dataBytes.Length)
    $w.Write([uint32]0)    # archive MD5 section
    $w.Write([uint32]48)   # other MD5 section
    $w.Write([uint32]0)    # signature section
    $w.Write($treeBytes); $w.Write($dataBytes)
    $w.Write($md5.ComputeHash($treeBytes))
    $w.Write($md5.ComputeHash([byte[]]@()))
    $w.Flush()
    $sofar = $outMs.ToArray()
    $w.Write($md5.ComputeHash($sofar))
    $w.Flush()
    [System.IO.File]::WriteAllBytes($Out, $outMs.ToArray())
    return $files.Count
}

if ($PackOnly) {
    $n = Write-Vpk $PackOnly $VpkOut
    Write-Output "packed $n files into $VpkOut"
    exit 0
}

# ------------------------------------------------------------------ find the compiled addon
if (-not $Cs2) {
    foreach ($line in (& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Here 'find_cs2.ps1'))) {
        if ($line -like 'CS2=*') { $Cs2 = $line.Substring(4) }
    }
}
if (-not $Cs2) { throw "Counter-Strike 2 not found. Run: upload_workshop.bat ""D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive""" }
$Compiled = Join-Path $Cs2 "game\csgo_addons\$Addon"
if (-not (Test-Path $Compiled)) { throw "Compiled addon not found: $Compiled  (run compile_pack.bat first)" }
Write-Output "Compiled addon: $Compiled"

# ------------------------------------------------------------------ SteamCMD
$SteamCmdDir = Join-Path $Here 'steamcmd'
$SteamCmd = Join-Path $SteamCmdDir 'steamcmd.exe'
if (-not (Test-Path $SteamCmd)) {
    Write-Output "Downloading SteamCMD (Valve's command line Steam client)..."
    New-Item -ItemType Directory -Force -Path $SteamCmdDir | Out-Null
    $zip = Join-Path $SteamCmdDir 'steamcmd.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $SteamCmdDir -Force
}

$UserFile = Join-Path $Here 'steam_user.txt'
if (Test-Path $UserFile) { $User = (Get-Content $UserFile -Raw).Trim() } else {
    $User = Read-Host "Steam account name (the account that will own the Workshop item)"
    Set-Content -Path $UserFile -Value $User
}

$Work = Join-Path $Here 'workshop_upload'
$Vdf = Join-Path $Work 'item.vdf'
$Content = Join-Path $Work 'content'
$Preview = Join-Path $Here 'preview.png'
New-Item -ItemType Directory -Force -Path $Work | Out-Null

function Write-ItemVdf([string]$Id, [string]$Note) {
    $esc = { param($s) $s.Replace('\', '/').Replace('"', "'") }
    @"
"workshopitem"
{
    "appid" "730"
    "publishedfileid" "$Id"
    "contentfolder" "$(& $esc $Content)"
    "previewfile" "$(& $esc $Preview)"
    "visibility" "3"
    "title" "$(& $esc $Title)"
    "description" "$(& $esc $Description)"
    "changenote" "$(& $esc $Note)"
}
"@ | Set-Content -Path $Vdf -Encoding ASCII
}

function Invoke-Upload {
    Write-Output "Logging in to Steam as $User (enter your password / Steam Guard code if asked)..."
    & $SteamCmd +login $User +workshop_build_item ($Vdf.Replace('\', '/')) +quit
    if ($LASTEXITCODE -ne 0) { throw "SteamCMD failed (exit code $LASTEXITCODE). Read the messages above." }
}

# ------------------------------------------------------------------ first run: create the item to get its id
$IdFile = Join-Path $Here 'workshop_id.txt'
$Id = if (Test-Path $IdFile) { (Get-Content $IdFile -Raw).Trim() } else { '' }
if (-not $Id) {
    Write-Output "Creating the Workshop item (first upload)..."
    if (Test-Path $Content) { Remove-Item -Recurse -Force $Content }
    New-Item -ItemType Directory -Force -Path $Content | Out-Null
    Set-Content -Path (Join-Path $Content 'readme.txt') -Value 'SWBS pack - upload in progress'
    Write-ItemVdf '0' 'Created'
    Invoke-Upload
    $m = Select-String -Path $Vdf -Pattern '"publishedfileid"\s+"(\d+)"'
    if (-not $m -or $m.Matches[0].Groups[1].Value -eq '0') { throw "SteamCMD did not report the new item id. Read the messages above." }
    $Id = $m.Matches[0].Groups[1].Value
    Set-Content -Path $IdFile -Value $Id
    Write-Output "Created Workshop item $Id"
}

# ------------------------------------------------------------------ pack and upload
if (Test-Path $Content) { Remove-Item -Recurse -Force $Content }
New-Item -ItemType Directory -Force -Path $Content | Out-Null
Write-Output "Packing the compiled files into ${Id}_dir.vpk..."
$n = Write-Vpk $Compiled (Join-Path $Content "${Id}_dir.vpk")
Write-Output "  $n files"
Write-ItemVdf $Id ("Update " + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Invoke-Upload
Write-Output ""
Write-Output "UPLOADED: https://steamcommunity.com/sharedfiles/filedetails/?id=$Id  (Unlisted)"
Write-Output "Server: add $Id to mm_extra_addons in cfg/multiaddonmanager/multiaddonmanager.cfg"
