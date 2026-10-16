"""
Builds SWBS store pack 2 (addon swbs_store2, its own Workshop item): the store's Face and Back equipment
(equipment.py) and, optionally, more player models already rebuilt on the CS2 agent skeleton
(tools/prisoner-skeleton/retarget.py with model folder characters/models/swbs/store2).

usage: python build_pack.py <output folder> [--models <addon content folder holding characters/models/swbs/store2>]

Output: <output>/SwbsJailbreak-store-pack-2/ (swbs_store2 addon, compile_pack.bat, find_cs2.ps1, README.txt) and
<output>/SwbsJailbreak-store-pack-2.zip.
"""
import argparse
import shutil
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import equipment  # noqa: E402

ADDON = 'swbs_store2'
PLAYER_ROOT = 'characters/models/swbs/store2'

README = r"""SWBS store pack 2
=================

Brings back the SMPlugins store's equipment slots that the CS2 port did not have yet ("Item_Equipment" face and torso):

  Face (!store > Face)                          Back (!store > Back)
    models/swbs/store2/face/sunglasses             models/swbs/store2/back/angel_wings
    models/swbs/store2/face/round_glasses          models/swbs/store2/back/bat_wings     (sold as Devil wings)
    models/swbs/store2/face/bandit_mask            models/swbs/store2/back/jetpack
    models/swbs/store2/face/mustache               models/swbs/store2/back/backpack
    models/swbs/store2/face/clown_nose             models/swbs/store2/back/katana
    models/swbs/store2/face/eyepatch

They share one white material; the plugin tints each store item, so one model can be sold in several colours by adding
store items (copy an item in the Store config, give it a new Id, Name and Color).
@PLAYERS@
Install (Windows)
-----------------
1. Extract the whole zip, then double click compile_pack.bat. It finds CS2, copies swbs_store2 into
   ...\Counter-Strike Global Offensive\content\csgo_addons\swbs_store2\ and compiles it.
   (Or copy the swbs_store2 folder there yourself and compile in the Workshop Tools: Asset Browser, search swbs,
   select everything, right click - Recompile.)
2. Workshop Tools - pick swbs_store2 - Workshop Manager - upload it as a NEW item (keep it separate from the main pack
   and from the first store pack).
3. Server: add its id to MultiAddonManager after the other two packs:
     mm_extra_addons "<main pack id>,<store pack id>,<store pack 2 id>"
4. Plugin config, section WorkshopPack: "Store2Equipment": true (and "Store2WorkshopId" for your reference). Reload the
   plugin. The Face and Back categories appear in !store.

Tuning (section Store)
----------------------
  FaceAttachment "clip_limit", FaceForward 4.5, FaceHeight 0    glasses and masks follow the head
  BackAttachment "c4", BackBelowEye 17, BackDepth 6               wings and packs follow the upper spine
If glasses sink into a model's face raise FaceForward; if a back item floats behind the player lower BackDepth.
Equipment is hidden from its wearer, honours !store > Effect visibility and is off on wardays and in non-freeday LRs.
"""

PLAYERS = r"""
Player models
-------------
  @LIST@
Built on CS2's diver agent skeleton by tools/prisoner-skeleton/retarget.py. Add each one to the Store config as an item
of Type "PlayerModel" with the path above as Data.
"""


def _zip(folder: Path, zip_path: Path) -> None:
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as z:
        for f in sorted(folder.rglob('*')):
            if f.is_file():
                z.write(f, f.relative_to(folder.parent))


def compile_bat(pack_dir: str, models, players: bool) -> str:
    steps = [('materials', '-r -i "%DEST%\\materials\\swbs\\store2\\*.vmat"'),
             ('equipment', '-r -i "%DEST%\\models\\swbs\\store2\\*.vmdl"')]
    if players:
        steps += [('player materials', '-r -i "%DEST%\\characters\\models\\swbs\\store2\\*.vmat"'),
                  ('player models', '-r -i "%DEST%\\characters\\models\\swbs\\store2\\*.vmdl"')]
    text = (HERE / 'compile_pack.bat.in').read_text()
    for key, value in (
            ('@TITLE@', 'SWBS store pack 2'), ('@ADDON@', ADDON), ('@PACKDIR@', pack_dir),
            ('@MARKER@', equipment.VMAT.replace('/', '\\')), ('@REQUIRED@', equipment.VMAT.replace('/', '\\') + '_c'),
            ('@COMPILE@', ''.join(f'echo === {label} >> "%LOG%"\n"%RC%" {args} >> "%LOG%" 2>&1\n' for label, args in steps).rstrip('\n')),
            ('@MODELS@', ' '.join(m[:-len('.vmdl')].replace('/', '\\') for m in models)), ('@NMODELS@', str(len(models))),
            ('@MISSING_HINT@', 'Keep WorkshopPack.Store2Equipment off until every model compiles.'),
            ('%N% sounds and ', '')):
        text = text.replace(key, value)
    return text


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('out')
    ap.add_argument('--models', help=f'addon content folder holding {PLAYER_ROOT}/<name>/ (retarget.py output)')
    a = ap.parse_args()
    out = Path(a.out).resolve()
    pack = out / 'SwbsJailbreak-store-pack-2'
    if pack.exists():
        shutil.rmtree(pack)
    addon = pack / ADDON

    sys.argv = ['equipment.py', str(addon)]
    equipment.main()
    models = [f'{equipment.MODEL_ROOT}/{slot}/{name}.vmdl' for slot, name, _ in equipment.all_models()]

    players = []
    if a.models:
        src = Path(a.models).resolve() / PLAYER_ROOT
        for d in sorted(p for p in src.iterdir() if p.is_dir()) if src.is_dir() else []:
            if (d / f'{d.name}.vmdl').is_file():
                shutil.copytree(d, addon / PLAYER_ROOT / d.name)
                players.append(f'{PLAYER_ROOT}/{d.name}/{d.name}.vmdl')
        if (src / 'shared').is_dir():
            shutil.copytree(src / 'shared', addon / PLAYER_ROOT / 'shared')
        if not players:
            print(f'no player models in {src}', file=sys.stderr)
            return 1

    readme = README.replace('@PLAYERS@', PLAYERS.replace('@LIST@', '\n  '.join(players)) if players else '')
    (pack / 'README.txt').write_text(readme.replace('\n', '\r\n'), encoding='utf-8')
    (pack / 'compile_pack.bat').write_text(compile_bat(pack.name, models + players, bool(players)).replace('\n', '\r\n'), encoding='ascii')
    (pack / 'find_cs2.ps1').write_text((HERE / 'find_cs2.ps1').read_text().replace('\n', '\r\n'), encoding='utf-8')
    zip_path = out / 'SwbsJailbreak-store-pack-2.zip'
    _zip(pack, zip_path)
    print(f'store pack 2: {zip_path} ({len(models)} equipment models, {len(players)} player models)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
