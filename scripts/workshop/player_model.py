"""
Rebuilds player models for CS2's current player skeleton (the lowercase "_l / _r" skeleton the 2025 animation update
introduced, driven by animation/graphs/worldmodel/worldmodel.vnmgraph).

Input is a skinned model on an older skeleton: a CS:GO .mdl (the SWBS prisoners) or a glTF exported from an older CS2
model (Source 2 Viewer / ValveResourceFormat). Every source bone is matched to a CS2 bone by name, and every vertex is
moved from the source bind pose into the CS2 bind pose (each bone's piece of the mesh is scaled, re-aimed along the bone
and placed on the CS2 bone). The result is an SMD on the full CS2 skeleton plus a .vmdl built from
cs2_player/player_template.vmdl, which carries the hitboxes, attachments, ragdoll, skeleton and animation graphs of a
working 2026 CS2 player model.
"""
import json
import math
import re
import struct
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent / "cs2_player"
TEMPLATE = HERE / "player_template.vmdl"
SKELETON = HERE / "skeleton.json"

# First-person arms for models that have none of their own: CS2's black hard-knuckle gloves.
DEFAULT_GLOVES = "characters/models/shared/arms/glove_hardknuckle/v_glove_hardknuckle_black.vmdl"

# The child a bone points at, where "the child with the biggest subtree" would pick the wrong one.
PRIMARY_CHILD = {
    "spine_3": ["neck_0"],
    "hand_l": ["finger_middle_meta_l", "finger_middle_0_l"],
    "hand_r": ["finger_middle_meta_r", "finger_middle_0_r"],
}


# ---------------------------------------------------------------- math

def _add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def _sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def _mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def _dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def _cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def _len(a): return math.sqrt(_dot(a, a))


def _norm(a):
    n = _len(a)
    return (a[0] / n, a[1] / n, a[2] / n) if n > 1e-9 else (0.0, 0.0, 0.0)


def _mat_vec(m, v):
    return (m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
            m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
            m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2])


def _mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def _transpose(m):
    return [[m[j][i] for j in range(3)] for i in range(3)]


