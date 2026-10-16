"""
Generates the face and back equipment of SWBS store pack 2 (the SMPlugins Item_Equipment "face" and "torso" slots, lost
in the CS2 port): simple low-poly meshes (SMD, one "root" bone, Z up, +X forward, +Y left) with one shared white
material that the plugin tints per store item, like the hats (tools/workshop-hats).

  Face items: origin between the eyes on the face surface (the plugin puts it in front of the eye position).
  Back items: origin on the upper back, items reach backwards (-X) (the plugin parents them to the "c4" spine attachment).

usage: python equipment.py <output addon folder>     (writes models/swbs/store2/{face,back}/*.vmdl|.smd and materials/swbs/store2/*)
"""
import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'workshop-hats'))
from hats import Mesh, VMDL, ball, cross, disk, frustum, norm, sub, torus, write_png  # noqa: E402

MAT = 'swbs_store2_white'
VMAT = 'materials/swbs/store2/equip_white.vmat'
PNG = 'materials/swbs/store2/equip_white.png'
MODEL_ROOT = 'models/swbs/store2'


# ------------------------------------------------------------------ helpers

def rot(axis, deg):
    """Rotation matrix about 'x', 'y' or 'z' (right-handed)."""
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    if axis == 'x':
        return ((1, 0, 0), (0, c, -s), (0, s, c))
    if axis == 'y':
        return ((c, 0, s), (0, 1, 0), (-s, 0, c))
    return ((c, -s, 0), (s, c, 0), (0, 0, 1))


def mul(m, v): return tuple(m[i][0] * v[0] + m[i][1] * v[1] + m[i][2] * v[2] for i in range(3))


def mmul(a, b): return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))


def place(dst, src, R=((1, 0, 0), (0, 1, 0), (0, 0, 1)), t=(0, 0, 0), scale=(1, 1, 1)):
    """Appends src to dst: scaled (per axis), rotated by R, moved by t. Normals follow the inverse-transpose."""
    flip = scale[0] * scale[1] * scale[2] < 0
    for tri in src.tris:
        out = []
        for (p, n, uv) in tri:
            q = mul(R, (p[0] * scale[0], p[1] * scale[1], p[2] * scale[2]))
            nn = norm(mul(R, (n[0] / scale[0], n[1] / scale[1], n[2] / scale[2])))
            out.append(((q[0] + t[0], q[1] + t[1], q[2] + t[2]), nn, uv))
        dst.tris.append(tuple(reversed(out)) if flip else tuple(out))
    return dst


