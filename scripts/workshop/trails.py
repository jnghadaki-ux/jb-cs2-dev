"""
SWBS trail pieces for the workshop pack (new designs, drawn here; not from any game). The plugin drops one piece every
few steps behind a player wearing a trail and removes it after a moment. Every texture is white with dark outlines,
so the plugin tints each piece to the trail's colour (render colour).

  footprint_l / footprint_r   shoe prints lying flat on the floor (+X = toe), dropped alternately; the only pieces
                              on the floor: everything else hangs behind the player's back
  star heart skull lightning snowflake note
                              two crossed 8-unit cards (look solid from every side)
  coin orb bubble             real 3D pieces: a rimmed coin with a star, a smooth ball, a see-through bubble
  flame_1..4                  fire trail: turbulent flame cards, coloured like burning fire (dark red edge -> orange
                              -> yellow-white core)
  ice_1..3                    ice trail: faceted see-through ice crystals, frosty at the base, clear at the tip, with
                              fine cracks (a tall shard, a cluster of three, a spread of small ones)
  galaxy galaxy_stars         galaxy trail: a purple / blue spiral galaxy card and a card of twinkling stars
The fire, ice and galaxy pieces keep their own colours (the plugin leaves them untinted).

Models: models/swbs/trails/<name>.vmdl, materials: materials/swbs/trails/<name>.vmat.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

from props import _vmdl

FLAT = ["footprint_l", "footprint_r"]
CARDS = ["star", "heart", "skull", "lightning", "snowflake", "note"]
SOLIDS = ["coin", "orb", "bubble"]
FIRE = ["flame_1", "flame_2", "flame_3", "flame_4"]
ICE = ["ice_1", "ice_2", "ice_3"]
GALAXY = ["galaxy", "galaxy_stars"]
COLOURED = FIRE + ICE + GALAXY  # drawn in colour, not tinted

N = 256  # texture size
SS = 4   # supersampling for smooth edges

WHITE, DARK, MID = 255, 70, 150  # body, outline / detail, coin rim


def names() -> list:
    return FLAT + CARDS + SOLIDS + COLOURED


def model_path(name: str) -> str:
    return f"models/swbs/trails/{name}.vmdl"


# ---------------------------------------------------------------- textures

class _Canvas:
    """Draws at SS x size; grey = colour (tinted in game), alpha = shape."""

    def __init__(self):
        self.size = N * SS
        self.grey = Image.new("L", (self.size, self.size), WHITE)
        self.alpha = Image.new("L", (self.size, self.size), 0)
        self.g = ImageDraw.Draw(self.grey)
        self.a = ImageDraw.Draw(self.alpha)

    def p(self, x, y):
        """Normalised (0..1, y down) -> canvas pixels."""
        return x * self.size, y * self.size

    def poly(self, pts, fill=WHITE, outline=DARK, width=0.02):
        px = [self.p(x, y) for x, y in pts]
        self.a.polygon(px, fill=255)
        self.g.polygon(px, fill=fill)
        if outline is not None:
            self.g.line(px + [px[0]], fill=outline, width=int(width * self.size), joint="curve")

    def ellipse(self, x0, y0, x1, y1, fill=WHITE, outline=DARK, width=0.02, shape=True):
        box = [*self.p(x0, y0), *self.p(x1, y1)]
        if shape:
            self.a.ellipse(box, fill=255)
        self.g.ellipse(box, fill=fill, outline=outline, width=int(width * self.size) if outline is not None else 0)

    def line(self, pts, fill=DARK, width=0.03, shape=False):
        px = [self.p(x, y) for x, y in pts]
        self.g.line(px, fill=fill, width=int(width * self.size), joint="curve")
        if shape:
            self.a.line(px, fill=255, width=int(width * self.size), joint="curve")

    def outline_from_alpha(self, width=0.02):
        """A dark rim just inside the shape's edge (for shapes drawn without a stroke)."""
        edge = self.alpha.filter(ImageFilter.MinFilter(int(width * self.size) | 1))
        rim = Image.eval(Image.eval(edge, lambda v: 255 - v), lambda v: v)
        self.grey.paste(DARK, mask=Image.composite(rim, Image.new("L", rim.size, 0), self.alpha))

    def result(self) -> Image.Image:
        img = Image.merge("RGBA", (self.grey, self.grey, self.grey, self.alpha))
        return img.resize((N, N), Image.LANCZOS)