def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def _qmat(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def _rotation_between(a, b):
    """Shortest rotation (3x3) turning unit vector a onto unit vector b."""
    v = _cross(a, b)
    c = _dot(a, b)
    if c < -0.999999:  # opposite: turn 180 degrees about any perpendicular axis
        axis = _norm(_cross(a, (1.0, 0.0, 0.0)) if abs(a[0]) < 0.9 else _cross(a, (0.0, 1.0, 0.0)))
        x, y, z = axis
        return [[2 * x * x - 1, 2 * x * y, 2 * x * z], [2 * x * y, 2 * y * y - 1, 2 * y * z], [2 * x * z, 2 * y * z, 2 * z * z - 1]]
    k = 1.0 / (1.0 + c)
    vx = [[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]]
    vx2 = _mat_mul(vx, vx)
    return [[(1.0 if i == j else 0.0) + vx[i][j] + vx2[i][j] * k for j in range(3)] for i in range(3)]


def smd_euler(q):
    """Local rotation as SMD / studiomdl radians (x, y, z) with matrix = Rz(z) * Ry(y) * Rx(x)."""
    m = _qmat(q)
    y = math.asin(max(-1.0, min(1.0, -m[2][0])))
    if abs(m[2][0]) < 0.999999:
        x = math.atan2(m[2][1], m[2][2])
        z = math.atan2(m[1][0], m[0][0])
    else:  # gimbal lock: put everything in z
        x = 0.0
        z = math.atan2(-m[0][1], m[1][1])
    return x, y, z


def euler_matrix(x, y, z):
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    return _mat_mul(rz, _mat_mul(ry, rx))


# ---------------------------------------------------------------- data

@dataclass
class TargetBone:
    name: str
    parent: str
    pos: tuple
    quat: tuple
    world_pos: tuple = (0.0, 0.0, 0.0)


def load_target(path: Path = SKELETON):
    bones = [TargetBone(b["name"], b["parent"], tuple(b["pos"]), tuple(b["quat"])) for b in json.loads(path.read_text())]
    world = {}
    for b in bones:
        if b.parent:
            pp, pq = world[b.parent]
            m = _qmat(pq)
            world[b.name] = (_add(pp, _mat_vec(m, b.pos)), _qmul(pq, b.quat))
        else:
            world[b.name] = (b.pos, b.quat)
        b.world_pos = world[b.name][0]
    return bones


@dataclass
class SourceModel:
    """A skinned mesh in its own bind pose: bone world positions, vertices and per-vertex bone weights."""
    bones: dict  # name -> (parent name or "", world position)
    vertices: list  # [(pos, normal, uv)]
    weights: list  # per vertex [(bone name, weight)]
    meshes: list  # [(material, [(i0, i1, i2)])]


def from_mdl(model) -> SourceModel:
    """source1_mdl.Model -> SourceModel (bone world positions from the bones' poseToBone matrices)."""
    bones = {}
    for b in model.bones:
        m = b.pose_to_bone  # rows: R | t (model -> bone); bone origin in model space = -R^T t
        r = [[m[0], m[1], m[2]], [m[4], m[5], m[6]], [m[8], m[9], m[10]]]
        t = (m[3], m[7], m[11])
        origin = _mul(_mat_vec(_transpose(r), t), -1.0)
        bones[b.name] = (model.bones[b.parent].name if b.parent >= 0 else "", origin)
    weights = [[(model.bones[i].name, w) for i, w in vw] for vw in model.weights]
    return SourceModel(bones, model.vertices, weights, [(m.material, m.triangles) for m in model.meshes])


def from_glb(path: Path, skip_meshes=()) -> SourceModel:
    """A glTF binary exported by Source 2 Viewer / ValveResourceFormat (one skin). Meshes whose name contains one of
    skip_meshes are left out (e.g. a model's first-person arms)."""
    data = path.read_bytes()
    if data[:4] != b"glTF":
        raise ValueError(f"{path} is not a .glb")
    jlen = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + jlen])
    bin_start = 20 + jlen + 8
    comp = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
    ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}

    def accessor(i):
        a = gltf["accessors"][i]
        view = gltf["bufferViews"][a["bufferView"]]
        fmt = comp[a["componentType"]]
        n = ncomp[a["type"]]
        size = struct.calcsize(fmt)
        stride = view.get("byteStride", size * n)
        base = bin_start + view.get("byteOffset", 0) + a.get("byteOffset", 0)
        out = []
        for k in range(a["count"]):
            vals = struct.unpack_from(f"<{n}{fmt}", data, base + k * stride)
            if a.get("normalized"):
                vals = tuple(v / {"B": 255.0, "H": 65535.0}[fmt] for v in vals)
            out.append(vals)
        return out

    skin = gltf["skins"][0]
    joints = [gltf["nodes"][j]["name"] for j in skin["joints"]]
    parent_of = {}
    for ni, node in enumerate(gltf["nodes"]):
        for c in node.get("children", []):
            parent_of[c] = ni
    ibm = accessor(skin["inverseBindMatrices"])
    bones = {}
    for j, node_index in enumerate(skin["joints"]):
        m = ibm[j]  # column-major 4x4: world -> joint
        r = [[m[0], m[4], m[8]], [m[1], m[5], m[9]], [m[2], m[6], m[10]]]
        t = (m[12], m[13], m[14])
        origin = _mul(_mat_vec(_transpose(r), t), -1.0)
        p = parent_of.get(node_index)
        parent = gltf["nodes"][p]["name"] if p is not None and p in skin["joints"] else ""
        bones[joints[j]] = (parent, origin)

    vertices, weights, meshes = [], [], []
    for mesh in gltf["meshes"]:
        if any(k in mesh.get("name", "") for k in skip_meshes):
            continue
        for prim in mesh["primitives"]:
            at = prim["attributes"]
            pos = accessor(at["POSITION"])
            nrm = accessor(at["NORMAL"]) if "NORMAL" in at else [(0.0, 0.0, 1.0)] * len(pos)
            uv = accessor(at["TEXCOORD_0"]) if "TEXCOORD_0" in at else [(0.0, 0.0)] * len(pos)
            sets = [(accessor(at[f"JOINTS_{k}"]), accessor(at[f"WEIGHTS_{k}"])) for k in range(4) if f"JOINTS_{k}" in at]
            first = len(vertices)
            for v in range(len(pos)):
                vertices.append((pos[v], nrm[v], (uv[v][0], uv[v][1])))  # top-left v like a .mdl
                vw = {}
                for js, ws in sets:
                    for jj, ww in zip(js[v], ws[v]):
                        if ww > 0:
                            vw[joints[jj]] = vw.get(joints[jj], 0.0) + ww
                weights.append(list(vw.items()))
            idx = [i[0] for i in accessor(prim["indices"])]
            mat = gltf["materials"][prim["material"]]["name"] if "material" in prim and "materials" in gltf else mesh.get("name", "body")
            tris = [(first + idx[k], first + idx[k + 1], first + idx[k + 2]) for k in range(0, len(idx) - 2, 3)]
            meshes.append((mat, tris))
    return SourceModel(bones, vertices, weights, meshes)


