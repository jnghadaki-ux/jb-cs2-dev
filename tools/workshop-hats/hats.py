"""
Generates the SWBS store hats for the workshop pack: simple low-poly meshes (SMD, one "root" bone, Z up, +X forward,
origin at the top of the head) with one shared white material that the plugin tints per store item.

usage: python hats.py <output addon folder>      (writes models/swbs/hats/*.vmdl|.smd and materials/swbs/hats/*)
"""
import math
import os
import struct
import sys
import zlib

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out'
MAT = 'swbs_hat_white'
VMAT = 'materials/swbs/hats/hat_white.vmat'
PNG = 'materials/swbs/hats/hat_white.png'


class Mesh:
    def __init__(self):
        self.tris = []  # ((p, n, uv) x3)

    def tri(self, a, b, c, na=None, nb=None, nc=None, uv=((0, 0), (1, 0), (0, 1))):
        if na is None:
            n = norm(cross(sub(b, a), sub(c, a)))
            na = nb = nc = n
        self.tris.append(((a, na, uv[0]), (b, nb, uv[1]), (c, nc, uv[2])))

    def quad(self, a, b, c, d, na=None, nb=None, nc=None, nd=None):
        self.tri(a, b, c, na, nb, nc)
        self.tri(a, c, d, na, nc, nd)


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    l = math.sqrt(a[0] ** 2 + a[1] ** 2 + a[2] ** 2) or 1.0
    return (a[0] / l, a[1] / l, a[2] / l)


def ring(r, z, i, n): return (r * math.cos(2 * math.pi * i / n), r * math.sin(2 * math.pi * i / n), z)


def frustum(m, r0, r1, z0, z1, n=24):
    """Side wall from radius r0 at z0 to r1 at z1 (cylinder when r0 == r1, cone when r1 == 0)."""
    for i in range(n):
        a0, a1 = ring(r0, z0, i, n), ring(r0, z0, i + 1, n)
        b0, b1 = ring(r1, z1, i, n), ring(r1, z1, i + 1, n)
        n0 = norm((math.cos(2 * math.pi * i / n), math.sin(2 * math.pi * i / n), (r0 - r1) / max(0.01, z1 - z0)))
        n1 = norm((math.cos(2 * math.pi * (i + 1) / n), math.sin(2 * math.pi * (i + 1) / n), (r0 - r1) / max(0.01, z1 - z0)))
        m.quad(a0, a1, b1, b0, n0, n1, n1, n0)


def disk(m, r, z, up=True, n=24, r_inner=0.0):
    nz = (0, 0, 1 if up else -1)
    for i in range(n):
        o0, o1 = ring(r, z, i, n), ring(r, z, i + 1, n)
        if r_inner > 0:
            i0, i1 = ring(r_inner, z, i, n), ring(r_inner, z, i + 1, n)
            m.quad(i0, i1, o1, o0, nz, nz, nz, nz) if up else m.quad(i0, o0, o1, i1, nz, nz, nz, nz)
        else:
            c = (0, 0, z)
            m.tri(c, o0, o1, nz, nz, nz) if up else m.tri(c, o1, o0, nz, nz, nz)


def torus(m, R, r, z, n=28, k=10):
    def p(i, j):
        u, v = 2 * math.pi * i / n, 2 * math.pi * j / k
        return ((R + r * math.cos(v)) * math.cos(u), (R + r * math.cos(v)) * math.sin(u), z + r * math.sin(v))

    def nn(i, j):
        u, v = 2 * math.pi * i / n, 2 * math.pi * j / k
        return (math.cos(v) * math.cos(u), math.cos(v) * math.sin(u), math.sin(v))
    for i in range(n):
        for j in range(k):
            m.quad(p(i, j), p(i + 1, j), p(i + 1, j + 1), p(i, j + 1), nn(i, j), nn(i + 1, j), nn(i + 1, j + 1), nn(i, j + 1))


def dome(m, r, z, h, n=24, k=8):
    """Half ellipsoid: radius r at z, top at z + h."""
    def p(i, j):
        u, v = 2 * math.pi * i / n, (math.pi / 2) * j / k
        return (r * math.cos(v) * math.cos(u), r * math.cos(v) * math.sin(u), z + h * math.sin(v))

    def nn(i, j):
        u, v = 2 * math.pi * i / n, (math.pi / 2) * j / k
        return norm((math.cos(v) * math.cos(u) / r, math.cos(v) * math.sin(u) / r, math.sin(v) / h))
    for i in range(n):
        for j in range(k):
            m.quad(p(i, j), p(i + 1, j), p(i + 1, j + 1), p(i, j + 1), nn(i, j), nn(i + 1, j), nn(i + 1, j + 1), nn(i, j + 1))


def ball(m, r, c, n=12, k=8):
    for i in range(n):
        for j in range(k):
            def p(ii, jj):
                u, v = 2 * math.pi * ii / n, math.pi * jj / k - math.pi / 2
                return (c[0] + r * math.cos(v) * math.cos(u), c[1] + r * math.cos(v) * math.sin(u), c[2] + r * math.sin(v))
            m.quad(p(i, j), p(i + 1, j), p(i + 1, j + 1), p(i, j + 1),
                   *(norm(sub(q, c)) for q in (p(i, j), p(i + 1, j), p(i + 1, j + 1), p(i, j + 1))))


# ------------------------------------------------------------------ the hats (head top at z = 0, head radius ~4.5)

