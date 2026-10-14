#!/usr/bin/env python3
"""
Builds the SWBS Jailbreak workshop pack from the SMPlugins fork (hlstriker/Swoobles assets).

Output (a zip and the folder it was made from):
  swbs_jailbreak/                     -> copy into  ...\\Counter-Strike Global Offensive\\content\\csgo_addons\\swbs_jailbreak\\
      sounds/swbs/...                    the original sounds, converted to 44.1 kHz 16-bit mono WAV (CS2's compiler input)
      soundevents/swbs_jailbreak.vsndevts   one sound event per sound (names used by the plugin's WorkshopPack module)
  compile_pack.bat + find_cs2.ps1     finds CS2, copies the addon in, compiles and checks it (or use the Asset Browser)
  preview.png                         512x512 Workshop preview image
  README.txt                          step by step: compile, upload, MultiAddonManager, plugin config
  source1_models_to_convert/          the CS:GO models/materials (cannot be used as-is in CS2: see CONVERT_MODELS.txt)

Usage: build_pack.py <smplugins checkout> <output dir>
Needs ffmpeg on PATH; Pillow for the preview image (optional).
"""
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import props  # noqa: E402  (CS:GO props -> CS2 models)

ADDON = "swbs_jailbreak"
EVENTS_FILE = "soundevents/swbs_jailbreak.vsndevts"

