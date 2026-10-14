#!/usr/bin/env python3
"""
Builds the SWBS Jailbreak workshop pack from the SMPlugins fork (hlstriker/Swoobles assets).

Output (a zip and the folder it was made from):
  swbs_jailbreak/                     -> copy into  ...\\Counter-Strike Global Offensive\\content\\csgo_addons\\swbs_jailbreak\\
      sounds/swbs/...                    the original sounds, converted to 44.1 kHz 16-bit mono WAV (CS2's compiler input)
      soundevents/swbs_jailbreak.vsndevts   one sound event per sound (names used by the plugin's WorkshopPack module)
      models/swbs/...                    the SWBS props (props.py), paintball splats (paintballs.py), trail pieces (trails.py)
      characters/models/swbs/prisoners/  the SWBS prisoner player models on the CS2 skeleton (prisoners.py)
  compile_pack.bat + find_cs2.ps1     finds CS2, copies the addon in, compiles and checks it (or use the Asset Browser)
  preview.png                         512x512 Workshop preview image
  README.txt                          step by step: compile, upload, MultiAddonManager, plugin config

With --store-source (folders holding downloaded CS2 player models, see store_models.py) a second pack is built:
SwbsJailbreak-store-pack (addon swbs_store) with the store's character models, for a separate Workshop item.

Usage: build_pack.py <smplugins checkout> <output dir> [--store-source DIR ...] [--s2tool s2tool.dll]
Needs ffmpeg on PATH, srctools, Pillow; numpy and .NET (s2tool) for the store pack.
"""
import argparse
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import kotor_models  # noqa: E402  (KOTOR characters from the user's own game files)
import paintballs  # noqa: E402  (paintball splats)
import prisoners  # noqa: E402  (CS:GO prisoner models -> CS2 player models)
import props  # noqa: E402  (CS:GO props -> CS2 models)
import store_models  # noqa: E402  (downloaded CS2 player models -> current CS2 skeleton)
import trails  # noqa: E402  (SWBS trail pieces)

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
title @TITLE@ - compile
rem Finds CS2 (any Steam library) and its resource compiler, copies the pack into content\csgo_addons\@ADDON@,
rem compiles it and checks the result. Optional: drag your "Counter-Strike Global Offensive" folder onto this file.
set "PACK=%~dp0"
set "LOG=%PACK%compile_log.txt"
echo ============================================
echo  @TITLE@ - compile
echo ============================================
echo Running from: %PACK%
echo.
if not exist "%PACK%find_cs2.ps1" goto :not_extracted

echo Looking for Counter-Strike 2 and the Workshop Tools compiler (can take a few seconds)...
set "CS2="
set "RC="
for /f "usebackq tokens=1,* delims==" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PACK%find_cs2.ps1" "%~1"`) do set "%%A=%%B"
if "%CS2%"=="" goto :no_cs2
set "DEST=%CS2%\content\csgo_addons\@ADDON@"
set "OUT=%CS2%\game\csgo_addons\@ADDON@"
echo   CS2 folder: %CS2%
if not "%RC%"=="" echo   Compiler:   %RC%
if "%RC%"=="" echo   Compiler:   not found in this CS2 folder
echo.

rem The pack's @ADDON@ folder: next to this file, one folder down (zip extracted into a folder of the same
rem name), or already copied into CS2's content folder by hand.
set "SRC="
if exist "%PACK%@ADDON@\@MARKER@" set "SRC=%PACK%@ADDON@"
if "%SRC%"=="" if exist "%PACK%@PACKDIR@\@ADDON@\@MARKER@" set "SRC=%PACK%@PACKDIR@\@ADDON@"
if "%SRC%"=="" if exist "%DEST%\@MARKER@" goto :already_copied
if "%SRC%"=="" goto :no_source

echo [1/3] Copying the pack into
echo       %DEST%
robocopy "%SRC%" "%DEST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :copy_failed
goto :compile

:already_copied
echo [1/3] The pack is already in
echo       %DEST%
echo       (no @ADDON@ folder next to this file, so using those files)