# ---------------------------------------------------------------- retargeting

def map_bone(name: str, target_names: set):
    """CS2 bone for a source bone name, or None (the weight then goes to the nearest mapped ancestor)."""
    n = name.lower()
    if n in target_names:
        return n
    m = re.fullmatch(r"(arm_upper|arm_lower)_([lr])_twist1?", n)
    if m and f"{m.group(1)}_{m.group(2)}_twist1" in target_names:
        return f"{m.group(1)}_{m.group(2)}_twist1"
    return None


def _body_frame(pos):
    up = _norm(_sub(pos["head_0"], pos["pelvis"]))
    left = _sub(pos["leg_upper_l"], pos["leg_upper_r"])
    left = _norm(_sub(left, _mul(up, _dot(left, up))))
    fwd = _cross(left, up)
    return [[fwd[i], left[i], up[i]] for i in range(3)]  # columns: forward, left, up


def _size(pos):
    return _len(_sub(pos["head_0"], pos["pelvis"])) + _len(_sub(pos["ankle_l"], pos["leg_upper_l"])) + \
        _len(_sub(pos["ankle_r"], pos["leg_upper_r"]))


def retarget(src: SourceModel, target):
    """Returns (vertices [(pos, normal, uv)], weights [[(target bone, weight)]]) in the CS2 bind pose."""
    tnames = {b.name for b in target}
    tpos = {b.name: b.world_pos for b in target}
    tparent = {b.name: b.parent for b in target}
    children = {b.name: [] for b in target}
    for b in target:
        if b.parent:
            children[b.parent].append(b.name)

    def subtree(n):
        return 1 + sum(subtree(c) for c in children[n])

    # source bones by CS2 name (first match wins; "arm_upper_L_TWIST" and "_TWIST1" both map to arm_upper_l_twist1)
    spos, smap = {}, {}
    for name, (_parent, origin) in src.bones.items():
        t = map_bone(name, tnames)
        smap[name] = t
        if t and name.lower() == t:
            spos[t] = origin
    for need in ("pelvis", "head_0", "leg_upper_l", "leg_upper_r", "ankle_l", "ankle_r"):
        if need not in spos:
            raise ValueError(f"source model has no '{need}' bone; is it a CS:GO / CS2 player model?")

    rg = _mat_mul(_body_frame(tpos), _transpose(_body_frame(spos)))
    scale = _size(tpos) / _size(spos)

    def helper(n):
        return "twist" in n or n.endswith("_end") or n.startswith("weapon")

    deform = {}  # CS2 bone -> (target origin, 3x3, source origin)
    for b in target:
        n = b.name
        if n not in spos:
            continue
        kids = [c for c in PRIMARY_CHILD.get(n, []) if c in spos and c in children[n]]
        if not kids:
            kids = sorted((c for c in children[n] if c in spos and not helper(c)), key=subtree, reverse=True)
        if kids:
            ds, dt = _sub(spos[kids[0]], spos[n]), _sub(tpos[kids[0]], tpos[n])
        elif tparent[n] in spos:
            ds, dt = _sub(spos[n], spos[tparent[n]]), _sub(tpos[n], tpos[tparent[n]])
        else:
            ds = dt = None
        rot = rg
        if ds is not None and _len(ds) > 1e-4 and _len(dt) > 1e-4:
            rot = _mat_mul(_rotation_between(_norm(_mat_vec(rg, ds)), _norm(dt)), rg)
        deform[n] = (tpos[n], rot, spos[n])

    def deform_bone(t):
        """The bone whose motion moves the mesh of CS2 bone t (twist and end bones move with their limb)."""
        while t not in deform or "twist" in t or t.endswith("_end"):
            t = tparent[t]
        return t

    resolved = {}

    def resolve(name):
        """Source bone -> (CS2 bone that gets the weight, bone that moves it)."""
        if name not in resolved:
            cur = name
            while cur and not smap.get(cur):
                cur = src.bones.get(cur, ("", None))[0]
            t = smap[cur] if cur else "pelvis"
            if t not in tnames:
                t = "pelvis"
            # A weight on a bone the source has but the CS2 bone map could not place goes to the deforming parent.
            resolved[name] = (t, deform_bone(t))
        return resolved[name]

    out_v, out_w = [], []
    for (p, n, uv), vw in zip(src.vertices, src.weights):
        if not vw:
            vw = [("pelvis", 1.0)] if "pelvis" in src.bones else [(next(iter(src.bones)), 1.0)]
        total = sum(w for _, w in vw) or 1.0
        np_, nn = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0)
        tw = {}
        for name, w in vw:
            w /= total
            t, d = resolve(name)
            to, rot, so = deform[d]
            np_ = _add(np_, _mul(_add(to, _mat_vec(rot, _mul(_sub(p, so), scale))), w))
            nn = _add(nn, _mul(_mat_vec(rot, n), w))
            tw[t] = tw.get(t, 0.0) + w
        top = sorted(tw.items(), key=lambda kv: -kv[1])[:4]
        s = sum(w for _, w in top)
        out_v.append((np_, _norm(nn), uv))
        out_w.append([(t, w / s) for t, w in top])
    return out_v, out_w


