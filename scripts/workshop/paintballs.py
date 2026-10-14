"""
Paintball splats for the workshop pack: the three original SWBS splats (blue, green, pink) plus generated ones, each a
flat 12-unit see-through square model (models/swbs/paintballs/<name>.vmdl). The textures are white with the splat's
shading, so the plugin tints every splat to the paint colour (render colour). The quad faces +X; the plugin turns it
to face away from the surface the bullet hit.

On CS:GO the splats were decals placed with TE World Decal; CS2 servers cannot place decals, so the plugin spawns these
models flat on the surface instead.
"""
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

from props import _vmdl

try:
    from srctools.vtf import VTF
except ImportError:  # pragma: no cover - build_pack.py reports it
    VTF = None

ORIGINALS = "Mods/UltimateJailbreak/files/materials/swoobles/ultimate_jailbreak/paintballs"
ORIGINAL_NAMES = ["splat_blue", "splat_green", "splat_pink"]
GENERATED_SEEDS = [11, 23, 37, 41, 58]  # fixed seeds: the same shapes on every build
SIZE = 12.0  # units across: the original decals were 128 px at $decalscale 0.09


def names() -> list:
    return ORIGINAL_NAMES + [f"splat_{i + 1}" for i in range(len(GENERATED_SEEDS))]


def model_path(name: str) -> str:
    return f"models/swbs/paintballs/{name}.vmdl"


def _whiten(img: Image.Image) -> Image.Image:
    """An original coloured splat -> white with its shading kept (brightest pixel = white), alpha unchanged."""
    a = np.asarray(img.convert("RGBA")).astype(np.float32)
    v = a[..., :3].max(-1)
    inside = a[..., 3] > 32
    top = float(np.percentile(v[inside], 95)) if inside.any() else 255.0
    g = np.clip(v / max(top, 1.0), 0, 1) * 255
    g = np.where(inside, np.maximum(g, 150), 255).astype(np.uint8)
    return Image.fromarray(np.dstack([g, g, g, a[..., 3].astype(np.uint8)]), "RGBA")


def _generate(seed: int, n: int = 256) -> Image.Image:
    """A paint splat: wobbly body, streaks that end in drops, loose droplets. White with slight shading."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32)
    c = n / 2
    ang = np.arctan2(yy - c, xx - c)
    r = np.hypot(xx - c, yy - c)
    base = n * rng.uniform(0.17, 0.22)
    wob = np.zeros_like(ang)
    for k in range(2, 9):
        wob += rng.uniform(0, 0.06) * np.sin(k * ang + rng.uniform(0, 6.28))
    m = (r < base * (1 + wob)).astype(np.float32)
    for _ in range(rng.integers(7, 14)):
        a = rng.uniform(0, 6.28)
        length = base * rng.uniform(1.25, 2.0)
        w = rng.uniform(3, 7)
        t = np.clip(((xx - c) * np.cos(a) + (yy - c) * np.sin(a)) / length, 0, 1)
        d = np.hypot(xx - (c + np.cos(a) * length * t), yy - (c + np.sin(a) * length * t))
        m = np.maximum(m, (d < w * (1 - 0.6 * t)).astype(np.float32))
        end = np.hypot(xx - (c + np.cos(a) * length), yy - (c + np.sin(a) * length))
        m = np.maximum(m, (end < w * rng.uniform(0.9, 1.5)).astype(np.float32))
    for _ in range(rng.integers(6, 14)):
        a = rng.uniform(0, 6.28)
        length = base * rng.uniform(1.3, 2.4)
        m = np.maximum(m, (np.hypot(xx - (c + np.cos(a) * length), yy - (c + np.sin(a) * length)) < rng.uniform(2, 6))
                       .astype(np.float32))
    alpha = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2)))
    shade = np.clip(1.0 - 0.18 * (r / (base * 2.2)), 0.75, 1.0) * (0.92 + 0.08 * rng.random((n, n)))
    g = (255 * shade).astype(np.uint8)
    return Image.fromarray(np.dstack([g, g, g, alpha]), "RGBA")


def _quad_smd(material: str) -> str:
    h = SIZE / 2
    # (y, z, u, v) corners, counter-clockwise seen from +X; SMD v is bottom-up.
    corners = [(-h, -h, 0.0, 0.0), (h, -h, 1.0, 0.0), (h, h, 1.0, 1.0), (-h, h, 0.0, 1.0)]

    def vert(i):
        y, z, u, v = corners[i]
        return f"0 0.000000 {y:.6f} {z:.6f} 1.000000 0.000000 0.000000 {u:.6f} {v:.6f} 1 0 1"

    lines = ["version 1", "nodes", '0 "root" -1', "end", "skeleton", "time 0", "0 0 0 0 0 0 0", "end", "triangles"]
    for tri in ((0, 1, 2), (0, 2, 3)):
        lines.append(material)
        lines += [vert(i) for i in tri]
    lines.append("end")
    return "\n".join(lines) + "\n"


def _vmat(color_rel: str, mask_rel: str) -> str:
    return (
        "// SWBS Jailbreak paintball splat - scripts/workshop/paintballs.py (white: the plugin tints it)\n"
        "Layer0\n{\n"
        '\tshader "csgo_complex.vfx"\n'
        "\tF_TRANSLUCENT 1\n"
        "\tF_RENDER_BACKFACES 1\n"
        f'\tTextureColor "{color_rel}"\n'
        f'\tTextureTranslucency "{mask_rel}"\n'
        '\tTextureRoughness "[0.350000 0.350000 0.350000 0.000000]"\n'
        "}\n"
    )


def convert_all(fork: Path, addon: Path, log=print) -> list:
    """Writes every splat into the addon content folder; returns the .vmdl paths (relative to the addon)."""
    if VTF is None:
        raise RuntimeError("pip install srctools (reads the CS:GO .vtf textures)")
    images = {}
    for name in ORIGINAL_NAMES:
        with (fork / ORIGINALS / f"{name}.vtf").open("rb") as f:
            vtf = VTF.read(f)
            vtf.load()
            images[name] = _whiten(vtf.get().to_PIL())
    for i, seed in enumerate(GENERATED_SEEDS):
        images[f"splat_{i + 1}"] = _generate(seed)

    made = []
    mat_dir = "materials/swbs/paintballs"
    (addon / mat_dir).mkdir(parents=True, exist_ok=True)
    for name, img in images.items():
        color_rel, mask_rel, vmat_rel = f"{mat_dir}/{name}.png", f"{mat_dir}/{name}_trans.png", f"{mat_dir}/{name}.vmat"
        img.convert("RGB").save(addon / color_rel)
        img.getchannel("A").save(addon / mask_rel)
        (addon / vmat_rel).write_text(_vmat(color_rel, mask_rel), encoding="ascii")
        smd_name = f"swbs_paint_{name}"
        smd_rel = f"models/swbs/paintballs/{name}.smd"
        (addon / smd_rel).parent.mkdir(parents=True, exist_ok=True)
        (addon / smd_rel).write_text(_quad_smd(smd_name), encoding="ascii")
        vmdl_rel = model_path(name)
        (addon / vmdl_rel).write_text(_vmdl(name, smd_rel, [(smd_name, vmat_rel), (smd_name + ".vmat", vmat_rel)]),
                                      encoding="ascii")
        made.append(vmdl_rel)
    log(f"  models/swbs/paintballs: {len(made)} splats ({len(ORIGINAL_NAMES)} original, {len(GENERATED_SEEDS)} new)")
    return made