def top_hat():
    m = Mesh()
    disk(m, 7.2, -0.6, up=False); disk(m, 7.2, -0.2, up=True, r_inner=4.3); frustum(m, 7.2, 7.2, -0.6, -0.2)
    frustum(m, 4.3, 4.6, -0.2, 7.8); disk(m, 4.6, 7.8, up=True)
    frustum(m, 4.4, 4.45, 0.4, 1.6, n=24)  # band (slightly proud of the crown)
    return m


def crown():
    m = Mesh()
    frustum(m, 4.7, 4.9, -0.4, 1.8); frustum(m, 4.4, 4.6, -0.4, 1.8)
    n = 8
    for i in range(n):
        a0, a1, mid = 2 * math.pi * i / n, 2 * math.pi * (i + 1) / n, 2 * math.pi * (i + 0.5) / n
        b0 = (4.9 * math.cos(a0), 4.9 * math.sin(a0), 1.8)
        b1 = (4.9 * math.cos(a1), 4.9 * math.sin(a1), 1.8)
        tip = (5.1 * math.cos(mid), 5.1 * math.sin(mid), 5.0)
        m.tri(b0, b1, tip)
        ball(m, 0.45, tip, n=8, k=6)
    disk(m, 4.9, 1.8, up=True, r_inner=4.4)
    return m


def party_hat():
    m = Mesh()
    frustum(m, 4.4, 0.0, -0.3, 10.0, n=28); disk(m, 4.4, -0.3, up=False, n=28)
    ball(m, 1.1, (0, 0, 10.4))
    return m


def halo():
    m = Mesh()
    torus(m, 4.6, 0.45, 5.0)
    return m


def beanie():
    m = Mesh()
    dome(m, 5.0, -0.8, 6.0); torus(m, 5.0, 0.9, -0.6)
    ball(m, 1.2, (0, 0, 5.6))
    return m


def cowboy_hat():
    m = Mesh()
    # brim: flat centre, edges rolled up at the sides
    n = 32
    for i in range(n):
        def p(r, ii, base):
            a = 2 * math.pi * ii / n
            lift = 1.6 * abs(math.sin(a)) ** 2 * max(0.0, (r - 5.5) / 4.0)
            return (r * math.cos(a), r * math.sin(a), base + lift)
        for (r0, r1) in ((4.3, 6.5), (6.5, 9.5)):
            m.quad(p(r0, i, -0.2), p(r0, i + 1, -0.2), p(r1, i + 1, -0.2), p(r1, i, -0.2))
    frustum(m, 4.3, 3.9, -0.2, 5.0, n=32)
    dome(m, 3.9, 5.0, 1.2, n=32)
    torus(m, 4.25, 0.35, 0.5, n=32, k=6)
    return m


HATS = {
    'top_hat': top_hat, 'crown': crown, 'party_hat': party_hat, 'halo': halo, 'beanie': beanie, 'cowboy_hat': cowboy_hat
}


def write_smd(path, mesh):
    with open(path, 'w') as f:
        f.write('version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\ntriangles\n')
        for t in mesh.tris:
            f.write(MAT + '\n')
            for (p, n, uv) in t:
                f.write(f'0 {p[0]:.4f} {p[1]:.4f} {p[2]:.4f} {n[0]:.4f} {n[1]:.4f} {n[2]:.4f} {uv[0]:.4f} {uv[1]:.4f} 1 0 1\n')
        f.write('end\n')


VMDL = '''<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->
{
	rootNode = 
	{
		_class = "RootNode"
		children = 
		[
			{
				_class = "MaterialGroupList"
				children = 
				[
					{
						_class = "DefaultMaterialGroup"
						remaps = 
						[
							{
								from = "%MAT%"
								to = "%VMAT%"
							},
							{
								from = "%MAT%.vmat"
								to = "%VMAT%"
							},
						]
						use_global_default = false
						global_default_material = ""
					},
				]
			},
			{
				_class = "RenderMeshList"
				children = 
				[
					{
						_class = "RenderMeshFile"
						filename = "%SMD%"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 1.0
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter = 
						{
							exclude_by_default = false
							exception_list = [  ]
						}
					},
				]
			},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}
}
'''


def write_png(path, size=64, value=235):
    raw = b''.join(b'\x00' + bytes([value, value, value]) * size for _ in range(size))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
    open(path, 'wb').write(png)


def main():
    os.makedirs(os.path.join(OUT, 'models/swbs/hats'), exist_ok=True)
    os.makedirs(os.path.join(OUT, 'materials/swbs/hats'), exist_ok=True)
    write_png(os.path.join(OUT, PNG))
    open(os.path.join(OUT, VMAT), 'w').write(
        '// SWBS Jailbreak store hat - tools/workshop-hats/hats.py (white: the plugin tints it per store item)\n'
        'Layer0\n{\n\tshader "csgo_complex.vfx"\n\tF_RENDER_BACKFACES 1\n'
        f'\tTextureColor "{PNG}"\n\tTextureRoughness "[0.450000 0.450000 0.450000 0.000000]"\n}}\n')
    for name, build in HATS.items():
        mesh = build()
        smd = f'models/swbs/hats/{name}.smd'
        write_smd(os.path.join(OUT, smd), mesh)
        open(os.path.join(OUT, f'models/swbs/hats/{name}.vmdl'), 'w').write(
            VMDL.replace('%MAT%', MAT).replace('%VMAT%', VMAT).replace('%SMD%', smd))
        zs = [p[2] for t in mesh.tris for (p, _, _) in t]
        rs = [math.hypot(p[0], p[1]) for t in mesh.tris for (p, _, _) in t]
        print(f'{name}: {len(mesh.tris)} triangles, height {min(zs):.1f}..{max(zs):.1f}, radius {max(rs):.1f}')


main()