:compile
if "%RC%"=="" goto :no_compiler
rem Only the pack's own files: the example maps a new addon starts with are left alone (building them needs a
rem lightmap-capable GPU and is not part of the pack).
echo [2/3] Compiling the pack (output in compile_log.txt; player models take a minute)...
> "%LOG%" echo @TITLE@ compile log
@COMPILE@
:check
echo [3/3] Checking the result...
rem Some setups write compiled files next to the sources (content); the Workshop upload takes them from game.
if not exist "%OUT%" mkdir "%OUT%"
robocopy "%DEST%" "%OUT%" *.vmdl_c *.vmat_c *.vtex_c *.vsnd_c *.vsndevts_c /S /XO /NFL /NDL /NJH /NJS /NP >nul
if not exist "%OUT%\@REQUIRED@" goto :compile_failed
set /a N=0
if exist "%OUT%\sounds" for /r "%OUT%\sounds" %%F in (*.vsnd_c) do set /a N+=1
set /a M=0
for %%G in (@MODELS@) do if exist "%OUT%\%%G.vmdl_c" (set /a M+=1) else (echo   missing model: %%G)
echo.
echo SUCCESS: %N% sounds and %M% of @NMODELS@ models are compiled in
echo   %OUT%
if %M% LSS @NMODELS@ echo NOTE: not every model compiled. The compiler's messages about them:
if %M% LSS @NMODELS@ if exist "%LOG%" findstr /i /c:"error" /c:"warning" /c:"fail" "%LOG%"
if %M% LSS @NMODELS@ echo Send compile_log.txt. @MISSING_HINT@
echo.
echo Next: start the CS2 Workshop Tools, pick the @ADDON@ addon and upload it with the
echo Workshop Manager (README.txt).
goto :end

:no_compiler
if exist "%OUT%\@REQUIRED@" goto :check
echo.
echo The files are copied. This CS2 folder has no resourcecompiler.exe, so compile inside the Workshop Tools:
echo   1. Steam - Counter-Strike 2 - Play - "Launch Counter-Strike 2 Workshop Tools".
echo   2. In the launcher pick @ADDON@ and click Launch Tools.
echo   3. In the Asset Browser type swbs in the search box, select everything it lists,
echo      right click - Recompile (or Full Recompile).
echo   4. Run compile_pack.bat again: it says SUCCESS once the compiled files are there.
goto :end

:not_extracted
echo PROBLEM: find_cs2.ps1 is not next to compile_pack.bat.
if not "%PACK:\Temp\=%"=="%PACK%" echo It looks like compile_pack.bat was opened from INSIDE the zip (Windows unpacks only that file).
echo Right click the zip - Extract All - Extract, then open the extracted folder and run compile_pack.bat there.
echo The folder you run it from must contain: compile_pack.bat, find_cs2.ps1 and the @ADDON@ folder.
goto :end

:no_source
echo PROBLEM: the @ADDON@ folder is not next to compile_pack.bat and not in
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
echo Close the Workshop Tools / CS2 and try again, or copy the folder contents by hand (README).
goto :end

:compile_failed
echo PROBLEM: compiling did not produce %OUT%\@REQUIRED@
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


