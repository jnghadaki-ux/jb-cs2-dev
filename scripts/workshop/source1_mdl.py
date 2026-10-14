"""
Minimal reader for Source 1 (CS:GO, MDL v44-49) models: the LOD 0 mesh of every body part, with materials, the bone
table and per-vertex bone weights. write_smd() writes it as a static SMD reference mesh (one root bone) that CS2's
ModelDoc can import; player_model.py uses the bones and weights to move a player model onto the CS2 skeleton.

Layouts follow studio.h / optimize.h (studiohdr_t, mstudiobodyparts_t, mstudiomodel_t, mstudiomesh_t, mstudiovertex_t,
vertexFileHeader_t, OptimizedModel::*, mstudiobone_t). No animation, flexes or eyeballs.
"""
import struct
from dataclasses import dataclass, field
from pathlib import Path


def _cstr(data: bytes, offset: int) -> str:
    end = data.index(b"\0", offset)
    return data[offset:end].decode("latin-1")


@dataclass
class Mesh:
    material: str
    triangles: list = field(default_factory=list)  # [(i0, i1, i2)] into Model.vertices


@dataclass
class Bone:
    name: str
    parent: int  # -1 for a root
    pos: tuple  # local position (parent space)
    quat: tuple  # local rotation (x, y, z, w)
    pose_to_bone: tuple  # 3x4 row-major matrix: model space -> bone space in the bind pose


@dataclass
class Model:
    name: str
    textures: list
    cdmaterials: list
    vertices: list  # [(pos(3), normal(3), uv(2))]
    meshes: list
    bones: list = field(default_factory=list)  # [Bone]
    weights: list = field(default_factory=list)  # per vertex: [(bone index, weight)]


def read_model(mdl_path: Path) -> Model:
    mdl = mdl_path.read_bytes()
    if mdl[:4] != b"IDST":
        raise ValueError(f"{mdl_path} is not a Source 1 model")
    version = struct.unpack_from("<i", mdl, 4)[0]
    name = _cstr(mdl, 12)

    num_tex, tex_index, num_cd, cd_index, num_skinref, num_families, skin_index, num_bodyparts, bodypart_index = \
        struct.unpack_from("<9i", mdl, 204)

    num_bones, bone_index = struct.unpack_from("<2i", mdl, 156)
    bones = []
    for i in range(num_bones):
        base = bone_index + i * 216
        name_off, parent = struct.unpack_from("<2i", mdl, base)
        pos = struct.unpack_from("<3f", mdl, base + 32)
        quat = struct.unpack_from("<4f", mdl, base + 44)
        pose_to_bone = struct.unpack_from("<12f", mdl, base + 96)
        bones.append(Bone(_cstr(mdl, base + name_off), parent, pos, quat, pose_to_bone))

    textures = []
    for i in range(num_tex):
        base = tex_index + i * 64
        name_off = struct.unpack_from("<i", mdl, base)[0]
        textures.append(_cstr(mdl, base + name_off).replace("\\", "/"))
    cdmaterials = []
    for i in range(num_cd):
        off = struct.unpack_from("<i", mdl, cd_index + i * 4)[0]
        cdmaterials.append(_cstr(mdl, off).replace("\\", "/").strip("/"))
    skin0 = list(struct.unpack_from(f"<{num_skinref}h", mdl, skin_index)) if num_skinref else []

    # ---- VVD: vertices (LOD 0, with fixups applied)
    vvd = mdl_path.with_suffix(".vvd").read_bytes()
    if vvd[:4] != b"IDSV":
        raise ValueError(f"{mdl_path.with_suffix('.vvd')} is not a vertex file")
    _, _, _, num_lods = struct.unpack_from("<4i", vvd, 0)
    lod_verts = struct.unpack_from("<8i", vvd, 16)
    num_fixups, fixup_start, vert_start, _tangent_start = struct.unpack_from("<4i", vvd, 48)

    def vertex(i):
        o = vert_start + i * 48
        px, py, pz, nx, ny, nz, u, v = struct.unpack_from("<8f", vvd, o + 16)
        return (px, py, pz), (nx, ny, nz), (u, v)

    def weight(i):
        o = vert_start + i * 48
        w = struct.unpack_from("<3f", vvd, o)
        b = struct.unpack_from("<3B", vvd, o + 12)
        n = vvd[o + 15]
        return [(b[k], w[k]) for k in range(min(n, 3)) if w[k] > 0]

    if num_fixups:
        order = []
        for f in range(num_fixups):
            lod, src, count = struct.unpack_from("<3i", vvd, fixup_start + f * 12)
            if lod >= 0:
                order.extend(range(src, src + count))
    else:
        order = range(lod_verts[0])
    vertices = [vertex(i) for i in order]
    weights = [weight(i) for i in order]

    # ---- MDL: body parts -> models -> meshes (vertex offsets)
    mdl_meshes = []  # per body part, per model: [(material skinref, first vertex of the mesh)]
    for b in range(num_bodyparts):
        bp = bodypart_index + b * 16
        _name_off, num_models, _base, model_index = struct.unpack_from("<4i", mdl, bp)
        models = []
        for m in range(num_models):
            mo = bp + model_index + m * 148
            num_meshes, mesh_index, _numverts, vertex_index = struct.unpack_from("<4i", mdl, mo + 64 + 4 + 4)
            model_first = vertex_index // 48
            meshes = []
            for k in range(num_meshes):
                me = mo + mesh_index + k * 116
                material, _modelindex, _nv, vertex_offset = struct.unpack_from("<4i", mdl, me)
                meshes.append((material, model_first + vertex_offset))
            models.append(meshes)
        mdl_meshes.append(models)

    # ---- VTX: triangles of LOD 0
    vtx = mdl_path.with_suffix(".dx90.vtx").read_bytes()
    _vtx_version, = struct.unpack_from("<i", vtx, 0)
    vtx_bodyparts, vtx_bodypart_offset = struct.unpack_from("<2i", vtx, 28)

    def strip_groups(mesh_hdr, count, offset, size):
        groups = []
        for g in range(count):
            sg = mesh_hdr + offset + g * size
            nverts, vert_off, nidx, idx_off = struct.unpack_from("<4i", vtx, sg)
            verts = [struct.unpack_from("<H", vtx, sg + vert_off + i * 9 + 4)[0] for i in range(nverts)]
            idx = list(struct.unpack_from(f"<{nidx}H", vtx, sg + idx_off)) if nidx else []
            groups.append((verts, idx))
        return groups

    def plausible(groups):
        return all(i < len(v) for v, idx in groups for i in idx) and all(len(idx) % 3 == 0 for _, idx in groups)

    out_meshes = []
    for b in range(min(vtx_bodyparts, len(mdl_meshes))):
        bp = vtx_bodypart_offset + b * 8
        num_models, model_off = struct.unpack_from("<2i", vtx, bp)
        for m in range(min(num_models, len(mdl_meshes[b]))):
            mo = bp + model_off + m * 8
            _num_lods, lod_off = struct.unpack_from("<2i", vtx, mo)
            lo = mo + lod_off  # LOD 0
            num_meshes, mesh_off = struct.unpack_from("<2i", vtx, lo)
            for k in range(min(num_meshes, len(mdl_meshes[b][m]))):
                mh = lo + mesh_off + k * 9
                num_groups, group_off = struct.unpack_from("<2i", vtx, mh)
                # CS:GO / L4D2 strip groups carry two extra ints (topology); older ones do not.
                groups = strip_groups(mh, num_groups, group_off, 33)
                if num_groups > 1 and not plausible(groups):
                    groups = strip_groups(mh, num_groups, group_off, 25)
                skinref, first = mdl_meshes[b][m][k]
                tex = skin0[skinref] if 0 <= skinref < len(skin0) else skinref
                material = textures[tex] if 0 <= tex < len(textures) else f"material{tex}"
                mesh = Mesh(material)
                for verts, idx in groups:
                    for t in range(0, len(idx) - 2, 3):
                        a, c, d = (first + verts[idx[t]], first + verts[idx[t + 1]], first + verts[idx[t + 2]])
                        mesh.triangles.append((a, c, d))
                if mesh.triangles:
                    out_meshes.append(mesh)

    return Model(name, textures, cdmaterials, vertices, out_meshes, bones, weights)