# event name -> (volume, [source files relative to the fork], target folder under sounds/swbs)
# Left out on purpose: SpeedRuns announcer lines (clips from other games), map vote and Blocks sounds (unused plugins).
SOUNDS = {
    "swbs.guards_win": (1.0, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/guards_win_v5.mp3"], "jailbreak"),
    "swbs.prisoners_win": (1.0, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/prisoners_win_v5.mp3"], "jailbreak"),
    "swbs.lr_activated": (1.0, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/last_request_activated.mp3"], "jailbreak"),
    "swbs.freekill": (1.0, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/freekill.mp3"], "jailbreak"),
    "swbs.chat_alert": (0.9, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/chat_alert.mp3"], "jailbreak"),
    "swbs.cowbell": (0.5, ["Mods/UltimateJailbreak/files/sound/swoobles/ultimate_jailbreak/cowbell.mp3"], "jailbreak"),
    "swbs.rpg_shoot": (1.0, ["Plugins/CustomWeapons/files/sound/swoobles/rocket_jumping/shoot_v1.mp3"], "rpg"),
    "swbs.rpg_explode": (1.0, [f"Plugins/CustomWeapons/files/sound/swoobles/rocket_jumping/explode{i}.mp3" for i in range(1, 5)], "rpg"),
    "swbs.rpg_pain": (0.9, [f"Plugins/CustomWeapons/files/sound/swoobles/rocket_jumping/pain{i}.mp3" for i in range(1, 3)], "rpg"),
    "swbs.rpg_death": (1.0, ["Plugins/CustomWeapons/files/sound/swoobles/rocket_jumping/death.mp3"], "rpg"),
    "swbs.jetpack": (0.7, ["Plugins/Jetpacks/files/sound/swoobles/jetpack/jetpack_v1.mp3"], "misc"),
    "swbs.slap": (1.0, ["Plugins/Blocks/files/sound/swoobles/blocks/slap/slap2.mp3"], "misc"),
    "swbs.teleport": (0.9, ["Plugins/Blocks/files/sound/swoobles/blocks/teleport/teleport.mp3"], "misc"),
}

# Source 1 (CS:GO) assets that still need a person (re-rigging onto the CS2 player skeleton): the player models.
# The props are converted automatically by props.py.
SOURCE1_MODELS = [
    "Mods/UltimateJailbreak/files/models/player/custom_player/swoobles",
]
SOURCE1_MATERIALS = [
    "Mods/UltimateJailbreak/files/materials/swoobles/player",
]

KV3_HEADER = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->"


def convert(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-ac", "1", "-ar", "44100", "-sample_fmt", "s16", str(dst)], check=True)


def soundevents(entries: dict) -> str:
    lines = [KV3_HEADER, "{"]
    for name, (volume, files) in entries.items():
        lines.append(f'\t"{name}" =')
        lines.append("\t{")
        lines.append('\t\ttype = "csgo_mega"')
        lines.append(f"\t\tvolume = {volume:.2f}")
        if len(files) == 1:
            lines.append(f'\t\tvsnd_files_track_01 = "{files[0]}"')
        else:
            lines.append("\t\tvsnd_files_track_01 =")
            lines.append("\t\t[")
            for f in files:
                lines.append(f'\t\t\t"{f}",')
            lines.append("\t\t]")
        lines.append("\t}")
    lines.append("}")
    return "\n".join(lines) + "\n"


COMPILE_BAT = r"""@echo off
setlocal EnableExtensions
title SWBS workshop pack - compile
rem Finds CS2 (any Steam library) and its resource compiler, copies the pack into content\csgo_addons\swbs_jailbreak,
rem compiles it and checks the result. Optional: drag your "Counter-Strike Global Offensive" folder onto this file.
set "PACK=%~dp0"
set "LOG=%PACK%compile_log.txt"
echo ============================================
echo  SWBS workshop pack - compile
echo ============================================
echo Running from: %PACK%
echo.
if not exist "%PACK%find_cs2.ps1" goto :not_extracted

echo Looking for Counter-Strike 2 and the Workshop Tools compiler (can take a few seconds)...
set "CS2="
set "RC="
for /f "usebackq tokens=1,* delims==" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PACK%find_cs2.ps1" "%~1"`) do set "%%A=%%B"
if "%CS2%"=="" goto :no_cs2
set "DEST=%CS2%\content\csgo_addons\swbs_jailbreak"
set "OUT=%CS2%\game\csgo_addons\swbs_jailbreak"
echo   CS2 folder: %CS2%
if not "%RC%"=="" echo   Compiler:   %RC%
if "%RC%"=="" echo   Compiler:   not found in this CS2 folder
echo.

rem The pack's swbs_jailbreak folder: next to this file, one folder down (zip extracted into a folder of the same
rem name), or already copied into CS2's content folder by hand.
set "SRC="
if exist "%PACK%swbs_jailbreak\soundevents\swbs_jailbreak.vsndevts" set "SRC=%PACK%swbs_jailbreak"
if "%SRC%"=="" if exist "%PACK%SwbsJailbreak-workshop-pack\swbs_jailbreak\soundevents\swbs_jailbreak.vsndevts" set "SRC=%PACK%SwbsJailbreak-workshop-pack\swbs_jailbreak"
if "%SRC%"=="" if exist "%DEST%\soundevents\swbs_jailbreak.vsndevts" goto :already_copied
if "%SRC%"=="" goto :no_source

echo [1/3] Copying the pack into
echo       %DEST%
robocopy "%SRC%" "%DEST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :copy_failed
goto :compile

:already_copied
echo [1/3] The pack is already in
echo       %DEST%
echo       (no swbs_jailbreak folder next to this file, so using those files)

:compile
if "%RC%"=="" goto :no_compiler
echo [2/3] Compiling (the compiler output goes to compile_log.txt)...
"%RC%" -r -i "%DEST%\*" > "%LOG%" 2>&1

:check
echo [3/3] Checking the result...
if not exist "%OUT%\soundevents\swbs_jailbreak.vsndevts_c" goto :compile_failed
set /a N=0
for /r "%OUT%\sounds" %%F in (*.vsnd_c) do set /a N+=1
set /a M=0
if exist "%OUT%\models" for /r "%OUT%\models" %%F in (*.vmdl_c) do set /a M+=1
echo.
echo SUCCESS: the sound events, %N% sounds and %M% of 5 models are compiled in
echo   %OUT%
if %M% LSS 5 echo NOTE: not every model compiled. Send compile_log.txt; until then set "UseModels": false in WorkshopPack.
echo.
echo Next: start the CS2 Workshop Tools, pick the swbs_jailbreak addon and upload it with the
echo Workshop Manager (README.txt step 4).
goto :end

:no_compiler
if exist "%OUT%\soundevents\swbs_jailbreak.vsndevts_c" goto :check
echo.
echo The files are copied. This CS2 folder has no resourcecompiler.exe, so compile inside the Workshop Tools:
echo   1. Steam - Counter-Strike 2 - Play - "Launch Counter-Strike 2 Workshop Tools".
echo   2. In the launcher pick swbs_jailbreak and click Launch Tools.
echo   3. In the Asset Browser type swbs in the search box, select everything it lists,
echo      right click - Recompile (or Full Recompile).
echo   4. Run compile_pack.bat again: it says SUCCESS once the compiled files are there.
goto :end

:not_extracted
echo PROBLEM: find_cs2.ps1 is not next to compile_pack.bat.
if not "%PACK:\Temp\=%"=="%PACK%" echo It looks like compile_pack.bat was opened from INSIDE the zip (Windows unpacks only that file).
echo Right click the zip - Extract All - Extract, then open the extracted folder and run compile_pack.bat there.
echo The folder you run it from must contain: compile_pack.bat, find_cs2.ps1 and the swbs_jailbreak folder.
goto :end

:no_source
echo PROBLEM: the swbs_jailbreak folder is not next to compile_pack.bat and not in
echo   %DEST%
echo These are the files next to compile_pack.bat:
dir /b "%PACK%"
echo.
echo Extract the WHOLE zip again (right click the zip - Extract All) and run compile_pack.bat from the extracted folder.
goto :end

:no_cs2
echo PROBLEM: Counter-Strike 2 was not found in your Steam libraries.
echo Drag your "Counter-Strike Global Offensive" folder onto compile_pack.bat, or run in a command prompt:
echo   compile_pack.bat "D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"
goto :end

:copy_failed
echo PROBLEM: could not copy the files into %DEST%
echo Close the Workshop Tools / CS2 and try again, or copy the folder contents by hand (README step 3).
goto :end

:compile_failed
echo PROBLEM: compiling did not produce the sound events.
if exist "%LOG%" echo The compiler said:
if exist "%LOG%" echo --------------------------------------------
if exist "%LOG%" type "%LOG%"
if exist "%LOG%" echo --------------------------------------------
echo Send compile_log.txt (next to this file) if you need help. You can also compile in the Workshop Tools:
echo Asset Browser - search swbs - select everything - right click - Recompile, then run this again.
goto :end

:end
echo.
echo Press any key to close this window.
pause >nul
"""

FIND_CS2_PS1 = r"""# Used by compile_pack.bat. Prints "CS2=<Counter-Strike Global Offensive folder>" and, when the Workshop Tools'
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
"""

README = r"""SWBS Jailbreak workshop pack
============================

What is in it
-------------
Sounds from the original SWBS jailbreak (SMPlugins fork), one sound event each:

  swbs.guards_win      guards win the round          -> Settings.GuardsWinSound
  swbs.prisoners_win   prisoners win the round       -> Settings.PrisonersWinSound
  swbs.lr_activated    last request activated        -> LastRequest.ActivatedSound
  swbs.freekill        !freekill report              -> Commands.FreekillSound
  swbs.chat_alert      guard message (!ctmessage)    -> Commands.GuardMessageSound
  swbs.cowbell         Horror Movie moving prisoner  -> DaysLarge.HorrorMoveSound, store kill sound "Cowbell"
  swbs.rpg_shoot       RPG / Rocketeer rocket        -> Rpg.ShootSound
  swbs.rpg_explode     rocket explosion (4 random)   -> Rpg.ExplosionSound
  swbs.rpg_pain        hurt by a rocket (2 random)   -> Rpg.PainSound
  swbs.rpg_death       killed by a rocket            -> Rpg.DeathSound
  swbs.jetpack         flying with a jetpack         -> GameDays.JetpackSound
  swbs.slap            admin slap, last guard slap   -> Admin.SlapSound, LastGuard.SlapSound
  swbs.teleport        Teleport freeday, Teleporter  -> DaysNew.TeleportSound, LastRequestsExtra.TeleporterSound

You do not type these anywhere: turning WorkshopPack.Enabled on (step 6) sets all of them once.

Models (the original SWBS props, converted to CS2: SMD mesh + PNG textures + .vmat + .vmdl):

  models/swbs/flag.vmdl            Capture the Flag flag (tinted in the team colour)
  models/swbs/capture_point.vmdl   Capture the Flag capture point
  models/swbs/finish_line.vmdl     Race LR and Speedrun finish line
  models/swbs/missile.vmdl         RPG day / Rocketeer rockets
  models/swbs/rocket_launcher.vmdl converted for later (CS2 cannot swap the knife's first-person model)

With WorkshopPack.Enabled and WorkshopPack.UseModels (default true) the plugin uses them instead of the beam versions.
compile_pack.bat reports how many of the 5 models compiled; if it is fewer, send compile_log.txt and set
"UseModels": false meanwhile (a missing model would show as the engine's error model).

Not included: the SpeedRuns announcer lines (clips from other games, which get Workshop items taken down), the map vote
and Blocks sounds (plugins you do not run). The guard and prisoner player models are in source1_models_to_convert
(see CONVERT_MODELS.txt): they need re-rigging onto the CS2 player skeleton by hand.

The SMPlugins fork has no license file. Keep the Workshop item Unlisted and replace anything you are unsure about over time.

Steps (Windows)
---------------
1. Install the tools: Steam > Counter-Strike 2 > right click > Properties > DLC > tick "Counter-Strike 2 Workshop Tools".

2. Create the addon: start CS2 Workshop Tools (Steam > CS2 > Play > "Launch CS2 Workshop Tools"), choose
   "Create New Addon", name it exactly   swbs_jailbreak   and close the tools again.
   This makes  ...\Counter-Strike Global Offensive\content\csgo_addons\swbs_jailbreak\
   and         ...\Counter-Strike Global Offensive\game\csgo_addons\swbs_jailbreak\

3. Extract the whole zip, then double click compile_pack.bat. It finds CS2 in any Steam library, copies the pack into
   content\csgo_addons\swbs_jailbreak, compiles it and tells you SUCCESS (with the number of sounds) or what is wrong.
   If it cannot find CS2, drag your "Counter-Strike Global Offensive" folder onto compile_pack.bat.
   (By hand instead: copy the contents of this pack's swbs_jailbreak folder into
   ...\Counter-Strike Global Offensive\content\csgo_addons\swbs_jailbreak\ and in the Workshop Tools Asset Browser
   select the addon's files > right click > Recompile.)
   The compiled .vsnd_c / .vsndevts_c files appear under ...\game\csgo_addons\swbs_jailbreak\

   Quick check before uploading: in the Workshop Tools, open the Asset Browser, find swbs_jailbreak.vsndevts,
   double click a sound event and press play.

4. Upload: in the Workshop Tools open the Workshop Manager (Tools menu / the Steam Workshop button), "New", pick the
   swbs_jailbreak addon, give it a title, use this pack's preview.png, publish.
   Then on the Steam Workshop page of the item set Visibility to "Unlisted" (players must be able to download it;
   "Hidden" items do not download). Note the item id (the number in the page URL ...?id=1234567890).

5. Server: install the Metamod plugin MultiAddonManager (github.com/Source2ZE/MultiAddonManager) and in
   game/csgo/cfg/multiaddonmanager/multiaddonmanager.cfg set
       mm_extra_addons "1234567890"
   (your id; several ids are comma separated). Players download the pack when they join.

6. Plugin: in addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json set
       "WorkshopPack": { "Enabled": true, "WorkshopId": "1234567890", ... }
   and restart the server (or change map). On the first start the plugin precaches soundevents/swbs_jailbreak.vsndevts
   and points every sound setting listed above at the pack. You can still change any of them; the console command
   css_workshoppack (root admins) points them at the pack again.

Updating the pack later: change files in content\csgo_addons\swbs_jailbreak, compile, Workshop Manager > your item >
Update. MultiAddonManager makes players download the new version.
"""

CONVERT_MODELS = r"""CS:GO player models from the SMPlugins fork (guard + 3 prisoners)
====================================================================
The props (flag, capture point, finish line, missile, rocket launcher) are already converted and inside the addon.
These player models are not: CS2 player models use a different skeleton, so each one has to be re-rigged by hand:

  1. Blender + the SourceIO add-on: File > Import > Source Engine > MDL (keep this folder structure for the textures).
  2. Export a CS2 agent with Source 2 Viewer as the target skeleton, move the mesh onto it and redo the weight painting.
  3. ModelDoc: build the model on the CS2 base player model (hitboxes, animations), assign materials, compile.
  4. Set PlayerModels.GuardModel / PlayerModels.PrisonerModels to the new .vmdl paths and add them to
     WorkshopPack.ExtraResources. Test on an empty server before PlayerModels.ApplyModels = true; a wrong rig turns
     players into boxes.
"""


def preview(path: Path) -> None:
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        return
    img = Image.new("RGB", (512, 512), (24, 26, 32))
    d = ImageDraw.Draw(img)
    for y in range(0, 512, 32):  # cell bars
        d.rectangle([0, y, 511, y + 6], fill=(46, 50, 60))
    d.rectangle([40, 150, 472, 362], fill=(230, 120, 30))
    try:
        big = ImageFont.truetype("DejaVuSans-Bold.ttf", 64)
        small = ImageFont.truetype("DejaVuSans-Bold.ttf", 30)
    except OSError:
        big = small = ImageFont.load_default()
    d.text((256, 220), "SWBS", font=big, fill=(20, 20, 24), anchor="mm")
    d.text((256, 300), "JAILBREAK PACK", font=small, fill=(20, 20, 24), anchor="mm")
    img.save(path)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    fork = Path(sys.argv[1]).resolve()
    out = Path(sys.argv[2]).resolve()
    pack = out / "SwbsJailbreak-workshop-pack"
    if pack.exists():
        shutil.rmtree(pack)
    addon = pack / ADDON
    events = {}
    for name, (volume, sources, folder) in SOUNDS.items():
        refs = []
        for rel in sources:
            src = fork / rel
            if not src.is_file():
                print(f"missing: {rel}", file=sys.stderr)
                return 1
            stem = src.stem
            dst = addon / "sounds" / "swbs" / folder / f"{stem}.wav"
            convert(src, dst)
            refs.append(f"sounds/swbs/{folder}/{stem}.vsnd")
        events[name] = (volume, refs)
    ev = addon / EVENTS_FILE
    ev.parent.mkdir(parents=True, exist_ok=True)
    ev.write_text(soundevents(events), encoding="utf-8")

    print("models:")
    models = props.convert_all(fork, addon)

    staged = pack / "source1_models_to_convert"
    for rel in SOURCE1_MODELS + SOURCE1_MATERIALS:
        src = fork / rel
        if not src.exists():
            continue
        for f in src.rglob("*"):
            if f.is_file() and f.suffix.lower() != ".bz2":
                files_root = next(p for p in f.parents if p.name == "files")
                dst = staged / f.relative_to(files_root)
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(f, dst)
    (staged / "CONVERT_MODELS.txt").write_text(CONVERT_MODELS.replace("\n", "\r\n"), encoding="utf-8")

    (pack / "compile_pack.bat").write_text(COMPILE_BAT.replace("\n", "\r\n"), encoding="ascii")
    (pack / "find_cs2.ps1").write_text(FIND_CS2_PS1.replace("\n", "\r\n"), encoding="utf-8")
    (pack / "README.txt").write_text(README.replace("\n", "\r\n"), encoding="utf-8")
    preview(pack / "preview.png")

    zip_path = out / "SwbsJailbreak-workshop-pack.zip"
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(pack.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(out))
    print(f"pack: {zip_path} ({len(events)} sound events, {len(models)} models)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
