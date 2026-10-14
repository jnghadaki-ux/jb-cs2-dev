"""
Reader for KOTOR 1 / 2 binary models (Odyssey engine .mdl + .mdx, as exported by Holocron Toolset or KotOR Tool):
the node tree, every mesh's triangles, UVs and texture name, and skin weights, as a player_model.SourceModel.

Layout (all offsets in the .mdl are relative to byte 12, after the file header):
  file header      u32 0, u32 mdl size, u32 mdx size
  geometry header  2 function pointers (they tell KOTOR 1 from KOTOR 2), name[32], root node, node count, ...
  model header     ..., supermodel name, name offsets array (node names)
  node             u16 flags, u16 node number, u16 name index, ..., position, orientation (w x y z), children
  mesh / skin      per-node extras (flags 0x20 mesh, 0x40 skin); vertex data lives in the .mdx
Only what a static bind pose needs is read: no controllers (animations), emitters, lights or walkmeshes.
"""
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path

import player_model

NODE_MESH, NODE_SKIN, NODE_DANGLY, NODE_AABB, NODE_SABER = 0x20, 0x40, 0x100, 0x200, 0x800
K2_FUNCTION_POINTERS = {4285200, 4285872}  # PC / Mac KOTOR 2 geometry header
BASE = 12


def _cstr(data: bytes, off: int, limit: int = 64) -> str:
    raw = data[off:off + limit]
    return raw.split(b"\0", 1)[0].decode("latin-1")


@dataclass
class Node:
    index: int
    name: str
    flags: int
    parent: int
    pos: tuple
    quat: tuple  # (x, y, z, w)
    children: list = field(default_factory=list)
    name_index: int = -1
    world_pos: tuple = (0.0, 0.0, 0.0)
    world_quat: tuple = (0.0, 0.0, 0.0, 1.0)


@dataclass
class Mesh:
    node: int
    texture: str
    render: bool
    vertices: list  # (pos local, normal local, uv)
    triangles: list
    weights: list = None  # per vertex [(name index of the bone node, weight)] for skins (bonemap is by name index)


@dataclass
class KotorModel:
    name: str
    supermodel: str
    nodes: list
    meshes: list


def read_model(mdl_path: Path) -> KotorModel:
    mdl = mdl_path.read_bytes()
    mdx = mdl_path.with_suffix(".mdx").read_bytes()
    d = mdl[BASE:]

    def u32(o): return struct.unpack_from("<I", d, o)[0]

    def arr(o):  # (offset, count)
        off, cnt, _ = struct.unpack_from("<3I", d, o)
        return off, cnt

    fp = u32(0)
    k2 = fp in K2_FUNCTION_POINTERS
    name = _cstr(d, 8, 32)
    root = u32(40)
    # model header follows the 80-byte geometry header
    mh = 80
    supermodel = _cstr(d, mh + 4 + 4 + 12 + 4 + 24 + 4 + 4, 32)
    names_off, names_cnt = arr(mh + 4 + 4 + 12 + 4 + 24 + 4 + 4 + 32 + 4 + 4 + 4 + 4)
    names = [_cstr(d, u32(names_off + 4 * i)) for i in range(names_cnt)]
    mesh_header = 340 if k2 else 332

    nodes = {}
    meshes = []

    def read_node(off, parent):
        flags, number, name_index = struct.unpack_from("<3H", d, off)
        px, py, pz, qw, qx, qy, qz = struct.unpack_from("<7f", d, off + 16)
        ch_off, ch_cnt = arr(off + 44)
        node = Node(number, names[name_index] if name_index < len(names) else f"node{number}", flags, parent,
                    (px, py, pz), (qx, qy, qz, qw), name_index=name_index)
        nodes[number] = node
        m = off + 80
        if flags & NODE_MESH and not flags & (NODE_AABB | NODE_SABER):
            meshes.append(_read_mesh(d, mdx, m, mesh_header, flags, number, k2))
        for i in range(ch_cnt):
            child = u32(ch_off + 4 * i)
            node.children.append(child)
            read_node(child, number)

    read_node(root, -1)

    # world bind pose: positions / orientations down the tree
    def walk(n, wpos, wq):
        node = nodes[n]
        node.world_pos = tuple(a + b for a, b in zip(wpos, _qrot(wq, node.pos)))
        node.world_quat = _qmul(wq, node.quat)
        for x in [x for x in nodes.values() if x.parent == node.index]:
            walk(x.index, node.world_pos, node.world_quat)

    rootnode = next(n for n in nodes.values() if n.parent == -1)
    walk(rootnode.index, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0))
    return KotorModel(name, supermodel, [nodes[k] for k in sorted(nodes)], meshes)


