"""Skeleton maths for moving the SWBS prisoner meshes onto a shipping CS2 agent skeleton."""
import math, re
import numpy as np

def euler_xyz_to_mat(rx, ry, rz):
    # SMD (Source) euler: X roll, Y pitch, Z yaw, applied as R = Rz * Ry * Rx
    cx, sx, cy, sy, cz, sz = math.cos(rx), math.sin(rx), math.cos(ry), math.sin(ry), math.cos(rz), math.sin(rz)
    Rx = np.array([[1,0,0],[0,cx,-sx],[0,sx,cx]]); Ry = np.array([[cy,0,sy],[0,1,0],[-sy,0,cy]]); Rz = np.array([[cz,-sz,0],[sz,cz,0],[0,0,1]])
    return Rz @ Ry @ Rx

def qangle_to_mat(pitch, yaw, roll):
    # Source QAngle (degrees): pitch about Y, yaw about Z, roll about X -> same composition Rz(yaw) Ry(pitch) Rx(roll)
    return euler_xyz_to_mat(math.radians(roll), math.radians(pitch), math.radians(yaw))

def mat4(R, t):
    M = np.eye(4); M[:3,:3] = R; M[:3,3] = t; return M

def mat_to_euler_xyz(R):
    # inverse of euler_xyz_to_mat
    sy = -R[2,0]
    sy = max(-1.0, min(1.0, sy))
    ry = math.asin(sy)
    if abs(math.cos(ry)) > 1e-6:
        rx = math.atan2(R[2,1], R[2,2]); rz = math.atan2(R[1,0], R[0,0])
    else:
        rx = math.atan2(-R[1,2], R[1,1]); rz = 0.0
    return rx, ry, rz

def read_smd(path):
    lines = open(path).read().split('\n')
    i = lines.index('nodes'); j = lines.index('end', i)
    nodes = []
    for l in lines[i+1:j]:
        m = re.match(r'\s*(\d+)\s+"([^"]*)"\s+(-?\d+)', l)
        nodes.append((int(m.group(1)), m.group(2), int(m.group(3))))
    k = lines.index('skeleton'); e = lines.index('end', k)
    local = {}
    for l in lines[k+2:e]:
        p = l.split()
        if len(p) == 7:
            idx = int(p[0]); v = list(map(float, p[1:]))
            local[idx] = mat4(euler_xyz_to_mat(v[3], v[4], v[5]), v[:3])
    t = lines.index('triangles')
    tris_end = len(lines) - 1 - lines[::-1].index('end')
    return nodes, local, lines[t+1:tris_end], lines

def world(nodes, local):
    W = {}
    for idx, name, parent in nodes:  # parents come first in SMD
        W[idx] = local[idx] if parent < 0 else W[parent] @ local[idx]
    return W

def parse_vmdl_skeleton(text):
    """Returns [(name, parent_name, origin, angles)] from the ModelDoc Skeleton node, depth-first."""
    start = text.index('_class = "Skeleton"')
    out = []; stack = []
    depth_of_bone = []
    pos = start
    # walk line by line tracking bone nesting via 'children' brackets
    lines = text[start:].split('\n')
    cur = None; bone_stack = []; bracket = 0; bone_open_levels = []
    for l in lines[1:]:
        s = l.strip()
        if s.startswith('_class = "Bone"'):
            cur = {'name': None, 'origin': None, 'angles': None, 'parent': bone_stack[-1] if bone_stack else ''}
            continue
        if cur is not None and s.startswith('name ='):
            cur['name'] = s.split('"')[1]; continue
        if cur is not None and s.startswith('origin ='):
            cur['origin'] = [float(x) for x in s[s.index('[')+1:s.index(']')].split(',')]; continue
        if cur is not None and s.startswith('angles ='):
            cur['angles'] = [float(x) for x in s[s.index('[')+1:s.index(']')].split(',')]
            out.append(cur); continue
        if s.startswith('children') and cur is not None:
            bone_stack.append(cur['name']); bone_open_levels.append(bracket); cur = None; continue
        if s == '[': bracket += 1; continue
        if s in (']', '],'):
            bracket -= 1
            if bone_open_levels and bracket == bone_open_levels[-1]:
                bone_open_levels.pop(); bone_stack.pop()
            if bracket < 0: break
            continue
        if s.startswith('children = [  ]') or s.startswith('children = []'):
            cur = None
    return out
