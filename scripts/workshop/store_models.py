"""
Rebuilds downloaded CS2 player models (compiled .vmdl_c, e.g. from a "player model" zip) for CS2's current player skeleton
so they can be sold in the store, optionally recoloured:

    python store_models.py --source <folder with characters/...> [--source ...] --out <addon content folder>

Each model is exported with s2tool (ValveResourceFormat, scripts/workshop/s2tool) to glTF, moved onto the CS2 skeleton
by player_model.py and written as SMD + .vmdl (characters/models/swbs/store/<name>/<name>.vmdl). Its materials are
decompiled to .vmat + PNG next to it. Models that already use the current skeleton come through unchanged; models built
before CS2's 2025 animation update (old "_L / _R" skeleton) get their mesh moved onto the new one.

Character models (Deadpool, the Flash, ...) belong to their publishers and to the modders who made them: keep them in a
separate Workshop item from the main pack, credit the authors, and expect a takedown if the rights holder asks.
"""
import argparse
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import player_model

HERE = Path(__file__).resolve().parent
OUT_ROOT = "characters/models/swbs/store"

# Textures above this size are scaled down: smaller downloads, and a player model needs no more.
MAX_TEXTURE = 2048

# name -> settings. model: compiled model path inside a --source folder; skip_meshes: glTF mesh names to leave out;
# recolor: {material name: recolour function name}; credit: shown in the README.
STORE_MODELS = {
    "deadpool_xforce": {
        "model": "characters/models/kolka/deadpool/deadpool.vmdl_c",
        "skip_meshes": ["first_person_arms"],
        "recolor": {"body": "xforce_body", "head": "xforce"},
        "credit": "Deadpool player model by kolka, X-Force grey/black recolour by SWBS",
    },
    "flash": {
        "model": "characters/models/nozb1/theflash_player_model/theflash_player_model.vmdl_c",
        "skip_meshes": [],
        "recolor": {},
        "credit": "The Flash player model by nozb1 (steamcommunity.com/id/nozb1)",
    },
}


def vmdl_path(name: str) -> str:
    return f"{OUT_ROOT}/{name}/{name}.vmdl"


# ---------------------------------------------------------------- recolours

def _recolor_xforce(src: Path, dst: Path, keep_logo=None):
    """Deadpool red -> X-Force charcoal grey (shading kept), brown leather -> near black; black parts stay black.
    keep_logo = (x, y, radius) as fractions of the texture: a circle left untouched (the red belt buckle logo)."""
    import numpy as np
    from PIL import Image

    a = np.asarray(Image.open(src).convert("RGBA")).astype(np.float32) / 255.0
    rgb, alpha = a[..., :3], a[..., 3:]
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx, mn = rgb.max(-1), rgb.min(-1)
    s = np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0)
    d = np.maximum(mx - mn, 1e-6)
    h = np.where(mx == r, ((g - b) / d) % 6, np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) / 6.0
    hd = np.minimum(h, 1 - h)  # hue distance from red
    sat = np.clip((s - 0.25) / 0.2, 0, 1)
    red = np.clip((0.05 - hd) / 0.03, 0, 1) * sat
    brown = np.clip((hd - 0.03) / 0.02, 0, 1) * np.clip((0.14 - hd) / 0.03, 0, 1) * sat * (h < 0.5)
    if keep_logo:
        cx, cy, rad = keep_logo
        hh, ww = mx.shape
        yy, xx = np.mgrid[0:hh, 0:ww]
        red = np.where(((xx - cx * ww) ** 2 + (yy - cy * hh) ** 2) < (rad * ww) ** 2, 0, red)
    reds = mx[red > 0.5]
    shade = mx / max(float(np.percentile(reds, 60)) if reds.size else 0.55, 1e-3)
    grey = np.clip(0.25 * shade, 0, 1)[..., None] * np.array([0.95, 0.98, 1.04], np.float32)
    lum = 0.2126 * r + 0.7152 * g + 0.0722 * b
    dark = np.clip(lum * 0.55 + 0.04, 0, 1)[..., None] * np.array([1.0, 1.0, 1.03], np.float32)
    out = rgb * (1 - red[..., None]) + grey * red[..., None]
    out = out * (1 - brown[..., None]) + dark * brown[..., None]
    res = np.concatenate([np.clip(out, 0, 1), alpha], -1)
    Image.fromarray((res * 255 + 0.5).astype(np.uint8), "RGBA").save(dst)


RECOLORS = {
    "xforce": lambda s, d: _recolor_xforce(s, d),
    "xforce_body": lambda s, d: _recolor_xforce(s, d, keep_logo=(0.634, 0.5045, 0.036)),
}


# ---------------------------------------------------------------- s2tool

def s2tool_command(tool: str = None):
    """Command line for s2tool: a built s2tool.dll, or `dotnet run` on the project."""
    if tool:
        return ["dotnet", tool] if tool.endswith(".dll") else [tool]
    built = sorted((HERE / "s2tool" / "bin").glob("**/s2tool.dll"))
    if built:
        return ["dotnet", str(built[-1])]
    return ["dotnet", "run", "-c", "Release", "--project", str(HERE / "s2tool"), "--"]


