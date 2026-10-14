"""
Converts the SWBS CS:GO prisoner models onto CS2's current player skeleton (player_model.py): skinned SMD, PNG textures,
.vmat materials and a .vmdl each, with an "orange" default and a "white" material group (the SWBS plugin switched to the
white uniform while a prisoner was colour-tinted).

The SWBS guard is not converted: it is Valve's CS:GO ct_swat model with Valve's textures, and CS2 ships its own SWAT and
SAS agents (PlayerModels.GuardModel defaults to one).
"""
import re
from pathlib import Path

import player_model
import source1_mdl
from props import MATERIAL_ROOTS, _find_ci

try:
    from srctools.vtf import VTF
except ImportError:  # pragma: no cover - build_pack.py reports it
    VTF = None

MODEL_ROOT = "Mods/UltimateJailbreak/files/models/player/custom_player/swoobles/prisoners"
OUT_ROOT = "characters/models/swbs/prisoners"
BIND_POSE = "characters/models/swbs/shared/cs2_bind_pose.smd"

# CS2 model name -> source model folder (and .mdl name)
PRISONERS = {
    "male_black_big": "male_black_big_new",
    "female_asian": "female_asian_new",
    "male_white_hair": "male_white_hair_new",
}


def vmdl_path(name: str) -> str:
    return f"{OUT_ROOT}/{name}/{name}.vmdl"


def _vmt(fork: Path, cdmaterials, texture: str):
    for root in MATERIAL_ROOTS:
        for cd in cdmaterials or [""]:
            vmt = _find_ci(fork / root, f"{cd}/{texture}.vmt".strip("/"))
            if vmt is not None:
                return vmt
    return None


def _vtf(fork: Path, rel: str):
    for root in MATERIAL_ROOTS:
        vtf = _find_ci(fork / root, rel.replace("\\", "/") + ".vtf")
        if vtf is not None:
            return vtf
    return None


def _param(text: str, name: str):
    m = re.search(r'"?\$' + name + r'"?\s+"?([^"\s]+)"?', text, re.IGNORECASE)
    return m.group(1) if m else None


def _load_png(vtf_path: Path):
    with vtf_path.open("rb") as f:
        vtf = VTF.read(f)
        vtf.load()
        return vtf.get().to_PIL().convert("RGBA")


def _material(fork: Path, model, texture: str, out_dir: str, addon: Path, done: dict) -> str:
    """Writes <out_dir>/<texture>.vmat (+ PNGs) for a CS:GO VertexLitGeneric material; returns the .vmat path."""
    key = texture.lower()
    if key in done:
        return done[key]
    vmt = _vmt(fork, model.cdmaterials, texture)
    if vmt is None:
        raise FileNotFoundError(f"material '{texture}' not found")
    text = vmt.read_text(errors="ignore")
    base, bump = _param(text, "basetexture"), _param(text, "bumpmap")
    translucent = _param(text, "translucent") == "1"
    alphatest = _param(text, "alphatest") == "1"
    nocull = _param(text, "nocull") == "1"
    vtf = _vtf(fork, base) if base else None
    if vtf is None:
        raise FileNotFoundError(f"material '{texture}': texture '{base}' not found")

    (addon / out_dir).mkdir(parents=True, exist_ok=True)
    color = _load_png(vtf)
    color_rel = f"{out_dir}/{key}_color.png"
    color.convert("RGB").save(addon / color_rel)
    lines = []
    if translucent or alphatest:
        # csgo_complex handles alpha test / translucency; csgo_character is for opaque skin and clothes.
        mask_rel = f"{out_dir}/{key}_trans.png"
        color.getchannel("A").save(addon / mask_rel)
        lines += ['\tshader "csgo_complex.vfx"', f'\t{"F_TRANSLUCENT" if translucent else "F_ALPHA_TEST"} 1',
                  f'\tTextureTranslucency "{mask_rel}"']
    else:
        lines += ['\tshader "csgo_character.vfx"']
    if nocull:
        lines.append("\tF_RENDER_BACKFACES 1")
    lines.append(f'\tTextureColor "{color_rel}"')
    bump_vtf = _vtf(fork, bump) if bump else None
    if bump_vtf is not None:
        normal = _load_png(bump_vtf).convert("RGB")
        r, g, b = normal.split()
        from PIL import Image, ImageOps
        normal = Image.merge("RGB", (r, ImageOps.invert(g), b))  # Source 1 normal maps are green-down
        normal_rel = f"{out_dir}/{key}_normal.png"
        normal.save(addon / normal_rel)
        lines.append(f'\tTextureNormal "{normal_rel}"')
    lines.append('\tTextureRoughness "[0.700000 0.700000 0.700000 0.000000]"')
    vmat_rel = f"{out_dir}/{key}.vmat"
    (addon / vmat_rel).write_text(
        "// SWBS Jailbreak - converted from the CS:GO material by scripts/workshop/prisoners.py\n"
        "Layer0\n{\n" + "\n".join(lines) + "\n}\n", encoding="ascii")
    done[key] = vmat_rel
    return vmat_rel