def _star(c):
    pts = []
    for i in range(10):
        r = 0.46 if i % 2 == 0 else 0.19
        a = -math.pi / 2 + i * math.pi / 5
        pts.append((0.5 + r * math.cos(a), 0.53 + r * math.sin(a)))
    c.poly(pts, width=0.035)


def _heart(c):
    pts = []
    for i in range(120):
        t = i / 120 * 2 * math.pi
        x = 16 * math.sin(t) ** 3
        y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((0.5 + x / 36, 0.47 - y / 36))
    c.poly(pts, width=0.035)


def _skull(c):
    c.ellipse(0.17, 0.08, 0.83, 0.70, width=0.035)                     # cranium
    c.poly([(0.30, 0.55), (0.70, 0.55), (0.66, 0.90), (0.34, 0.90)], width=0.035)  # jaw
    c.ellipse(0.17 + 0.012, 0.08 + 0.012, 0.83 - 0.012, 0.70 - 0.012, outline=None, shape=False)  # hide the jaw seam
    c.ellipse(0.27, 0.34, 0.45, 0.53, fill=DARK, outline=None, shape=False)  # eye sockets
    c.ellipse(0.55, 0.34, 0.73, 0.53, fill=DARK, outline=None, shape=False)
    c.poly([(0.50, 0.55), (0.45, 0.66), (0.55, 0.66)], fill=DARK, outline=None)  # nose
    for x in (0.40, 0.47, 0.53, 0.60):                                    # teeth
        c.line([(x, 0.74), (x, 0.88)], width=0.02)
    c.line([(0.34, 0.78), (0.66, 0.78)], width=0.02)


def _lightning(c):
    c.poly([(0.58, 0.04), (0.22, 0.56), (0.46, 0.56), (0.36, 0.96), (0.80, 0.40), (0.55, 0.40), (0.70, 0.04)], width=0.035)


def _snowflake(c):
    for k in range(6):
        a = k * math.pi / 3
        ex, ey = 0.5 + 0.44 * math.cos(a), 0.5 + 0.44 * math.sin(a)
        c.line([(0.5, 0.5), (ex, ey)], fill=WHITE, width=0.075, shape=True)
        for f in (0.45, 0.72):  # branches
            bx, by = 0.5 + 0.44 * f * math.cos(a), 0.5 + 0.44 * f * math.sin(a)
            for s in (-1, 1):
                b = a + s * math.pi / 4
                c.line([(bx, by), (bx + 0.14 * math.cos(b), by + 0.14 * math.sin(b))], fill=WHITE, width=0.06, shape=True)
    c.ellipse(0.40, 0.40, 0.60, 0.60, outline=None)
    c.outline_from_alpha(0.03)


def _note(c):
    c.ellipse(0.16, 0.66, 0.46, 0.90, outline=None)                     # head
    c.poly([(0.40, 0.76), (0.46, 0.76), (0.46, 0.10), (0.40, 0.10)], outline=None)  # stem
    c.poly([(0.43, 0.10), (0.46, 0.10), (0.80, 0.30), (0.78, 0.46), (0.46, 0.30)], outline=None)  # flag
    c.outline_from_alpha(0.035)