# ---------------------------------------------------------------- output

def _smd_header(target):
    index = {b.name: i for i, b in enumerate(target)}
    lines = ["version 1", "nodes"]
    lines += [f'{i} "{b.name}" {index[b.parent] if b.parent else -1}' for i, b in enumerate(target)]
    lines += ["end", "skeleton", "time 0"]
    for i, b in enumerate(target):
        x, y, z = smd_euler(b.quat)
        lines.append(f"{i} {b.pos[0]:.6f} {b.pos[1]:.6f} {b.pos[2]:.6f} {x:.6f} {y:.6f} {z:.6f}")
    lines.append("end")
    return lines, index


def write_bind_pose_smd(target, path: Path):
    """One-frame animation of the bind pose (ModelDoc otherwise takes its reference pose from the first animation)."""
    lines, _ = _smd_header(target)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="ascii")


def write_skinned_smd(target, vertices, weights, meshes, path: Path, material_name) -> dict:
    """material_name(source material) -> SMD material name, or None to leave that mesh out."""
    lines, index = _smd_header(target)
    lines.append("triangles")
    used = {}
    for material, tris in meshes:
        mat = material_name(material)
        if mat is None:
            continue
        for tri in tris:
            vs = [tri[0], tri[1], tri[2]]
            (a, na, _), (b, nb, _), (c, nc, _) = (vertices[i] for i in vs)
            face = _cross(_sub(b, a), _sub(c, a))
            if _dot(face, _add(_add(na, nb), nc)) < 0:
                vs = [vs[0], vs[2], vs[1]]
            lines.append(mat)
            for i in vs:
                (p, n, (u, v)), vw = vertices[i], weights[i]
                links = " ".join(f"{index[t]} {w:.6f}" for t, w in vw)
                lines.append(f"{index[vw[0][0]]} {p[0]:.6f} {p[1]:.6f} {p[2]:.6f} {n[0]:.6f} {n[1]:.6f} {n[2]:.6f} "
                             f"{u:.6f} {1.0 - v:.6f} {len(vw)} {links}")
            used[mat] = used.get(mat, 0) + 1
    lines.append("end")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="ascii")
    return used


def _remaps(pairs, indent):
    tabs = "\t" * indent
    return "".join(f"{tabs}{{\n{tabs}\tfrom = \"{a}\"\n{tabs}\tto = \"{b}\"\n{tabs}}},\n" for a, b in pairs)


def build_vmdl(smd_rel: str, bind_pose_rel: str, remaps, groups=None, gloves: str = DEFAULT_GLOVES) -> str:
    """remaps: [(SMD material, .vmat)]; groups: {name: [(SMD material, .vmat)]} extra material groups (skins)."""
    render = f"""			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						name = "body"
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
			}},"""
    group_nodes = "".join(f"""					{{
						_class = "MaterialGroup"
						name = "{name}"
						remaps =
						[
{_remaps(pairs, 7)}						]
					}},
""" for name, pairs in (groups or {}).items())
    materials = f"""			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[
{_remaps(remaps, 7)}						]
						use_global_default = false
						global_default_material = ""
					}},
{group_nodes}				]
			}},"""
    bodygroups = """			{
				_class = "BodyGroupList"
				children =
				[
					{
						_class = "BodyGroup"
						name = "body"
						children =
						[
							{
								_class = "BodyGroupChoice"
								meshes =
								[
									"body",
								]
							},
						]
					},
				]
			},"""
    text = TEMPLATE.read_text()
    return (text.replace("{{RENDER_MESH_LIST}}", render).replace("{{MATERIAL_GROUP_LIST}}", materials)
            .replace("{{BODY_GROUP_LIST}}", bodygroups).replace("{{BIND_POSE_SMD}}", bind_pose_rel)
            .replace("{{GLOVES}}", gloves))