def _skin_family(mdl_path: Path, family: int):
    """Texture names of a skin family (index into the model's skin table)."""
    import struct
    data = mdl_path.read_bytes()
    num_tex, tex_index, _, _, nref, nfam, skin_index = struct.unpack_from("<7i", data, 204)
    if family >= nfam:
        return None
    names = []
    for i in range(num_tex):
        off = tex_index + i * 64
        names.append(source1_mdl._cstr(data, off + struct.unpack_from("<i", data, off)[0]))
    return [names[t] for t in struct.unpack_from(f"<{nref}h", data, skin_index + family * nref * 2)]


def convert_all(fork: Path, addon: Path, log=print) -> list:
    """Writes every prisoner into the addon content folder; returns the .vmdl paths (relative to the addon)."""
    if VTF is None:
        raise RuntimeError("pip install srctools (reads the CS:GO .vtf textures)")
    target = player_model.load_target()
    player_model.write_bind_pose_smd(target, addon / BIND_POSE)
    made = []
    for name, src_name in PRISONERS.items():
        mdl_path = fork / MODEL_ROOT / src_name / f"{src_name}.mdl"
        model = source1_mdl.read_model(mdl_path)
        out_dir = f"{OUT_ROOT}/{name}"
        mat_dir = f"{out_dir}/materials"
        done = {}
        smd_names = {}

        def smd_material(tex: str):
            key = tex.lower()
            smd_names[key] = f"swbs_{name}_{key}"
            return smd_names[key]

        vertices, weights = player_model.retarget(player_model.from_mdl(model), target)
        smd_rel = f"{out_dir}/{name}.smd"
        used = player_model.write_skinned_smd(target, vertices, weights, [(m.material, m.triangles) for m in model.meshes],
                                              addon / smd_rel, smd_material)
        remaps = []
        for key, smd in smd_names.items():
            vmat = _material(fork, model, key, mat_dir, addon, done)
            remaps += [(smd, vmat), (smd + ".vmat", vmat)]
        groups = {}
        default, white = _skin_family(mdl_path, 0), _skin_family(mdl_path, 1)
        if default and white:
            pairs = []
            for a, b in zip(default, white):
                if a.lower() != b.lower() and a.lower() in smd_names:
                    vmat = _material(fork, model, b, mat_dir, addon, done)
                    pairs += [(smd_names[a.lower()], vmat), (smd_names[a.lower()] + ".vmat", vmat)]
            if pairs:
                groups["white"] = pairs
        vmdl_rel = vmdl_path(name)
        (addon / vmdl_rel).write_text(player_model.build_vmdl(smd_rel, BIND_POSE, remaps, groups), encoding="ascii")
        log(f"  {vmdl_rel}: {sum(used.values())} triangles, {len(done)} materials, white uniform: {'yes' if groups else 'no'}")
        made.append(vmdl_rel)
    return made