def _noise(n: int, seed: int, octaves: int = 5, base: int = 4):
    """Fractal value noise in 0..1 (numpy array n x n)."""
    import numpy as np
    rng = np.random.default_rng(seed)
    out = np.zeros((n, n), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        cells = base * 2 ** o
        grid = rng.random((cells + 1, cells + 1)).astype(np.float32)
        img = Image.fromarray((grid * 255).astype(np.uint8)).resize((n, n), Image.BICUBIC)
        out += amp * np.asarray(img, np.float32) / 255
        total += amp
        amp *= 0.5
    return out / total


def _footprint(mirror: bool) -> Image.Image:
    """Realistic shoe print, toe to the right (+X in the model), image width = length of the shoe: sole outline with
    heel, narrow arch on the inner side and wide ball; chevron tread lugs, a ring-tread heel, patchy ink that is
    strongest where the weight lands. mirror=True for the right foot."""
    import numpy as np
    n = N
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32) / n
    if mirror:
        yy = 1 - yy
    t = xx                                               # 0 heel .. 1 toe
    # half widths of the sole towards the outer (y < centre) and inner (y > centre) side
    prof_t = [0.02, 0.10, 0.25, 0.42, 0.55, 0.70, 0.82, 0.92, 0.985]
    outer = np.interp(t, prof_t, [0.12, 0.25, 0.27, 0.26, 0.28, 0.32, 0.32, 0.26, 0.10])
    inner = np.interp(t, prof_t, [0.12, 0.25, 0.25, 0.17, 0.22, 0.34, 0.35, 0.28, 0.10])
    cy = 0.5 + 0.03 * np.sin((t - 0.2) * 2.5)            # slight curve of the shoe
    dy = yy - cy
    half = np.where(dy < 0, outer, inner)
    inside = (np.abs(dy) < half) & (t > 0.015) & (t < 0.99)
    edge = np.clip((half - np.abs(dy)) / 0.025, 0, 1)    # soft sole edge
    # tread: fine chevron lugs on the front, cross-hatched heel, a light arch; grooves stay faintly inked
    chev = np.sin((t * 80 + np.abs(dy) * 45) * np.pi) > -0.35
    hatch = (np.sin(t * 90 * np.pi) > -0.3) & (np.sin(dy * 60 * np.pi) > -0.6)
    tread = np.where(t < 0.36, hatch, np.where(t < 0.52, 0.55, chev)).astype(np.float32)
    tread = 0.35 + 0.65 * tread
    # ink: weight on heel and ball, patchy noise, worn edges
    weight = np.clip(np.interp(t, [0, 0.2, 0.45, 0.72, 1], [0.9, 1.0, 0.45, 1.0, 0.8]), 0, 1)
    patch = _noise(n, 101 if mirror else 202)
    ink = np.clip((patch * 1.2 + weight * 0.6 - 0.62) * 2.6, 0, 1)
    a = inside * edge * tread * ink
    a = np.asarray(Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.9)))
    grey = (200 + 55 * _noise(n, 303, 3, 8)).astype(np.uint8)  # a little variation in the paint
    return Image.fromarray(np.dstack([grey, grey, grey, a]).astype(np.uint8), "RGBA")


def _coin_texture() -> Image.Image:
    """Opaque: rim grey everywhere (the coin's edge samples a corner), face circle with a star in the middle."""
    c = _Canvas()
    c.a.rectangle([0, 0, c.size, c.size], fill=255)
    c.g.rectangle([0, 0, c.size, c.size], fill=MID)
    c.ellipse(0.04, 0.04, 0.96, 0.96, fill=WHITE, outline=DARK, width=0.04)
    c.ellipse(0.14, 0.14, 0.86, 0.86, fill=WHITE, outline=MID, width=0.025)
    pts = []
    for i in range(10):
        r = 0.27 if i % 2 == 0 else 0.11
        a = -math.pi / 2 + i * math.pi / 5
        pts.append((0.5 + r * math.cos(a), 0.52 + r * math.sin(a)))
    c.g.polygon([c.p(x, y) for x, y in pts], fill=MID, outline=DARK)
    return c.result()