def box(m, c, size):
    """Axis-aligned box centred on c."""
    hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2
    v = [(c[0] + sx * hx, c[1] + sy * hy, c[2] + sz * hz) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    # index = (sx>0)*4 + (sy>0)*2 + (sz>0)
    faces = [(4, 6, 7, 5), (0, 1, 3, 2), (2, 3, 7, 6), (0, 4, 5, 1), (1, 5, 7, 3), (0, 2, 6, 4)]
    for a, b, cc, d in faces:
        m.quad(v[a], v[b], v[cc], v[d])
    return m


def cylinder(r0, r1, h, n=16, caps=True):
    """Along +Z from 0 to h (cone when r1 == 0)."""
    m = Mesh()
    frustum(m, r0, r1, 0, h, n=n)
    if caps:
        disk(m, r0, 0, up=False, n=n)
        if r1 > 0:
            disk(m, r1, h, up=True, n=n)
    return m


def unit_ball(n=12, k=8):
    m = Mesh()
    ball(m, 1.0, (0, 0, 0), n=n, k=k)
    return m


def look(d):
    """Rotation taking +Z to direction d."""
    z = norm(d)
    up = (0, 0, 1) if abs(z[2]) < 0.95 else (1, 0, 0)
    x = norm(cross(up, z))
    y = cross(z, x)
    return ((x[0], y[0], z[0]), (x[1], y[1], z[1]), (x[2], y[2], z[2]))


def rod(m, a, b, r0, r1=None, n=10):
    """A cylinder (or tapered rod) from point a to point b."""
    d = sub(b, a)
    length = math.sqrt(d[0] ** 2 + d[1] ** 2 + d[2] ** 2)
    place(m, cylinder(r0, r0 if r1 is None else r1, length, n=n), look(d), a)


def ellipsoid(m, c, radii, R=((1, 0, 0), (0, 1, 0), (0, 0, 1)), n=12, k=8):
    place(m, _ball(n, k), R, c, radii)


_BALLS = {}


def _ball(n, k):
    if (n, k) not in _BALLS:
        _BALLS[(n, k)] = unit_ball(n, k)
    return _BALLS[(n, k)]


def plate(m, pts, thickness, axis=(1, 0, 0)):
    """A flat polygon (convex, points in order) given thickness along axis: both faces and the rim."""
    h = (axis[0] * thickness / 2, axis[1] * thickness / 2, axis[2] * thickness / 2)
    front = [(p[0] + h[0], p[1] + h[1], p[2] + h[2]) for p in pts]
    back = [(p[0] - h[0], p[1] - h[1], p[2] - h[2]) for p in pts]
    for i in range(1, len(pts) - 1):
        m.tri(front[0], front[i], front[i + 1])
        m.tri(back[0], back[i + 1], back[i])
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        m.quad(front[i], back[i], back[j], front[j])


def oval(cy, cz, ry, rz, n=20):
    """Points of an ellipse in the YZ plane (counter-clockwise seen from +X)."""
    return [(0.0, cy + ry * math.cos(2 * math.pi * i / n), cz + rz * math.sin(2 * math.pi * i / n)) for i in range(n)]


def head_band(m, z, height, r=4.7, cx=-4.2, a0=-115, a1=115, n=28, tilt=0.0, holes=()):
    """A band around the head (cylinder of radius r centred at x = cx), from angle a0 to a1 (0 = straight ahead).
    holes: (angle, z, half-angle, half-height) ellipses left open (mask eye holes). tilt raises the band towards +Y."""
    rows = 12 if holes else 1
    n = n * 3 if holes else n
    for i in range(n):
        for j in range(rows):
            u0, u1 = a0 + (a1 - a0) * i / n, a0 + (a1 - a0) * (i + 1) / n
            v0, v1 = z - height / 2 + height * j / rows, z - height / 2 + height * (j + 1) / rows
            uc, vc = (u0 + u1) / 2, (v0 + v1) / 2
            if any(((uc - ha) / hw) ** 2 + ((vc - hz) / hh) ** 2 < 1 for (ha, hz, hw, hh) in holes):
                continue

            def p(u, v, rr=r):
                a = math.radians(u)
                return (cx + rr * math.cos(a), rr * math.sin(a), v + tilt * math.sin(a))

            def nn(u):
                a = math.radians(u)
                return (math.cos(a), math.sin(a), 0)
            m.quad(p(u0, v0), p(u1, v0), p(u1, v1), p(u0, v1), nn(u0), nn(u1), nn(u1), nn(u0))


# ------------------------------------------------------------------ face (origin between the eyes, on the face)

def sunglasses():
    m = Mesh()
    for side in (1, -1):
        lens = [(0.0, side * 1.7 + 1.25 * math.cos(a), -0.15 + 0.75 * math.sin(a) - (0.25 if math.sin(a) < 0 else 0))
                for a in (2 * math.pi * i / 20 for i in range(20))]
        plate(m, lens, 0.25)
        rod(m, (-0.1, side * 2.95, 0.35), (-4.6, side * 3.9, 0.45), 0.13, n=6)  # temple arm
    rod(m, (0.05, -0.5, 0.25), (0.05, 0.5, 0.25), 0.14, n=6)  # bridge
    return m


def round_glasses():
    m = Mesh()
    for side in (1, -1):
        place(m, _torus(1.05, 0.13), rot('y', 90), (0.05, side * 1.65, 0))
        rod(m, (-0.05, side * 2.7, 0.15), (-4.6, side * 3.9, 0.35), 0.11, n=6)
    # curved bridge
    pts = [(0.15, -0.6 + 1.2 * i / 6, 0.15 + 0.25 * math.sin(math.pi * i / 6)) for i in range(7)]
    for a, b in zip(pts, pts[1:]):
        rod(m, a, b, 0.11, n=6)
    return m


def _torus(R, r):
    m = Mesh()
    torus(m, R, r, 0, n=24, k=8)
    return m


def bandit_mask():
    m = Mesh()
    head_band(m, 0.1, 2.0, r=4.75, holes=((22, 0.0, 12, 0.75), (-22, 0.0, 12, 0.75)))
    # two knot tails at the back
    for side in (1, -1):
        rod(m, (-8.9, 0, 0.2), (-10.6, side * 1.0, -1.8), 0.35, 0.15, n=6)
    return m


def mustache():
    m = Mesh()
    for side in (1, -1):
        # a chain of shrinking blobs curling up at the end
        for i in range(9):
            t = i / 8
            y = side * (0.25 + 2.1 * t)
            z = -2.55 + 1.3 * t ** 2.4
            x = 0.55 - 0.6 * t
            r = 0.55 * (1 - 0.7 * t) + 0.1
            ellipsoid(m, (x, y, z), (r * 0.9, r * 1.3, r), n=10, k=6)
    return m


def clown_nose():
    m = Mesh()
    ellipsoid(m, (1.1, 0, -1.25), (1.05, 1.05, 1.05), n=16, k=10)
    return m


def eyepatch():
    m = Mesh()
    plate(m, [(0.25, y, z) for (_, y, z) in oval(-1.65, 0.0, 1.15, 0.95)], 0.25)
    head_band(m, 0.15, 0.35, r=4.72, a0=-30, a1=330, n=40, tilt=1.4)
    return m


# ------------------------------------------------------------------ back (origin on the upper back, items reach -X)

def _feather_wing(m, side):
    root = (-1.6, side * 1.6, 1.5)
    for i in range(8):
        t = i / 7
        a = math.radians(-20 + 95 * t)  # from slightly down-and-out to up-and-out
        length = 9.0 + 9.0 * math.sin(math.pi * (0.35 + 0.55 * t))
        d = (-0.25, side * math.cos(a), math.sin(a))
        dn = norm(d)
        c = (root[0] + dn[0] * length * 0.55, root[1] + dn[1] * length * 0.55, root[2] + dn[2] * length * 0.55)
        R = mmul(look(d), rot('z', 90))
        ellipsoid(m, c, (1.5, 0.35, length * 0.55), R, n=10, k=6)
    ellipsoid(m, (root[0] - 0.2, side * 2.6, root[2] + 1.0), (1.2, 2.2, 2.0), n=10, k=6)


def angel_wings():
    m = Mesh()
    for side in (1, -1):
        _feather_wing(m, side)
    return m


def bat_wings():
    m = Mesh()
    for side in (1, -1):
        root = (-2.0, side * 1.4, 1.0)
        elbow = (-3.2, side * 7.5, 9.0)
        tips = [(-4.0, side * 16.0, 10.5), (-4.2, side * 17.5, 4.0), (-4.0, side * 15.0, -2.5), (-3.4, side * 9.5, -5.5)]
        rod(m, root, elbow, 0.55, 0.4, n=8)
        ellipsoid(m, elbow, (0.6, 0.6, 0.6), n=8, k=6)
        rod(m, elbow, (-3.6, side * 9.0, 13.0), 0.35, 0.05, n=6)  # claw
        for tip in tips:
            rod(m, elbow, tip, 0.3, 0.08, n=6)
        # membrane: elbow-fan between neighbouring fingers, with a scalloped (inset) edge
        chain = [root, tips[3], tips[2], tips[1], tips[0]]
        for a, b in zip(tips, tips[1:]):
            mid = tuple((a[k] + b[k]) / 2 for k in range(3))
            inset = tuple(mid[k] + (elbow[k] - mid[k]) * 0.18 for k in range(3))
            m.tri(elbow, a, inset)
            m.tri(elbow, inset, b)
        m.tri(root, elbow, chain[1])
    return m


def jetpack():
    m = Mesh()
    box(m, (-1.2, 0, -1.0), (1.6, 7.5, 11.0))  # back plate
    for side in (1, -1):
        y = side * 2.3
        place(m, cylinder(2.0, 2.0, 10.0, n=16, caps=False), t=(-3.9, y, -6.0))
        place(m, _dome(2.0, 1.4), t=(-3.9, y, 4.0))
        place(m, cylinder(1.3, 1.8, 2.2, n=14), rot('x', 180), (-3.9, y, -6.0))  # nozzle (points down)
        place(m, _torus(2.05, 0.25), t=(-3.9, y, -3.0))  # tank bands
        place(m, _torus(2.05, 0.25), t=(-3.9, y, 2.0))
    rod(m, (-3.9, -2.3, 3.0), (-3.9, 2.3, 3.0), 0.35, n=8)  # cross pipe
    return m


def _dome(r, h):
    m = Mesh()
    from hats import dome
    dome(m, r, 0, h, n=16, k=6)
    return m


def backpack():
    m = Mesh()
    box(m, (-3.3, 0, -2.0), (5.2, 9.5, 12.5))  # main bag
    box(m, (-6.1, 0, -4.4), (1.6, 7.0, 5.5))  # front pocket
    box(m, (-3.5, 0, 4.6), (5.6, 9.9, 1.2))  # top flap
    place(m, _torus(1.0, 0.25), rot('x', 90), (-3.3, 0, 5.8))  # carry loop
    for side in (1, -1):  # straps over the shoulders
        pts = [(-0.7 + 2.6 * math.sin(math.pi * i / 8), side * 3.2, 3.4 + 2.2 * math.sin(math.pi * i / 8)) for i in range(5)]
        for a, b in zip(pts, pts[1:]):
            rod(m, a, b, 0.45, n=6)
    return m


def katana():
    m = Mesh()
    k = Mesh()
    # built along +Z (handle down at z=0), then laid diagonally across the back
    box(k, (0, 0, 4.0), (0.9, 0.9, 8.0))  # handle
    place(k, cylinder(1.6, 1.6, 0.4, n=12), t=(0, 0, 8.0))  # guard
    box(k, (0, 0, 21.0), (0.25, 1.2, 25.6))  # blade (in its saya: drawn as a sheath)
    place(k, cylinder(0.62, 0.0, 1.4, n=8, caps=False), rot('z', 45), (0, 0, 33.8))
    place(m, k, rot('x', 216.87), (-3.2, -5.5, 9.0))  # handle over the right shoulder, tip at the left hip
    rod(m, (-0.7, -4.5, 3.5), (-0.7, 4.5, -5.0), 0.3, n=6)  # strap across the back
    return m


FACE = {
    'sunglasses': sunglasses, 'round_glasses': round_glasses, 'bandit_mask': bandit_mask,
    'mustache': mustache, 'clown_nose': clown_nose, 'eyepatch': eyepatch,
}
BACK = {
    'angel_wings': angel_wings, 'bat_wings': bat_wings, 'jetpack': jetpack, 'backpack': backpack, 'katana': katana,
}


def write_smd(path, mesh):
    with open(path, 'w') as f:
        f.write('version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\ntriangles\n')
        for t in mesh.tris:
            f.write(MAT + '\n')
            for (p, n, uv) in t:
                f.write(f'0 {p[0]:.4f} {p[1]:.4f} {p[2]:.4f} {n[0]:.4f} {n[1]:.4f} {n[2]:.4f} {uv[0]:.4f} {uv[1]:.4f} 1 0 1\n')
        f.write('end\n')


def all_models():
    return [('face', n, b) for n, b in FACE.items()] + [('back', n, b) for n, b in BACK.items()]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else 'out'
    os.makedirs(os.path.join(out, os.path.dirname(VMAT)), exist_ok=True)
    write_png(os.path.join(out, PNG))
    open(os.path.join(out, VMAT), 'w').write(
        '// SWBS store pack 2 equipment - tools/store-pack-2/equipment.py (white: the plugin tints it per store item)\n'
        'Layer0\n{\n\tshader "csgo_complex.vfx"\n\tF_RENDER_BACKFACES 1\n'
        f'\tTextureColor "{PNG}"\n\tTextureRoughness "[0.450000 0.450000 0.450000 0.000000]"\n}}\n')
    for slot, name, build in all_models():
        mesh = build()
        folder = f'{MODEL_ROOT}/{slot}'
        os.makedirs(os.path.join(out, folder), exist_ok=True)
        smd = f'{folder}/{name}.smd'
        write_smd(os.path.join(out, smd), mesh)
        open(os.path.join(out, f'{folder}/{name}.vmdl'), 'w').write(
            VMDL.replace('%MAT%', MAT).replace('%VMAT%', VMAT).replace('%SMD%', smd))
        xs, ys, zs = ([p[i] for t in mesh.tris for (p, _, _) in t] for i in range(3))
        print(f'{slot}/{name}: {len(mesh.tris)} triangles, x {min(xs):.1f}..{max(xs):.1f}, '
              f'y {min(ys):.1f}..{max(ys):.1f}, z {min(zs):.1f}..{max(zs):.1f}')


if __name__ == '__main__':
    main()
