"""
Moves the SWBS prisoner meshes onto the skeleton of a shipping CS2 agent (decompiled ctm_diver_varianta) and writes a
ModelDoc .vmdl that copies that agent's attachments, hitboxes, physics, bone constraints, animation skeletons and
graphs. Fixes "Attempted to attach attachment ... to an unknown bone" and broken weapon holding of the 0.18 prisoners.
"""
import math, os, re, shutil, sys
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
from rig import read_smd, world, parse_vmdl_skeleton, qangle_to_mat, mat4, mat_to_euler_xyz

# usage: python retarget.py <pack addon folder (swbs_jailbreak)> <decompiled ctm_diver_varianta folder> <output folder>
PACK, DIVER_DIR, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
DIVER_VMDL = DIVER_DIR + '/ctm_diver_varianta.vmdl'
ROOT = 'characters/models/swbs/prisoners'
SHARED = ROOT + '/shared'
PRISONERS = ['male_black_big', 'female_asian', 'male_white_hair']

diver_text = open(DIVER_VMDL).read()
bones = parse_vmdl_skeleton(diver_text)
assert len(bones) == 86, len(bones)
dw, dlocal, dindex = {}, {}, {}
for i, b in enumerate(bones):
    L = mat4(qangle_to_mat(*b['angles']), b['origin'])
    dlocal[b['name']] = L
    dw[b['name']] = L if not b['parent'] else dw[b['parent']] @ L
    dindex[b['name']] = i
by_lower = {b['name'].lower(): b['name'] for b in bones}


def node_block():
    out = ['nodes']
    for i, b in enumerate(bones):
        out.append(f'{i} "{b["name"]}" {dindex[b["parent"]] if b["parent"] else -1}')
    out += ['end', 'skeleton', 'time 0']
    for i, b in enumerate(bones):
        L = dlocal[b['name']]
        rx, ry, rz = mat_to_euler_xyz(L[:3, :3])
        t = L[:3, 3]
        out.append(f'{i} {t[0]:.6f} {t[1]:.6f} {t[2]:.6f} {rx:.6f} {ry:.6f} {rz:.6f}')
    out.append('end')
    return out


def retarget(name):
    src = f'{PACK}/{ROOT}/{name}/{name}.smd'
    nodes, local, tri_lines, _ = read_smd(src)
    pw = world(nodes, local)
    pname = {i: n for i, n, _ in nodes}
    pparent = {i: p for i, _, p in nodes}

    def target(i):
        """Prisoner bone index -> diver bone name (unknown helper bones fall back to their parent)."""
        while i >= 0:
            d = by_lower.get(pname[i].lower())
            if d: return d
            i = pparent[i]
        return 'pelvis'

    delta, tidx, unmapped = {}, {}, set()
    for i, _, _ in nodes:
        d = target(i)
        if d.lower() != pname[i].lower(): unmapped.add(pname[i])
        delta[i] = dw[d] @ np.linalg.inv(pw[i])
        tidx[i] = dindex[d]

    out = ['version 1'] + node_block() + ['triangles']
    used_unmapped, n_vert = set(), 0
    for line in tri_lines:
        p = line.split()
        if len(p) < 9:
            out.append(line.strip()); continue
        parent = int(p[0]); pos = np.array(list(map(float, p[1:4]))); nrm = np.array(list(map(float, p[4:7])))
        uv = p[7:9]
        links = []
        if len(p) > 9:
            n = int(p[9])
            links = [(int(p[10 + 2 * k]), float(p[11 + 2 * k])) for k in range(n)]
        if not links: links = [(parent, 1.0)]
        tot = sum(w for _, w in links) or 1.0
        M = sum((w / tot) * delta[b] for b, w in links)
        v = M @ np.append(pos, 1.0)
        nn = M[:3, :3] @ nrm
        nn /= (np.linalg.norm(nn) or 1.0)
        merged = {}
        for b, w in links:
            if pname[b] in unmapped: used_unmapped.add(pname[b])
            merged[tidx[b]] = merged.get(tidx[b], 0.0) + w / tot
        link_s = ' '.join(f'{b} {w:.6f}' for b, w in merged.items())
        out.append(f'{tidx[parent]} {v[0]:.6f} {v[1]:.6f} {v[2]:.6f} {nn[0]:.6f} {nn[1]:.6f} {nn[2]:.6f} {uv[0]} {uv[1]} {len(merged)} {link_s}')
        n_vert += 1
    out.append('end')
    return '\n'.join(out) + '\n', n_vert, used_unmapped


def block(text, cls):
    """The whole { ... } list node of a RootNode child with _class = cls (string slice incl. braces)."""
    i = text.index(f'_class = "{cls}"')
    start = text.rindex('{', 0, i)
    depth = 0
    for j in range(start, len(text)):
        if text[j] == '{': depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                return text[start:j + 1]
    raise ValueError(cls)


def build_vmdl(name, prisoner_vmdl):
    v = diver_text
    # Render meshes: our body instead of the diver's third-person body + gloves; first-person arms stay Valve's.
    rml = block(v, 'RenderMeshList')
    new_rml = rml
    new_rml = re.sub(r'\{\s*_class = "RenderMeshFile"\s*name = "thirdperson_default_gloves"[^}]*\},\s*', '', new_rml)
    new_rml = new_rml.replace('agents/models/ctm_diver/ctm_diver_varianta_thirdperson_body.dmx', f'{ROOT}/{name}/{name}.smd')
    # Materials: the prisoner's own remaps and "white" material group (from the 0.18 .vmdl), right after the meshes.
    mgl = block(prisoner_vmdl, 'MaterialGroupList')
    first_vmat = re.search(r'to = "([^"]+\.vmat)"', mgl).group(1)
    assert rml in v
    v = v.replace(rml, new_rml + ',\n\t\t\t' + mgl, 1)
    v = re.sub(r'\s*"thirdperson_default_gloves",', '', v)
    v = v.replace('agents/models/ctm_diver/ctm_diver_varianta_', f'{SHARED}/diver_')
    # Agent patches (stickers) go on material_0: none for the prisoners (it would land on hair / face).
    v = v.replace('characters/models/ctm_diver/materials/ctm_diver_body_varianta.vmat', '')
    header = ('<!-- SWBS Jailbreak: SWBS prisoner on the skeleton of CS2\'s ctm_diver_varianta agent (86 bones, current\n'
              '     animation skeletons and graphs, Valve\'s hitboxes / physics / attachments / bone constraints).\n'
              '     Generated by the skeleton fix; the mesh was moved bone by bone from the 0.18 rig. -->\n')
    first_nl = v.index('\n') + 1
    return v[:first_nl] + header + v[first_nl:]


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(f'{OUT}/{SHARED}', exist_ok=True)
    for f in os.listdir(DIVER_DIR):
        if f.endswith('.dmx') and 'thirdperson' not in f:
            shutil.copy(f'{DIVER_DIR}/{f}', f'{OUT}/{SHARED}/{f.replace("ctm_diver_varianta_", "diver_")}')
    for name in PRISONERS:
        smd, n, unmapped = retarget(name)
        d = f'{OUT}/{ROOT}/{name}'
        os.makedirs(d, exist_ok=True)
        open(f'{d}/{name}.smd', 'w').write(smd)
        pv = open(f'{PACK}/{ROOT}/{name}/{name}.vmdl').read()
        open(f'{d}/{name}.vmdl', 'w').write(build_vmdl(name, pv))
        print(f'{name}: {n} vertices, weighted helper bones folded into parents: {sorted(unmapped) or "none"}')


main()