def _flame(seed: int) -> Image.Image:
    """A burning flame: turbulent tongues (the shape is pushed around by fractal noise that grows with height), coloured
    by temperature like real fire: dark red at the cooling edges, orange body, yellow then white-hot core at the base."""
    import numpy as np
    n = N
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32) / n
    h = 1.0 - yy                                           # 0 bottom .. 1 top
    nx = _noise(n, seed * 7 + 1, 5, 3) - 0.5
    ny = _noise(n, seed * 7 + 2, 5, 3) - 0.5
    x = xx + nx * 0.35 * (0.2 + h)                         # turbulence, stronger higher up
    hh = np.clip(h + ny * 0.25 * h, 0, 1.2)
    width = np.interp(hh, [0, 0.08, 0.25, 0.55, 0.85, 1.05], [0.10, 0.26, 0.30, 0.22, 0.09, 0.0])
    d = np.abs(x - 0.5) / np.maximum(width, 1e-3)
    detail = _noise(n, seed * 7 + 3, 6, 6)
    temp = np.clip((1 - d) * (1.15 - hh) + (detail - 0.5) * 0.35, 0, 1)   # 0 cold .. 1 hottest
    body = np.clip((1 - d) * 2.2, 0, 1) * np.clip((1.05 - hh) * 3, 0, 1) * (h > 0.01)
    # blackbody-ish ramp
    stops = [0.0, 0.25, 0.5, 0.75, 1.0]
    r = np.interp(temp, stops, [0.35, 0.85, 1.00, 1.00, 1.00])
    g = np.interp(temp, stops, [0.02, 0.18, 0.50, 0.82, 0.98])
    b = np.interp(temp, stops, [0.00, 0.02, 0.06, 0.30, 0.85])
    a = np.clip(body * (0.35 + temp * 1.2), 0, 1)
    img = np.dstack([r, g, b, a]) * 255
    return Image.fromarray(img.astype(np.uint8), "RGBA").filter(ImageFilter.GaussianBlur(1.0))


def _ice() -> Image.Image:
    """Ice crystal texture, v = height (0 tip .. 1 base): frosty white base, clear pale-blue tip, fine bright cracks."""
    import numpy as np
    rng = np.random.default_rng(5)
    n = N
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32) / n
    frost = np.clip((yy - 0.45) * 2.0, 0, 1) * (0.6 + 0.4 * _noise(n, 41, 5, 6))
    r = 0.72 + 0.28 * frost
    g = 0.90 + 0.10 * frost
    b = np.ones_like(frost)
    a = 0.42 + 0.45 * frost
    img = Image.fromarray((np.dstack([r, g, b, a]) * 255).astype(np.uint8), "RGBA")
    d = ImageDraw.Draw(img)
    for _ in range(14):  # cracks
        x, y = rng.uniform(0, n), rng.uniform(n * 0.2, n)
        pts = [(x, y)]
        for _ in range(rng.integers(3, 7)):
            x += rng.uniform(-18, 18)
            y += rng.uniform(-22, 10)
            pts.append((x, y))
        d.line(pts, fill=(245, 252, 255, 235), width=1)
    return img.filter(ImageFilter.GaussianBlur(0.4))


def _galaxy() -> Image.Image:
    """Two-armed spiral galaxy: warm white core, purple / blue arms, white star specks."""
    import numpy as np
    rng = np.random.default_rng(7)
    n = N
    yy, xx = (np.mgrid[0:n, 0:n].astype(np.float32) / n - 0.5) * 2
    r = np.hypot(xx, yy) + 1e-4
    th = np.arctan2(yy, xx)
    arms = np.zeros_like(r)
    for k in range(2):
        phase = (th - 2.6 * np.log(r) + k * np.pi) % (2 * np.pi)
        dist = np.minimum(phase, 2 * np.pi - phase)
        arms += np.exp(-(dist ** 2) / 0.35)
    fall = np.clip(1 - r, 0, 1)
    core = np.exp(-(r ** 2) / 0.02)
    inten = np.clip(arms * fall ** 1.1 * 1.5 + core, 0, 1)
    t = np.clip(r * 1.4, 0, 1)  # colour by radius: core warm white -> pink/purple -> blue
    col_r = (1 - t) * 1.0 + t * np.interp(t, [0, 0.5, 1], [0.95, 0.65, 0.25])
    col_g = (1 - t) * 0.95 + t * np.interp(t, [0, 0.5, 1], [0.85, 0.35, 0.45])
    col_b = (1 - t) * 0.85 + t * np.interp(t, [0, 0.5, 1], [0.95, 0.95, 1.0])
    rgb = np.dstack([col_r, col_g, col_b])
    alpha = np.clip(inten * 1.3, 0, 1)
    for _ in range(90):  # star specks
        x, y = rng.integers(8, n - 8, 2)
        if np.hypot(x / n - 0.5, y / n - 0.5) < 0.48:
            rgb[y - 1:y + 2, x - 1:x + 2] = 1.0
            alpha[y - 1:y + 2, x - 1:x + 2] = np.maximum(alpha[y - 1:y + 2, x - 1:x + 2], 0.9)
    img = np.dstack([rgb, alpha[..., None]]) * 255
    return Image.fromarray(img.astype(np.uint8), "RGBA").filter(ImageFilter.GaussianBlur(0.8))