def compile_bat(title: str, addon: str, pack_dir: str, marker: str, required: str, compile_steps, models,
                missing_hint: str) -> str:
    """compile_steps: [(label, resourcecompiler arguments with %DEST%)]; models: .vmdl paths relative to the addon."""
    steps = "".join(f'echo === {label} >> "%LOG%"\n"%RC%" {args} >> "%LOG%" 2>&1\n' for label, args in compile_steps)
    names = " ".join(m[:-len(".vmdl")].replace("/", "\\") for m in models)
    text = COMPILE_BAT
    for key, value in (("@TITLE@", title), ("@ADDON@", addon), ("@PACKDIR@", pack_dir), ("@MARKER@", marker),
                       ("@REQUIRED@", required), ("@COMPILE@", steps.rstrip("\n")), ("@MODELS@", names),
                       ("@NMODELS@", str(len(models))), ("@MISSING_HINT@", missing_hint)):
        text = text.replace(key, value)
    return text


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

  models/swbs/paintballs/*.vmdl    paintball splats: the 3 original SWBS splats + 5 new shapes (white, tinted in game)

With WorkshopPack.Enabled and WorkshopPack.UseModels (default true) the plugin uses them instead of the beam versions.
Paintballs (store items and the warden's) then leave a random splat, flat on the wall or floor the bullet hit.

  models/swbs/trails/*.vmdl        SWBS store trails, new designs: shoe footprints (on the floor), and behind the
                                   back: stars, hearts, skulls, lightning, snowflakes, music notes, coins, orbs,
                                   bubbles, fire, ice crystals and a galaxy. Without them trails draw as beams.
If compile_pack.bat reports missing prop models, send compile_log.txt and set "UseModels": false meanwhile (a missing
model would show as the engine's error model).

Player models (the SWBS prisoners, moved onto CS2's current player skeleton with CS2's hitboxes, ragdoll and animations;
material group "white" is the white uniform):

  characters/models/swbs/prisoners/male_black_big/male_black_big.vmdl
  characters/models/swbs/prisoners/female_asian/female_asian.vmdl
  characters/models/swbs/prisoners/male_white_hair/male_white_hair.vmdl

The SWBS guard was Valve's CS:GO SWAT model; CS2 ships its own agents, so guards keep PlayerModels.GuardModel.
Turning WorkshopPack.Enabled on (step 6) also sets PlayerModels.PrisonerModels to them; they are only applied with
PlayerModels.ApplyModels = true, so check them first (step 7).

Not included: the SpeedRuns announcer lines (clips from other games, which get Workshop items taken down), the map vote
and Blocks sounds (plugins you do not run).

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

7. Player models: in the Workshop Tools Asset Browser search "prisoners", double click a model. The Model Editor
   (ModelDoc) shows it standing; in its Animation panel pick any animation (e.g. a run) and check that arms, legs
   and head move with the body. Then on the server set "PlayerModels": { "ApplyModels": true } and change map.
   If a model looks broken, set ApplyModels back to false and send a screenshot.

Updating the pack later: change files in content\csgo_addons\swbs_jailbreak, compile, Workshop Manager > your item >
Update. MultiAddonManager makes players download the new version.
"""

STORE_README = r"""SWBS Jailbreak store pack (character player models)
==================================================
A separate Workshop item from the main pack on purpose: character models (Deadpool, the Flash, ...) are owned by
their publishers. If a rights holder has the item taken down, only these store models go, not the jailbreak pack.

Models (moved onto CS2's current player skeleton, with CS2's hitboxes, ragdoll and animations):
@MODELS@
Steps (same as the main pack, with the addon name swbs_store):
1. Workshop Tools: Create New Addon named exactly   swbs_store
2. Extract this zip, run compile_pack.bat (it reports how many models compiled).
3. Check each model in the Asset Browser / Model Editor as in the main pack's README step 7.
4. Upload it with the Workshop Manager as a NEW item, Visibility "Unlisted". Credit the model authors in the
   description (listed above).
5. Server: add its id to MultiAddonManager next to the main pack:   mm_extra_addons "<main id>,<store id>"
6. Plugin: the store already sells them (Store items of type PlayerModel, category "Player models"). Players
   equip one with !store; it replaces their model on spawn while PlayerModels.ApplyModels is on.
"""

def preview(path: Path, subtitle: str = "JAILBREAK PACK") -> None:
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
    d.text((256, 300), subtitle, font=small, fill=(20, 20, 24), anchor="mm")
    img.save(path)


def _zip(folder: Path, zip_path: Path) -> None:
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(folder.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(folder.parent))


def _tools(pack: Path, bat: str, readme: str, title: str) -> None:
    (pack / "compile_pack.bat").write_text(bat.replace("\n", "\r\n"), encoding="ascii")
    (pack / "find_cs2.ps1").write_text(FIND_CS2_PS1.replace("\n", "\r\n"), encoding="utf-8")
    (pack / "README.txt").write_text(readme.replace("\n", "\r\n"), encoding="utf-8")
    preview(pack / "preview.png", title)


def build_main(fork: Path, out: Path) -> int:
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
    splats = paintballs.convert_all(fork, addon)
    trail_pieces = trails.convert_all(addon)
    print("player models:")
    players = prisoners.convert_all(fork, addon)

    bat = compile_bat(
        "SWBS workshop pack", ADDON, pack.name, EVENTS_FILE.replace("/", "\\"), EVENTS_FILE.replace("/", "\\") + "_c",
        [("sounds", '-r -i "%DEST%\\sounds\\swbs\\*.wav"'),
         ("sound events", '-i "%DEST%\\soundevents\\swbs_jailbreak.vsndevts"'),
         ("materials", '-r -i "%DEST%\\materials\\swbs\\*.vmat"'),
         ("player materials", '-r -i "%DEST%\\characters\\models\\swbs\\*.vmat"'),
         ("models", '-r -i "%DEST%\\models\\swbs\\*.vmdl"'),
         ("player models", '-r -i "%DEST%\\characters\\models\\swbs\\*.vmdl"')],
        models + splats + trail_pieces + players,
        'Until then set "UseModels": false in WorkshopPack if a prop is missing, and keep PlayerModels.ApplyModels off.')
    _tools(pack, bat, README, "JAILBREAK PACK")
    zip_path = out / "SwbsJailbreak-workshop-pack.zip"
    _zip(pack, zip_path)
    print(f"pack: {zip_path} ({len(events)} sound events, {len(models)} models, {len(splats)} paintball splats, {len(trail_pieces)} trail pieces, "
          f"{len(players)} player models)")
    return 0


def build_store(sources, out: Path, tool=None, kotor=None) -> int:
    pack = out / "SwbsJailbreak-store-pack"
    if pack.exists():
        shutil.rmtree(pack)
    addon = pack / "swbs_store"
    print("store player models:")
    made = store_models.convert_all(sources, addon, tool) if sources else []
    credits = {store_models.vmdl_path(n): s["credit"] for n, s in store_models.STORE_MODELS.items()}
    if kotor:
        print("KOTOR player models:")
        made += kotor_models.convert_all(kotor, addon)
        credits.update({kotor_models.vmdl_path(n): s["credit"] for n, s in kotor_models.KOTOR_MODELS.items()})
    if not made:
        print("no store models found in the --store-source folders", file=sys.stderr)
        return 1
    lines = "".join(f"  {m}\n      {credits[m]}\n" for m in made)
    bind = f"{store_models.OUT_ROOT}/cs2_bind_pose.smd".replace("/", "\\")
    bat = compile_bat(
        "SWBS store pack", "swbs_store", pack.name, bind, made[0].replace("/", "\\") + "_c",
        [("materials", '-r -i "%DEST%\\characters\\models\\swbs\\*.vmat"'),
         ("player models", '-r -i "%DEST%\\characters\\models\\swbs\\*.vmdl"')],
        made, "Keep the store's player model items disabled until they compile.")
    _tools(pack, bat, STORE_README.replace("@MODELS@", lines), "STORE MODELS")
    zip_path = out / "SwbsJailbreak-store-pack.zip"
    _zip(pack, zip_path)
    print(f"store pack: {zip_path} ({len(made)} player models)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("fork", help="SMPlugins checkout")
    ap.add_argument("out", help="output folder")
    ap.add_argument("--store-source", action="append", default=[], help="folder holding characters/... (repeatable)")
    ap.add_argument("--s2tool", help="built s2tool.dll (default: scripts/workshop/s2tool)")
    ap.add_argument("--kotor-source", help="folder with KOTOR .mdl/.mdx/.tpc files exported from your game (kotor_models.py)")
    ap.add_argument("--store-only", action="store_true", help="only build the store pack")
    a = ap.parse_args()
    out = Path(a.out).resolve()
    if not a.store_only:
        rc = build_main(Path(a.fork).resolve(), out)
        if rc:
            return rc
    if a.store_source or a.kotor_source:
        return build_store([Path(s).resolve() for s in a.store_source], out, a.s2tool,
                           Path(a.kotor_source).resolve() if a.kotor_source else None)
    return 0


if __name__ == "__main__":
    sys.exit(main())
