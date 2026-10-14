"""
KOTOR 1 / 2 characters for the store pack: reads the .mdl / .mdx (kotor_mdl) and .tpc textures (kotor_tpc) exported
from your own copy of the game with Holocron Toolset, hangs capes and robes down, moves the mesh onto CS2's player
skeleton (player_model) and writes SMD + PNG + .vmat + .vmdl under characters/models/swbs/store/<name>/.

    python kotor_models.py --source <folder with the .mdl/.mdx/.tpc files> --out <addon content folder> [names]

Polish so they read like the game's characters under CS2 lighting:
  * colour textures scaled up 2x and sharpened (KOTOR's are 256-512 px);
  * KOTOR keeps an environment-map (shine) mask in the colour texture's alpha, not transparency: where a texture's
    TXI names an envmap, that mask becomes CS2 metalness + roughness maps, so the same metal parts shine;
  * a normal map is generated from the texture's shading, so folds, plates and scars catch the light;
  * optional recolours (the red Sith trooper is the silver armour with red plates, like KOTOR's red troopers);
  * troopers: the armour plates get a glossy metallic coat (low roughness, part metal), the undersuit stays matte.
"""
import argparse
import sys
from pathlib import Path

from PIL import Image, ImageFilter

import kotor_mdl
import kotor_tpc
import player_model

OUT_ROOT = "characters/models/swbs/store"
CLOTH = ("cloth", "cape", "robe", "flap", "belt")

# name -> model file (without extension) and credit
KOTOR_MODELS = {
    "kotor_malak": {"model": "n_darthmalak", "credit": "Darth Malak (Star Wars: KOTOR, BioWare / Lucasfilm)"},
    "kotor_revan": {"model": "n_darthrevan", "credit": "Darth Revan (Star Wars: KOTOR, BioWare / Lucasfilm)"},
    "kotor_sion": {"model": "n_darthsion", "credit": "Darth Sion (Star Wars: KOTOR II, Obsidian / Lucasfilm)"},
    "kotor_traya": {"model": "n_darthtraya", "credit": "Darth Traya (Star Wars: KOTOR II, Obsidian / Lucasfilm)"},
    "kotor_nihilus": {"model": "n_darthnihilus", "credit": "Darth Nihilus (Star Wars: KOTOR II, Obsidian / Lucasfilm)"},
    "kotor_sith_trooper": {"model": "n_sithcommando", "gloss_plates": True, "credit": "Sith trooper, silver (Star Wars: KOTOR II, Obsidian / Lucasfilm)"},
    "kotor_sith_trooper_red": {"model": "n_sithcommando", "recolor": {"n_sithcom01": "sith_red"}, "gloss_plates": True,
                               "credit": "Sith trooper, red (Star Wars: KOTOR II armour, red plates as in KOTOR)"},
}


def _recolor_sith_red(img):
    """White / grey armour plates -> KOTOR red-trooper red (shading kept); the black undersuit stays black."""
    import numpy as np
    from PIL import Image
    a = np.asarray(img.convert("RGB")).astype(np.float32) / 255
    lum = a.mean(-1)
    sat = a.max(-1) - a.min(-1)
    plate = np.clip((lum - 0.30) / 0.15, 0, 1) * np.clip((0.25 - sat) / 0.1, 0, 1)
    red = np.clip(lum[..., None] * np.array([0.70, 0.07, 0.06]), 0, 1)  # deep crimson like KOTOR's red troopers
    out = a * (1 - plate[..., None]) + red * plate[..., None]
    return Image.fromarray((out * 255).astype(np.uint8), "RGB")


RECOLORS = {"sith_red": _recolor_sith_red}


def _normal_map(img, strength: float = 2.5):
    """Normal map from the texture's shading (luminance as height), green up."""
    import numpy as np
    from PIL import Image
    h = np.asarray(img.convert("L").filter(ImageFilter.GaussianBlur(1.2))).astype(np.float32) / 255
    dx = np.gradient(h, axis=1) * strength * h.shape[1] / 256
    dy = np.gradient(h, axis=0) * strength * h.shape[0] / 256
    n = np.dstack([-dx, dy, np.ones_like(h)])
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return Image.fromarray(((n * 0.5 + 0.5) * 255).astype(np.uint8), "RGB")


def _plate_mask(rgb):
    """Armour plates: bright parts of the original (silver / white) texture, before any recolour. 0..1 array."""
    import numpy as np
    a = np.asarray(rgb.convert("RGB")).astype(np.float32) / 255
    lum = a.mean(-1)
    sat = a.max(-1) - a.min(-1)
    return np.clip((lum - 0.30) / 0.15, 0, 1) * np.clip((0.25 - sat) / 0.1, 0, 1)


def _gloss_plates(metal, rough, plates):
    """Glossy metallic coat on the armour plates (like the troopers' polished armour), the undersuit unchanged."""
    import numpy as np
    from PIL import Image
    m = np.maximum(np.asarray(metal).astype(np.float32) / 255, plates * 0.65)
    r = np.minimum(np.asarray(rough).astype(np.float32) / 255, 1 - plates * 0.82)  # plates: roughness ~0.18
    return Image.fromarray((m * 255).astype(np.uint8), "L"), Image.fromarray((r * 255).astype(np.uint8), "L")


def _shine_maps(alpha):
    """KOTOR envmap mask (alpha: 255 = no reflection) -> (metalness, roughness) greyscale images."""
    import numpy as np
    from PIL import Image
    reflect = 1 - np.asarray(alpha).astype(np.float32) / 255
    reflect = np.clip(reflect * 1.6, 0, 1)
    metal = Image.fromarray((reflect * 0.85 * 255).astype(np.uint8), "L")
    rough = Image.fromarray(((0.72 - reflect * 0.5) * 255).astype(np.uint8), "L")
    return metal, rough