def _twinkles() -> Image.Image:
    """A few four-point twinkling stars, white to pale blue."""
    c = Image.new("RGBA", (N * SS, N * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(c)
    stars = [(0.32, 0.36, 0.22, (255, 255, 255)), (0.70, 0.62, 0.15, (200, 225, 255)), (0.66, 0.24, 0.09, (230, 210, 255)),
             (0.30, 0.76, 0.08, (210, 235, 255))]
    s = N * SS
    for x, y, r, col in stars:
        pts = []
        for i in range(8):
            rad = r if i % 2 == 0 else r * 0.22
            a = i * math.pi / 4
            pts.append(((x + rad * math.cos(a)) * s, (y + rad * math.sin(a)) * s))
        d.polygon(pts, fill=col + (255,))
    return c.resize((N, N), Image.LANCZOS).filter(ImageFilter.GaussianBlur(0.6))


def _plain(alpha: int) -> Image.Image:
    return Image.new("RGBA", (64, 64), (255, 255, 255, alpha))


def texture(name: str) -> Image.Image:
    if name == "coin":
        return _coin_texture()
    if name == "orb":
        return _plain(255)
    if name == "bubble":
        return _plain(110)
    if name in FIRE:
        return _flame(FIRE.index(name) + 3)
    if name in ICE:
        return _ice()
    if name == "galaxy":
        return _galaxy()
    if name == "galaxy_stars":
        return _twinkles()
    if name.startswith("footprint"):
        return _footprint(mirror=name.endswith("_r"))
    c = _Canvas()
    {"star": _star, "heart": _heart, "skull": _skull, "lightning": _lightning, "snowflake": _snowflake,
     "note": _note}[name](c)
    return c.result()


# ---------------------------------------------------------------- meshes

def _tri(material, a, b, c):
    """One SMD triangle; each corner = (pos, normal, uv) with uv in image space (v down)."""
    lines = [material]
    for p, n, (u, v) in (a, b, c):
        lines.append(f"0 {p[0]:.4f} {p[1]:.4f} {p[2]:.4f} {n[0]:.4f} {n[1]:.4f} {n[2]:.4f} {u:.4f} {1 - v:.4f} 1 0 1")
    return lines


def _quad(material, corners, normal):
    """corners: 4 x (pos, uv) counter-clockwise around normal."""
    v = [(p, normal, uv) for p, uv in corners]
    return _tri(material, v[0], v[1], v[2]) + _tri(material, v[0], v[2], v[3])


def _flat(material, length, width):
    hl, hw = length / 2, width / 2
    return _quad(material, [((-hl, hw, 0), (0, 0)), ((-hl, -hw, 0), (0, 1)), ((hl, -hw, 0), (1, 1)), ((hl, hw, 0), (1, 0))],
                 (0, 0, 1))


def _cards(material, size):
    h = size / 2
    return (_quad(material, [((0, h, -h), (0, 1)), ((0, -h, -h), (1, 1)), ((0, -h, h), (1, 0)), ((0, h, h), (0, 0))], (1, 0, 0))
            + _quad(material, [((-h, 0, -h), (0, 1)), ((h, 0, -h), (1, 1)), ((h, 0, h), (1, 0)), ((-h, 0, h), (0, 0))], (0, -1, 0)))


def _coin(material, radius=2.6, thickness=0.7, segments=20):
    """Standing on its edge: faces point along +X / -X."""
    out = []
    h = thickness / 2
    ring = [(math.cos(2 * math.pi * i / segments), math.sin(2 * math.pi * i / segments)) for i in range(segments)]
    edge_uv = (0.01, 0.01)
    for i in range(segments):
        (y0, z0), (y1, z1) = ring[i], ring[(i + 1) % segments]
        p0, p1 = (radius * y0, radius * z0), (radius * y1, radius * z1)
        uv = lambda y, z: (0.5 + 0.46 * y, 0.5 - 0.46 * z)  # noqa: E731
        out += _tri(material, ((h, 0, 0), (1, 0, 0), (0.5, 0.5)), ((h, *p0), (1, 0, 0), uv(y0, z0)), ((h, *p1), (1, 0, 0), uv(y1, z1)))
        out += _tri(material, ((-h, 0, 0), (-1, 0, 0), (0.5, 0.5)), ((-h, *p1), (-1, 0, 0), uv(-y1, z1)), ((-h, *p0), (-1, 0, 0), uv(-y0, z0)))
        n0, n1 = (0, y0, z0), (0, y1, z1)
        out += _tri(material, ((-h, *p0), n0, edge_uv), ((h, *p0), n0, edge_uv), ((h, *p1), n1, edge_uv))
        out += _tri(material, ((-h, *p0), n0, edge_uv), ((h, *p1), n1, edge_uv), ((-h, *p1), n1, edge_uv))
    return out


def _sphere(material, radius, rings=10, segments=16):
    out = []

    def pt(i, j):
        th, ph = math.pi * i / rings, 2 * math.pi * j / segments
        n = (math.sin(th) * math.cos(ph), math.sin(th) * math.sin(ph), math.cos(th))
        return (tuple(radius * x for x in n), n, (j / segments, i / rings))

    for i in range(rings):
        for j in range(segments):
            a, b, c, d = pt(i, j), pt(i + 1, j), pt(i + 1, j + 1), pt(i, j + 1)
            if i > 0:
                out += _tri(material, a, b, d)
            if i < rings - 1:
                out += _tri(material, d, b, c)
    return out


def _shard(material, height, radius, tilt_x=0.0, tilt_y=0.0, offset=(0.0, 0.0)):
    """Hexagonal ice crystal standing on the floor (z = 0 at its base), flat-shaded facets, optionally tilted.
    v follows height (0 at the tip, 1 at the base) so the texture's frost sits low and the tip stays clear."""
    def place(p):
        x, y, z = p
        x, z = x * math.cos(tilt_x) + z * math.sin(tilt_x), -x * math.sin(tilt_x) + z * math.cos(tilt_x)
        y, z = y * math.cos(tilt_y) - z * math.sin(tilt_y), y * math.sin(tilt_y) + z * math.cos(tilt_y)
        return (x + offset[0], y + offset[1], z)

    ring_z = height * 0.72
    low = [((radius * math.cos(i * math.pi / 3), radius * math.sin(i * math.pi / 3), ring_z * 0.15), i / 6) for i in range(6)]
    high = [((radius * 0.85 * math.cos(i * math.pi / 3), radius * 0.85 * math.sin(i * math.pi / 3), ring_z), i / 6) for i in range(6)]
    tip, base = (0.0, 0.0, height), (0.0, 0.0, 0.0)
    out = []

    def v(p, u):
        return place(p), (u, 1 - p[2] / height)

    def face(a, b, c):
        (pa, ua), (pb, ub), (pc, uc) = a, b, c
        n = _normal(pa, pb, pc)
        out.extend(_tri(material, (pa, n, ua), (pb, n, ub), (pc, n, uc)))

    for i in range(6):
        j = (i + 1) % 6
        uj = (i + 1) / 6
        face(v(low[i][0], low[i][1]), v(low[j][0], uj), v(high[j][0], uj))
        face(v(low[i][0], low[i][1]), v(high[j][0], uj), v(high[i][0], high[i][1]))
        face(v(high[i][0], high[i][1]), v(high[j][0], uj), v(tip, (low[i][1] + uj) / 2))
        face(v(low[j][0], uj), v(low[i][0], low[i][1]), v(base, (low[i][1] + uj) / 2))
    return out


def _normal(a, b, c):
    u = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
    n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
    length = math.sqrt(sum(x * x for x in n)) or 1.0
    return tuple(x / length for x in n)


def mesh(name: str, material: str) -> list:
    if name in FIRE:
        return _cards(material, 9.0)
    if name == "ice_1":
        return _shard(material, 9.0, 1.4, 0.12, -0.08)
    if name == "ice_2":
        return (_shard(material, 6.5, 1.1, 0.25, 0.1, (0.8, 0.5)) + _shard(material, 4.5, 0.9, -0.35, 0.2, (-1.2, -0.4))
                + _shard(material, 3.5, 0.8, 0.1, -0.4, (0.2, -1.4)))
    if name == "ice_3":
        out = []
        for k, (hgt, tx, ty, ox, oy) in enumerate([(4.0, 0.3, 0.0, 2.2, 0.4), (3.0, -0.4, 0.2, -2.0, 1.0), (5.0, 0.1, -0.3, 0.2, -2.1),
                                                   (2.5, 0.5, 0.5, -1.0, -1.5), (3.5, -0.2, -0.5, 1.2, 2.2)]):
            out += _shard(material, hgt, 0.7 + 0.05 * k, tx, ty, (ox, oy))
        return out
    if name in GALAXY:
        return _cards(material, 9.0 if name == "galaxy" else 7.0)
    if name.startswith("footprint"):
        return _flat(material, 11.0, 5.5)
    if name in CARDS:
        return _cards(material, 8.0)
    if name == "coin":
        return _coin(material)
    if name == "orb":
        return _sphere(material, 2.6)
    return _sphere(material, 3.2)  # bubble


def _smd(tris: list) -> str:
    head = ["version 1", "nodes", '0 "root" -1', "end", "skeleton", "time 0", "0 0 0 0 0 0 0", "end", "triangles"]
    return "\n".join(head + tris + ["end"]) + "\n"


def _vmat(name: str, color_rel: str, mask_rel) -> str:
    lines = ['\tshader "csgo_complex.vfx"']
    if mask_rel:
        lines += ["\tF_TRANSLUCENT 1", f'\tTextureTranslucency "{mask_rel}"']
    if name in FLAT or name in CARDS or name in FIRE or name in GALAXY:
        lines.append("\tF_RENDER_BACKFACES 1")
    lines.append(f'\tTextureColor "{color_rel}"')
    rough = {"coin": 0.25, "orb": 0.2, "bubble": 0.1, "ice_1": 0.05, "ice_2": 0.05}.get(name, 0.6)
    lines.append(f'\tTextureRoughness "[{rough:.6f} {rough:.6f} {rough:.6f} 0.000000]"')
    return ("// SWBS Jailbreak trail piece - scripts/workshop/trails.py (white: the plugin tints it)\n"
            "Layer0\n{\n" + "\n".join(lines) + "\n}\n")


def convert_all(addon: Path, log=print) -> list:
    """Writes every trail piece into the addon content folder; returns the .vmdl paths (relative to the addon)."""
    made = []
    mat_dir = "materials/swbs/trails"
    (addon / mat_dir).mkdir(parents=True, exist_ok=True)
    (addon / "models/swbs/trails").mkdir(parents=True, exist_ok=True)
    for name in names():
        img = texture(name)
        color_rel = f"{mat_dir}/{name}.png"
        img.convert("RGB").save(addon / color_rel)
        mask_rel = None
        if name not in ("coin", "orb"):
            mask_rel = f"{mat_dir}/{name}_trans.png"
            img.getchannel("A").save(addon / mask_rel)
        vmat_rel = f"{mat_dir}/{name}.vmat"
        (addon / vmat_rel).write_text(_vmat(name, color_rel, mask_rel), encoding="ascii")
        smd_name = f"swbs_trail_{name}"
        smd_rel = f"models/swbs/trails/{name}.smd"
        (addon / smd_rel).write_text(_smd(mesh(name, smd_name)), encoding="ascii")
        remaps = [(smd_name, vmat_rel), (smd_name + ".vmat", vmat_rel)]
        vmdl_rel = model_path(name)
        (addon / vmdl_rel).write_text(_vmdl(name, smd_rel, remaps), encoding="ascii")
        made.append(vmdl_rel)
    log(f"  models/swbs/trails: {len(made)} trail pieces ({', '.join(names())})")
    return made