def _front_facing(v0, v1, v2) -> bool:
    """True when (v0, v1, v2) winds counter-clockwise around the vertex normals (right-hand rule)."""
    (a, na, _), (b, nb, _), (c, nc, _) = v0, v1, v2
    e1 = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    e2 = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
    n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
    avg = (na[0] + nb[0] + nc[0], na[1] + nb[1] + nc[1], na[2] + nb[2] + nc[2])
    return n[0] * avg[0] + n[1] * avg[1] + n[2] * avg[2] >= 0


def write_smd(model: Model, path: Path, material_path) -> dict:
    """Static reference SMD (one bone). material_path(texture name) -> material name written per triangle, or None to
    leave that mesh out. Returns {material name: triangle count}."""
    lines = ["version 1", "nodes", '0 "root" -1', "end", "skeleton", "time 0", "0 0 0 0 0 0 0", "end", "triangles"]
    used = {}
    for mesh in model.meshes:
        mat = material_path(mesh.material)
        if mat is None:
            continue
        for tri in mesh.triangles:
            vs = [model.vertices[i] for i in tri]
            if not _front_facing(*vs):
                vs = [vs[0], vs[2], vs[1]]
            lines.append(mat)
            for (p, n, (u, v)) in vs:
                lines.append(f"0 {p[0]:.6f} {p[1]:.6f} {p[2]:.6f} {n[0]:.6f} {n[1]:.6f} {n[2]:.6f} {u:.6f} {1.0 - v:.6f} 1 0 1")
            used[mat] = used.get(mat, 0) + 1
    lines.append("end")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="ascii")
    return used


def bounds(model: Model):
    xs = [p[0] for p, _, _ in model.vertices]
    ys = [p[1] for p, _, _ in model.vertices]
    zs = [p[2] for p, _, _ in model.vertices]
    return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))
