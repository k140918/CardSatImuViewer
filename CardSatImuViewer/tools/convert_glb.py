"""KiCad の GLB から部品だけを取り出し、機体座標の軽量メッシュ（components.bin.gz）にする。

機体座標 [mm]：基板中心が原点、+X = B 列（KiCad の +Y）、+Y = ESP32-S3 側（KiCad の +X）、+Z = 部品面の外向き
KiCad GLB [m]：X = KiCad X、Y = 高さ（基板裏面 0、部品面 1.6 mm）、Z = KiCad Y
"""
import sys, json, struct, gzip, collections
import numpy as np

SRC, OUT = sys.argv[1], sys.argv[2]
CX, CY, HALF_T = 136.2, 122.41, 0.8
BOARD_MESH_PREFIX = '=>[0:1:1:87]'   # 基板本体（Gerber 画像の板を使うので除外）
# 太陽電池はモデルが白い仮の色なので除外し、アプリ側で濃紺の画像付きの板を置く（大きさだけ表示）
SKIP_REFS = {'CEL1', 'CEL2'}
skip_bounds = collections.defaultdict(list)

d = open(SRC, 'rb').read()
clen = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + clen])
o = 20 + clen
blen = struct.unpack('<I', d[o:o + 4])[0]
binc = d[o + 8:o + 8 + blen]

CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}
def acc(i):
    a = j['accessors'][i]; bv = j['bufferViews'][a['bufferView']]
    dt = np.dtype(CT[a['componentType']]); n = NC[a['type']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride', 0)
    if stride and stride != dt.itemsize * n:
        raw = np.frombuffer(binc, np.uint8, count=stride * a['count'], offset=off).reshape(a['count'], stride)
        return raw[:, :dt.itemsize * n].copy().view(dt).reshape(a['count'], n)
    return np.frombuffer(binc, dt, count=a['count'] * n, offset=off).reshape(a['count'], n)

def quat_mat(q):
    x, y, z, w = q
    return np.array([[1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y)],
                     [2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x)],
                     [2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y)]])
def local(n):
    M = np.eye(4)
    if 'matrix' in n: return np.array(n['matrix']).reshape(4, 4).T
    R = quat_mat(n['rotation']) if 'rotation' in n else np.eye(3)
    S = np.diag(n.get('scale', [1, 1, 1]))
    M[:3, :3] = R @ S
    M[:3, 3] = n.get('translation', [0, 0, 0])
    return M

def lin2srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, 12.92 * c, 1.055 * np.power(c, 1 / 2.4) - 0.055)

groups = collections.defaultdict(lambda: {'p': [], 'n': [], 'i': [], 'nv': 0})
refs = collections.Counter()
skipped = 0
nodes = j['nodes']

def walk(ni, parentM, ref):
    global skipped
    n = nodes[ni]
    M = parentM @ local(n)
    if 'mesh' in n:
        mesh = j['meshes'][n['mesh']]
        if mesh.get('name', '').startswith(BOARD_MESH_PREFIX) or n.get('name', '').startswith(BOARD_MESH_PREFIX):
            skipped += sum(j['accessors'][p['indices']]['count'] // 3 for p in mesh['primitives'])
        else:
            for pr in mesh['primitives']:
                if pr.get('mode', 4) != 4: continue
                P = acc(pr['attributes']['POSITION']).astype(np.float64)
                N = acc(pr['attributes']['NORMAL']).astype(np.float64) if 'NORMAL' in pr['attributes'] else None
                I = acc(pr['indices']).reshape(-1).astype(np.int64)
                Pw = (M[:3, :3] @ P.T).T + M[:3, 3]
                # GLB [m] → 機体 [mm]
                pb = np.stack([Pw[:, 2] * 1000 - CY, Pw[:, 0] * 1000 - CX, Pw[:, 1] * 1000 - HALF_T], 1)
                if ref in SKIP_REFS:
                    skip_bounds[ref].append(pb)
                    continue
                if N is not None:
                    Nw = (np.linalg.inv(M[:3, :3]).T @ N.T).T
                    Nw /= np.maximum(np.linalg.norm(Nw, axis=1, keepdims=True), 1e-12)
                    nb = np.stack([Nw[:, 2], Nw[:, 0], Nw[:, 1]], 1)
                else:
                    nb = np.zeros_like(pb)
                # 行列式が負（鏡像）なら三角形の向きを反転
                if np.linalg.det(M[:3, :3]) < 0:
                    I = I.reshape(-1, 3)[:, [0, 2, 1]].reshape(-1)
                mat = j['materials'][pr['material']] if 'material' in pr else {}
                base = np.array(mat.get('pbrMetallicRoughness', {}).get('baseColorFactor', [0.8, 0.8, 0.8, 1])[:3])
                rgb = tuple(int(round(v * 255)) for v in lin2srgb(base))
                key = tuple(min(255, (v // 8) * 8 + 4) for v in rgb)   # 近い色はまとめる
                g = groups[key]
                g['p'].append(pb.astype(np.float32)); g['n'].append(nb.astype(np.float32))
                g['i'].append((I + g['nv']).astype(np.int32)); g['nv'] += len(pb)
                refs[ref] += len(I) // 3
    for c in n.get('children', []):
        walk(c, M, ref)

root = nodes[j['scenes'][j.get('scene', 0)]['nodes'][0]]
for c in root.get('children', []):
    walk(c, local(root), nodes[c].get('name', '?'))

out = bytearray(b'CSM1')
keys = sorted(groups, key=lambda k: -sum(len(x) for x in groups[k]['i']))
out += struct.pack('<i', len(keys))
total = 0
allp = []
for k in keys:
    g = groups[k]
    P = np.concatenate(g['p']); N = np.concatenate(g['n']); I = np.concatenate(g['i'])
    allp.append(P)
    out += struct.pack('<BBBB', k[0], k[1], k[2], 255)
    out += struct.pack('<ii', len(P), len(I))
    out += P.tobytes() + N.tobytes() + I.tobytes()
    total += len(I) // 3
allp = np.concatenate(allp)
with gzip.open(OUT, 'wb', compresslevel=9) as f:
    f.write(out)
print('groups', len(keys), 'triangles', total, 'skipped(board)', skipped)
print('bounds min', allp.min(0).round(2), 'max', allp.max(0).round(2))
print('raw', len(out), 'bytes')
print('top refs', refs.most_common(8))
for r,v in skip_bounds.items():
    a=np.concatenate(v); print('skip',r,'min',a.min(0).round(3),'max',a.max(0).round(3))
print('has', [r for r in ('J1', 'J2', 'CEL1', 'CEL2', 'U1', 'IC8') if r in refs])