def _read_mesh(d, mdx, m, header_size, flags, node_number, k2):
    def u32(o): return struct.unpack_from("<I", d, o)[0]

    faces_off, faces_cnt, _ = struct.unpack_from("<3I", d, m + 8)
    texture = _cstr(d, m + 88, 32)
    stride = u32(m + 252)
    offs = struct.unpack_from("<11i", d, m + 260)  # vertices, normals, colours, uv1..uv4, tangent1..4
    nverts = struct.unpack_from("<H", d, m + 304)[0]
    render = d[m + 313] != 0
    mdx_start = u32(m + 324 + (8 if k2 else 0))
    verts = []
    for i in range(nverts):
        base = mdx_start + i * stride
        p = struct.unpack_from("<3f", mdx, base + offs[0])
        n = struct.unpack_from("<3f", mdx, base + offs[1]) if offs[1] >= 0 else (0.0, 0.0, 1.0)
        uv = struct.unpack_from("<2f", mdx, base + offs[3]) if offs[3] >= 0 else (0.0, 0.0)
        verts.append((p, n, uv))
    tris = []
    for i in range(faces_cnt):
        a, b, c = struct.unpack_from("<3H", d, faces_off + i * 32 + 26)
        tris.append((a, b, c))
    mesh = Mesh(node_number, texture, render, verts, tris)
    if flags & NODE_SKIN:
        s = m + header_size
        w_off, i_off, bonemap_off, bonemap_cnt = struct.unpack_from("<2i2I", d, s + 12)
        bonemap = [int(x) for x in struct.unpack_from(f"<{bonemap_cnt}f", d, bonemap_off)]
        slot_to_node = {slot: node for node, slot in enumerate(bonemap) if slot >= 0}
        weights = []
        for i in range(nverts):
            base = mdx_start + i * stride
            ws = struct.unpack_from("<4f", mdx, base + w_off)
            bs = struct.unpack_from("<4f", mdx, base + i_off)
            vw = [(slot_to_node.get(int(b), -1), w) for w, b in zip(ws, bs) if w > 0 and b >= 0]
            weights.append(vw)
        mesh.weights = weights
    return mesh


def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def _qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def to_source(model: KotorModel, bone_names: dict, skip_textures=()) -> player_model.SourceModel:
    """KotorModel -> SourceModel in world space. bone_names: KOTOR node name -> CS2-style name used by the retarget
    (e.g. "torso_g" -> "spine_2"); unmapped nodes keep their own names and pass their weights up the tree.
    Rigid (non-skin) meshes are weighted fully to their own node."""
    by_index = {n.index: n for n in model.nodes}
    by_name_index = {n.name_index: n for n in model.nodes}

    def bone(n):
        node = by_index[n]
        return bone_names.get(node.name.lower(), node.name)

    def skin_bone(name_index, mesh_node):
        node = by_name_index.get(name_index) or by_index[mesh_node]
        return bone(node.index)

    bones = {bone(n.index): (bone(n.parent) if n.parent >= 0 else "", n.world_pos) for n in model.nodes}
    vertices, weights, meshes = [], [], []
    for mesh in model.meshes:
        if not mesh.render or not mesh.texture or mesh.texture.lower() in ("null", "") or mesh.texture.lower() in skip_textures:
            continue
        node = by_index[mesh.node]
        first = len(vertices)
        for i, (p, n, uv) in enumerate(mesh.vertices):
            wp = tuple(a + b for a, b in zip(node.world_pos, _qrot(node.world_quat, p)))
            wn = _qrot(node.world_quat, n)
            vertices.append((wp, wn, (uv[0], uv[1])))  # KOTOR v already counts from the top row of the .tpc
            if mesh.weights is not None and mesh.weights[i]:
                weights.append([(skin_bone(b, mesh.node), w) for b, w in mesh.weights[i]])
            else:
                weights.append([(bone(mesh.node), 1.0)])
        meshes.append((mesh.texture.lower(), [(first + a, first + b, first + c) for a, b, c in mesh.triangles]))
    return player_model.SourceModel(bones, vertices, weights, meshes)


# KOTOR humanoid skeleton -> CS2 bone names (the retarget matches by these). Unlisted bones (cloth, face, the
# lower-bicep twist, the upper neck) pass their weights up to the nearest listed parent.
CS2_BONES = {
    "pelvis_g": "pelvis", "torso_g": "spine_0", "torsoupr_g": "spine_2", "necklwr_g": "neck_0", "head_g": "head_0",
    "lthigh_g": "leg_upper_l", "lshin_g": "leg_lower_l", "lfoot_g": "ankle_l", "lfoott_g": "ball_l",
    "rthigh_g": "leg_upper_r", "rshin_g": "leg_lower_r", "rfoot_g": "ankle_r", "rfoott_g": "ball_r",
}
for _s in "lr":
    CS2_BONES.update({
        f"{_s}collar_g": f"clavicle_{_s}", f"{_s}bicep_g": f"arm_upper_{_s}", f"{_s}forearm_g": f"arm_lower_{_s}",
        f"{_s}hand_g": f"hand_{_s}",
        f"{_s}thumbb_g": f"finger_thumb_0_{_s}", f"{_s}thumbt_g": f"finger_thumb_1_{_s}",
    })
    for _k, _f in zip("abcd", ["index", "middle", "ring", "pinky"]):
        CS2_BONES[f"{_s}{_k}fngrb_g"] = f"finger_{_f}_0_{_s}"
        CS2_BONES[f"{_s}{_k}fngrt_g"] = f"finger_{_f}_1_{_s}"