def vmdl_path(name: str) -> str:
    return f"{OUT_ROOT}/{name}/{name}.vmdl"


def _find(folder: Path, name: str):
    for f in folder.rglob("*"):
        if f.is_file() and f.name.lower() == name.lower():
            return f
    return None


def _vmat(color_rel: str, normal_rel: str, metal_rel=None, rough_rel=None) -> str:
    lines = ['\tshader "csgo_character.vfx"', f'\tTextureColor "{color_rel}"', f'\tTextureNormal "{normal_rel}"']
    if metal_rel:
        lines += [f'\tTextureMetalness "{metal_rel}"', f'\tTextureRoughness "{rough_rel}"']
    else:
        lines.append('\tTextureRoughness "[0.650000 0.650000 0.650000 0.000000]"')
    return ("// SWBS Jailbreak store model - scripts/workshop/kotor_models.py\n"
            "Layer0\n{\n" + "\n".join(lines) + "\n}\n")


def convert(name: str, spec: dict, source: Path, addon: Path, log=print) -> str:
    mdl = _find(source, spec["model"] + ".mdl")
    if mdl is None or not mdl.with_suffix(".mdx").exists():
        raise FileNotFoundError(f"{name}: {spec['model']}.mdl / .mdx not found in {source}")
    model = kotor_mdl.read_model(mdl)
    src = kotor_mdl.to_source(model, kotor_mdl.CS2_BONES)
    src = player_model.drape(src, lambda b: any(k in b.lower() for k in CLOTH))
    target = player_model.load_target()
    vertices, weights = player_model.retarget(src, target)

    out_dir = f"{OUT_ROOT}/{name}"
    mat_dir = f"{out_dir}/materials"
    (addon / mat_dir).mkdir(parents=True, exist_ok=True)
    remaps, missing = [], []
    for texture in sorted({mat for mat, _ in src.meshes}):
        tpc = _find(source, texture + ".tpc")
        if tpc is None:
            missing.append(texture)
            continue
        rgba, txi = kotor_tpc.read_tpc(tpc)
        size = (rgba.width * 2, rgba.height * 2)
        img = rgba.convert("RGB")
        recolor = spec.get("recolor", {}).get(texture.lower())
        if recolor:
            img = RECOLORS[recolor](img)
        img = img.resize(size).filter(ImageFilter.UnsharpMask(radius=1.5, percent=60))
        color_rel = f"{mat_dir}/{texture}.png"
        img.save(addon / color_rel)
        normal_rel = f"{mat_dir}/{texture}_normal.png"
        _normal_map(img).save(addon / normal_rel)
        metal_rel = rough_rel = None
        if "envmaptexture" in txi.lower() or spec.get("gloss_plates"):
            alpha = rgba.getchannel("A") if "envmaptexture" in txi.lower() else Image.new("L", rgba.size, 255)
            metal, rough = _shine_maps(alpha.resize(size))
            if spec.get("gloss_plates"):
                metal, rough = _gloss_plates(metal, rough, _plate_mask(rgba.resize(size)))
            metal_rel, rough_rel = f"{mat_dir}/{texture}_metal.png", f"{mat_dir}/{texture}_rough.png"
            metal.save(addon / metal_rel)
            rough.save(addon / rough_rel)
        vmat_rel = f"{mat_dir}/{texture}.vmat"
        (addon / vmat_rel).write_text(_vmat(color_rel, normal_rel, metal_rel, rough_rel), encoding="ascii")
        smd = f"swbs_{name}_{texture}"
        remaps += [(smd, vmat_rel), (smd + ".vmat", vmat_rel)]
    if missing:
        raise FileNotFoundError(f"{name}: textures not found: {', '.join(t + '.tpc' for t in missing)}")

    smd_rel = f"{out_dir}/{name}.smd"
    used = player_model.write_skinned_smd(target, vertices, weights, src.meshes, addon / smd_rel, lambda t: f"swbs_{name}_{t}")
    bind_rel = f"{OUT_ROOT}/cs2_bind_pose.smd"
    player_model.write_bind_pose_smd(target, addon / bind_rel)
    vmdl_rel = vmdl_path(name)
    (addon / vmdl_rel).write_text(player_model.build_vmdl(smd_rel, bind_rel, remaps), encoding="ascii")
    log(f"  {vmdl_rel}: {sum(used.values())} triangles, {len(remaps) // 2} textures")
    return vmdl_rel


def convert_all(source: Path, addon: Path, names=None, log=print) -> list:
    made = []
    for name, spec in KOTOR_MODELS.items():
        if names and name not in names:
            continue
        if _find(source, spec["model"] + ".mdl") is None:
            log(f"  {name}: skipped ({spec['model']}.mdl not found)")
            continue
        made.append(convert(name, spec, source, addon, log))
    return made


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", required=True, help="folder with the exported .mdl / .mdx / .tpc files")
    ap.add_argument("--out", required=True, help="addon content folder to write into")
    ap.add_argument("names", nargs="*", help=f"models to build (default all: {', '.join(KOTOR_MODELS)})")
    a = ap.parse_args()
    made = convert_all(Path(a.source), Path(a.out), a.names)
    print(f"{len(made)} KOTOR model(s) written")
    return 0 if made else 1


if __name__ == "__main__":
    sys.exit(main())