def _run(cmd, *args):
    out = subprocess.run(cmd + [str(a) for a in args], capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(f"s2tool {args[0]} failed:\n{out.stdout}\n{out.stderr}")
    return out.stdout


# ---------------------------------------------------------------- conversion

def _find_source(sources, rel: str):
    for s in sources:
        if (s / rel).is_file():
            return s
    return None


def _limit_size(png: Path) -> None:
    from PIL import Image

    with Image.open(png) as im:
        if max(im.size) <= MAX_TEXTURE:
            return
        scale = MAX_TEXTURE / max(im.size)
        small = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), Image.LANCZOS)
    small.save(png)


def _material(tool, root: Path, vmat_rel: str, out_dir: str, addon: Path, recolor, tmp: Path) -> str:
    """Decompiles a .vmat_c into <out_dir>/<name>.vmat with its textures as PNGs; returns the new .vmat path."""
    name = Path(vmat_rel).stem
    work = tmp / f"mat_{name}"
    _run(tool, "extract", root, vmat_rel + "_c", work)
    text = (work / vmat_rel).read_text(errors="ignore")
    text = re.sub(r'\n\t"Compiled Textures"\s*\{[^}]*\}', "", text)
    (addon / out_dir).mkdir(parents=True, exist_ok=True)

    def texture(m):
        key, path = m.group(1), m.group(2)
        png = work / Path(path).name
        if not png.is_file():
            return m.group(0)  # a stock game texture (materials/default/...): keep the reference
        new_name = re.sub(r"[^a-z0-9_.]", "_", png.name.lower())
        dst = addon / out_dir / new_name
        if key == "TextureColor" and recolor:
            RECOLORS[recolor](png, dst)
        else:
            shutil.copyfile(png, dst)
        _limit_size(dst)
        return f'"{key}"\t"{out_dir}/{new_name}"'

    text = re.sub(r'"(Texture\w+)"\s+"([^"\[][^"]*)"', texture, text)
    out_rel = f"{out_dir}/{name}.vmat"
    (addon / out_rel).write_text("// SWBS Jailbreak store model - rebuilt by scripts/workshop/store_models.py\n" + text,
                                 encoding="utf-8")
    return out_rel


def convert(name: str, spec: dict, sources, addon: Path, tool, log=print) -> str:
    root = _find_source(sources, spec["model"])
    if root is None:
        raise FileNotFoundError(f"{name}: {spec['model']} is in none of the --source folders")
    target = player_model.load_target()
    out_dir = f"{OUT_ROOT}/{name}"
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        refs = [l[4:] for l in _run(tool, "info", root, spec["model"]).splitlines() if l.startswith("ref ")]
        old_skeleton = not any(r.endswith(".vnmgraph") for r in refs)
        _run(tool, "glb", root, spec["model"], tmp / "model.glb")
        src = player_model.from_glb(tmp / "model.glb", skip_meshes=spec.get("skip_meshes", []))
        vertices, weights = player_model.retarget(src, target)

        vmats = {Path(r).stem: r for r in refs if r.endswith(".vmat") and (root / (r + "_c")).is_file()}
        remaps, smd_names = [], {}

        def smd_material(mat: str):
            if mat not in vmats:
                return f"swbs_{name}_{mat}"
            smd_names[mat] = f"swbs_{name}_{mat}"
            return smd_names[mat]

        smd_rel = f"{out_dir}/{name}.smd"
        used = player_model.write_skinned_smd(target, vertices, weights, src.meshes, addon / smd_rel, smd_material)
        for mat, smd in smd_names.items():
            vmat = _material(tool, root, vmats[mat], f"{out_dir}/materials", addon, spec.get("recolor", {}).get(mat), tmp)
            remaps += [(smd, vmat), (smd + ".vmat", vmat)]
    bind_rel = f"{OUT_ROOT}/cs2_bind_pose.smd"
    player_model.write_bind_pose_smd(target, addon / bind_rel)
    vmdl_rel = vmdl_path(name)
    (addon / vmdl_rel).write_text(player_model.build_vmdl(smd_rel, bind_rel, remaps), encoding="ascii")
    log(f"  {vmdl_rel}: {sum(used.values())} triangles, {len(remaps) // 2} materials"
        f"{', moved from the pre-2025 skeleton' if old_skeleton else ''}")
    return vmdl_rel


def convert_all(sources, addon: Path, tool=None, names=None, log=print) -> list:
    cmd = s2tool_command(tool)
    made = []
    for name, spec in STORE_MODELS.items():
        if names and name not in names:
            continue
        if _find_source(sources, spec["model"]) is None:
            log(f"  {name}: skipped ({spec['model']} not found)")
            continue
        made.append(convert(name, spec, sources, addon, cmd, log))
    return made


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", action="append", required=True, help="folder holding characters/... (repeatable)")
    ap.add_argument("--out", required=True, help="addon content folder to write into")
    ap.add_argument("--tool", help="s2tool.dll (default: build or run scripts/workshop/s2tool)")
    ap.add_argument("names", nargs="*", help=f"models to build (default all: {', '.join(STORE_MODELS)})")
    a = ap.parse_args()
    made = convert_all([Path(s) for s in a.source], Path(a.out), a.tool, a.names)
    print(f"{len(made)} store model(s) written")
    return 0 if made else 1


if __name__ == "__main__":
    sys.exit(main())
