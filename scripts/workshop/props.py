"""
Converts the SWBS CS:GO props (flag, capture point, finish line, missile, rocket launcher) into CS2 content: an SMD mesh,
PNG textures, .vmat materials and a .vmdl ModelDoc file each, ready for the Workshop Tools' resource compiler.
"""
import re
from pathlib import Path

import source1_mdl

try:
    from srctools.vtf import VTF
except ImportError:  # pragma: no cover - build_pack.py reports it
    VTF = None

# name -> (model in the fork, meshes to leave out, textures made white so the plugin can tint them)
PROPS = {
    "flag": ("Mods/UltimateJailbreak/files/models/swoobles/ultimate_jailbreak/flag/flag.mdl", {"flag_fx_yellow"}, {"flag_yellow"}),
    "capture_point": ("Mods/UltimateJailbreak/files/models/swoobles/ultimate_jailbreak/capture_point/block.mdl", set(), {"block"}),
    "finish_line": ("Mods/UltimateJailbreak/files/models/swoobles/ultimate_jailbreak/finish_line/finish_line.mdl", set(), set()),
    "missile": ("Mods/UltimateJailbreak/files/models/swoobles/rocket_jumping/missile/missile.mdl", set(), set()),
    "rocket_launcher": ("Mods/UltimateJailbreak/files/models/weapons/rocket_launcher/w_rocket_launcher.mdl", set(), set()),
}

MATERIAL_ROOTS = ["Mods/UltimateJailbreak/files/materials", "Plugins/CustomWeapons/files/materials"]

VMDL_HEADER = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->"


def _find_ci(root: Path, rel: str):
    """Case-insensitive file lookup under root (Source 1 paths are case-insensitive)."""
    cur = root
    for part in rel.split("/"):
        if not cur.is_dir():
            return None
        match = next((c for c in cur.iterdir() if c.name.lower() == part.lower()), None)
        if match is None:
            return None
        cur = match
    return cur


def _base_texture(fork: Path, cdmaterials, texture: str):
    """The .vtf a model texture uses: materials/<cdmaterials>/<texture>.vmt -> $basetexture."""
    for root in MATERIAL_ROOTS:
        for cd in cdmaterials or [""]:
            vmt = _find_ci(fork / root, f"{cd}/{texture}.vmt".strip("/"))
            if vmt is None:
                continue
            m = re.search(r'"?\$basetexture"?\s+"?([^"\s]+)"?', vmt.read_text(errors="ignore"), re.IGNORECASE)
            if not m:
                continue
            tex = m.group(1).replace("\\", "/")
            for root2 in MATERIAL_ROOTS:
                vtf = _find_ci(fork / root2, tex + ".vtf")
                if vtf is not None:
                    return vtf
    return None


def _vmat(texture_rel: str) -> str:
    return (
        "// SWBS Jailbreak - converted from the CS:GO material by scripts/workshop/props.py\n"
        "Layer0\n"
        "{\n"
        '\tshader "csgo_complex.vfx"\n'
        "\n"
        f'\tTextureColor "{texture_rel}"\n'
        "}\n"
    )


def _vmdl(name: str, smd_rel: str, remaps) -> str:
    remap_lines = "".join(
        "\t\t\t\t\t\t\t{\n"
        f'\t\t\t\t\t\t\t\tfrom = "{src}"\n'
        f'\t\t\t\t\t\t\t\tto = "{dst}"\n'
        "\t\t\t\t\t\t\t},\n"
        for src, dst in remaps
    )
    return f"""{VMDL_HEADER}
{{
	rootNode = 
	{{
		_class = "RootNode"
		children = 
		[
			{{
				_class = "MaterialGroupList"
				children = 
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps = 
						[
{remap_lines}						]
						use_global_default = false
						global_default_material = ""
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children = 
				[
					{{
						_class = "RenderMeshFile"
						filename = "{smd_rel}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 1.0
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter = 
						{{
							exclude_by_default = false
							exception_list = [  ]
						}}
					}},
				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""


def convert_all(fork: Path, addon: Path, log=print) -> list:
    """Writes every prop into the addon content folder; returns the .vmdl paths (relative to the addon)."""
    if VTF is None:
        raise RuntimeError("pip install srctools (reads the CS:GO .vtf textures)")
    made = []
    for name, (mdl_rel, skip, tint) in PROPS.items():
        model = source1_mdl.read_model(fork / mdl_rel)
        mat_dir = f"materials/swbs/{name}"
        remaps = []
        textures_done = {}

        def material(tex: str):
            if tex in skip:
                return None
            key = tex.lower()
            if key not in textures_done:
                vtf_path = _base_texture(fork, model.cdmaterials, tex)
                if vtf_path is None:
                    raise FileNotFoundError(f"{name}: texture for material '{tex}' not found")
                with vtf_path.open("rb") as f:
                    vtf = VTF.read(f)
                    vtf.load()
                    img = vtf.get().to_PIL()
                if tex in tint:
                    # Light grey so the plugin's render colour tints it to any team colour.
                    from PIL import ImageOps
                    alpha = img.getchannel("A") if "A" in img.getbands() else None
                    img = ImageOps.autocontrast(ImageOps.grayscale(img), cutoff=1).point(lambda v: 110 + v * 145 // 255).convert("RGBA")
                    if alpha is not None:
                        img.putalpha(alpha)
                png_rel = f"{mat_dir}/{key}.png"
                (addon / png_rel).parent.mkdir(parents=True, exist_ok=True)
                img.save(addon / png_rel)
                vmat_rel = f"{mat_dir}/{key}.vmat"
                (addon / vmat_rel).write_text(_vmat(png_rel), encoding="ascii")
                smd_name = f"swbs_{name}_{key}"
                remaps.append((smd_name, vmat_rel))
                remaps.append((smd_name + ".vmat", vmat_rel))
                textures_done[key] = smd_name
            return textures_done[key]

        smd_rel = f"models/swbs/{name}/{name}.smd"
        used = source1_mdl.write_smd(model, addon / smd_rel, material)
        vmdl_rel = f"models/swbs/{name}.vmdl"
        (addon / vmdl_rel).write_text(_vmdl(name, smd_rel, remaps), encoding="ascii")
        lo, hi = source1_mdl.bounds(model)
        log(f"  {vmdl_rel}: {sum(used.values())} triangles, {len(textures_done)} materials, size "
            f"{hi[0] - lo[0]:.0f} x {hi[1] - lo[1]:.0f} x {hi[2] - lo[2]:.0f}")
        made.append(vmdl_rel)
    return made
