#!/usr/bin/env python3
"""
Builds the SWBS Jailbreak workshop pack from the SMPlugins fork (hlstriker/Swoobles assets).

Output (a zip and the folder it was made from):
  swbs_jailbreak/                     -> copy into  ...\\Counter-Strike Global Offensive\\content\\csgo_addons\\swbs_jailbreak\\
      sounds/swbs/...                    the original sounds, converted to 44.1 kHz 16-bit mono WAV (CS2's compiler input)
      soundevents/swbs_jailbreak.vsndevts   one sound event per sound (names used by the plugin's WorkshopPack module)
  compile_pack.bat                    compiles the addon with CS2's resourcecompiler (or use the Asset Browser)
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

# Source 1 (CS:GO) assets staged for manual conversion: model folders and the material folders they use.
SOURCE1_MODELS = [
    "Mods/UltimateJailbreak/files/models/swoobles/ultimate_jailbreak",
    "Mods/UltimateJailbreak/files/models/swoobles/rocket_jumping",
    "Mods/UltimateJailbreak/files/models/weapons/rocket_launcher",
    "Mods/UltimateJailbreak/files/models/player/custom_player/swoobles",
]
SOURCE1_MATERIALS = [
    "Mods/UltimateJailbreak/files/materials",
    "Plugins/CustomWeapons/files/materials",
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
rem Compiles the swbs_jailbreak addon with CS2's resource compiler.
rem Usage: compile_pack.bat ["path to Counter-Strike Global Offensive"]
setlocal
set "CS2=%~1"
if "%CS2%"=="" set "CS2=C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
set "RC=%CS2%\game\bin\win64\resourcecompiler.exe"
set "ADDON=%CS2%\content\csgo_addons\swbs_jailbreak"
if not exist "%RC%" (
  echo resourcecompiler.exe not found at "%RC%".
  echo Install "Counter-Strike 2 Workshop Tools" ^(Steam - CS2 - Properties - DLC^) or pass your CS2 folder as the first argument.
  pause
  exit /b 1
)
if not exist "%ADDON%\soundevents\swbs_jailbreak.vsndevts" (
  echo Copy the swbs_jailbreak folder from the pack into "%CS2%\content\csgo_addons\" first.
  pause
  exit /b 1
)
"%RC%" -r -i "%ADDON%\*"
echo.
echo Done. Compiled files are in "%CS2%\game\csgo_addons\swbs_jailbreak". Next: upload with the Workshop Manager (README step 4).
pause
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

Not included: the SpeedRuns announcer lines (clips from other games, which get Workshop items taken down), the map vote
and Blocks sounds (plugins you do not run). The CS:GO models are in source1_models_to_convert (see CONVERT_MODELS.txt):
CS2 cannot load CS:GO models, they must be rebuilt in ModelDoc first.

The SMPlugins fork has no license file. Keep the Workshop item Unlisted and replace anything you are unsure about over time.

Steps (Windows)
---------------
1. Install the tools: Steam > Counter-Strike 2 > right click > Properties > DLC > tick "Counter-Strike 2 Workshop Tools".

2. Create the addon: start CS2 Workshop Tools (Steam > CS2 > Play > "Launch CS2 Workshop Tools"), choose
   "Create New Addon", name it exactly   swbs_jailbreak   and close the tools again.
   This makes  ...\Counter-Strike Global Offensive\content\csgo_addons\swbs_jailbreak\
   and         ...\Counter-Strike Global Offensive\game\csgo_addons\swbs_jailbreak\

3. Copy everything inside this pack's  swbs_jailbreak  folder (sounds, soundevents) into
   ...\Counter-Strike Global Offensive\content\csgo_addons\swbs_jailbreak\
   Then compile it, either way:
   a) double click compile_pack.bat (pass your CS2 folder as an argument if it is not in the default Steam library), or
   b) open the Workshop Tools with the swbs_jailbreak addon, Asset Browser, select the addon's files, right click > Recompile.
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

CONVERT_MODELS = r"""CS:GO models from the SMPlugins fork (Source 1 .mdl/.vvd/.vtx/.phy with .vmt/.vtf materials)
==========================================================================================
CS2 cannot load these files. Each must be rebuilt as a CS2 model before it can go in the pack:

  1. Crowbar (steamcommunity.com/groups/CrowbarTool): Decompile the .mdl -> .qc + .smd files.
  2. VTFEdit: open each .vtf under materials\ and export it as .tga.
  3. Copy the .smd and .tga files into content\csgo_addons\swbs_jailbreak\models\swbs\... and materials\swbs\...
  4. Material Editor (Workshop Tools): one material per texture.
  5. ModelDoc: New model > add the .smd mesh, assign the materials, add physics where it needs collision, compile.

What each one is for in the plugin:
  swoobles/ultimate_jailbreak/flag            Capture the Flag flag (currently beams)
  swoobles/ultimate_jailbreak/capture_point   Capture the Flag capture point
  swoobles/ultimate_jailbreak/finish_line     Race LR / Speedrun finish line
  weapons/rocket_launcher + swoobles/rocket_jumping/missile   RPG launcher and rocket (currently beam trails)
  player/custom_player/swoobles/guard + prisoners/*   guard and prisoner player models (PlayerModels.GuardModel,
      PlayerModels.PrisonerModels) - these must also be re-rigged onto the CS2 player skeleton (export a CS2 agent with
      Source 2 Viewer, move the mesh onto it in Blender, build on the CS2 base player model in ModelDoc). Without that,
      players turn into boxes. Test on an empty server before setting PlayerModels.ApplyModels to true.

Add every compiled model path to WorkshopPack.ExtraResources so it is precached, then set the plugin setting.
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

    (pack / "compile_pack.bat").write_text(COMPILE_BAT.replace("\n", "\r\n"), encoding="utf-8")
    (pack / "README.txt").write_text(README.replace("\n", "\r\n"), encoding="utf-8")
    preview(pack / "preview.png")

    zip_path = out / "SwbsJailbreak-workshop-pack.zip"
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(pack.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(out))
    print(f"pack: {zip_path} ({len(events)} sound events)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
