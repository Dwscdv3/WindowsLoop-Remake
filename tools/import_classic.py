"""把原版 Unity 场景的 Tilemap 结构导出成 WindowsLoop 关卡 JSON（结构还原）。

只保留「实体 / 装饰 / 空气 / 宝石」的位置，用现有关卡图例绘制：
    g  地表实体    #  岩体实体    v  装饰（非实体）    .  空气

Unity 的 LoopArea 直接当作 Piece 的环形地图尺寸；y 轴翻转成 rows 从上到下；
Gem tile 转成 piece.gem 坐标；Background 层一律按非实体装饰处理。

用法：
    python tools/import_classic.py <输出关卡名> <场景:地图id[:出生列]> ...
例：
    python tools/import_classic.py classic-1 Level-0:cavern Level-0-Assist:start:11
"""

import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ORIG = os.path.join(ROOT, "ref-original")
SCENE_DIR = os.path.join(ORIG, "Assets", "Scenes")
OUT_DIR = os.path.join(ROOT, "src", "WindowsLoop", "assets", "levels")

# 新引擎里共用的图例，和 assets/levels/level1.json 保持一致
TILES = {
    "g": {"sprite": 9, "solid": True},
    "#": {"sprites": [0, 1, 2, 26], "solid": True},
    "v": {"sprite": 86, "solid": False},
}
SOLID_CHARS = ("g", "#")

# 关卡显示名：重跑脚本时不要把中文名退化成文件名
LEVEL_NAMES = {"classic-1": "经典第 1 关 · 起点与洞穴"}


def build_tile_roles():
    """guid -> 角色：G 宝石 / # 实体 / . 装饰。分类逻辑沿用 parse_levels.py。"""
    guid2path = {}
    for meta in glob.glob(os.path.join(ORIG, "Assets", "**", "*.asset.meta"), recursive=True):
        txt = open(meta, encoding="utf-8", errors="replace").read()
        m = re.search(r"guid: ([0-9a-f]{32})", txt)
        if m:
            guid2path[m.group(1)] = meta[:-5]

    roles = {}
    for guid, path in guid2path.items():
        txt = open(path, encoding="utf-8", errors="replace").read()
        nm = re.search(r"m_Name: (.*)", txt)
        name = nm.group(1).strip() if nm else os.path.basename(path)
        ct = re.search(r"m_ColliderType: (\d+)", txt)
        if "Gem" in name:
            roles[guid] = "G"
        elif ct and int(ct.group(1)) != 0:
            roles[guid] = "#"
        else:
            roles[guid] = "."
    return roles


ROLES = build_tile_roles()


def doc_split(text):
    parts = re.split(r"^--- !u!(\d+) &(\d+).*$", text, flags=re.M)
    return [(int(parts[i]), parts[i + 1], parts[i + 2]) for i in range(1, len(parts), 3)]


def block_lines(text, key):
    lines = text.splitlines()
    out, inside = [], False
    for ln in lines:
        if ln.startswith("  " + key + ":"):
            inside = True
            tail = ln.split(":", 1)[1].strip()
            if tail:
                out.append(tail)
            continue
        if inside:
            if ln.startswith("    ") or ln.startswith("  - ") or ln.startswith("   "):
                out.append(ln)
            elif ln.strip() == "":
                continue
            else:
                break
    return out


def parse_tilemap(obj_text):
    m = re.search(r"^  m_Tiles:\n(.*?)^  m_AnimatedTiles", obj_text, flags=re.M | re.S)
    if not m:
        return None
    tiles = []
    for blk in re.finditer(
        r"  - first: \{x: (-?\d+), y: (-?\d+), z: -?\d+\}\n    second:\n((?:      .*\n)+)",
        m.group(1),
    ):
        x, y = int(blk.group(1)), int(blk.group(2))
        idx = re.search(r"m_TileIndex: (-?\d+)", blk.group(3))
        tiles.append((x, y, int(idx.group(1)) if idx else -1))

    guids = []
    for ln in block_lines(obj_text, "m_TileAssetArray"):
        if "m_Data:" in ln:
            g = re.search(r"guid: ([0-9a-f]{32})", ln)
            guids.append(g.group(1) if g else None)
    return tiles, guids


def read_scene(name):
    path = os.path.join(SCENE_DIR, name + ".unity")
    text = open(path, encoding="utf-8", errors="replace").read()
    loop = re.search(
        r"LoopArea:\n    x: (-?\d+)\n    y: (-?\d+)\n    width: (\d+)\n    height: (\d+)", text
    )
    if not loop:
        raise ValueError(f"{name}: 找不到 LoopArea")
    x0, y0, w, h = map(int, loop.groups())

    docs = doc_split(text)
    names = {}
    for cid, fid, body in docs:
        if cid == 1:
            nm = re.search(r"m_Name: (.*)", body)
            if nm:
                names[fid] = nm.group(1).strip()

    layers = {}
    for cid, fid, body in docs:
        if cid != 1839735485:  # Tilemap
            continue
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
        layer = names.get(go.group(1), "") if go else ""
        res = parse_tilemap(body)
        if res:
            layers[layer] = res
    return (x0, y0, w, h), layers


def convert(scene_name, piece_id, prefer_col=None):
    area, layers = read_scene(scene_name)
    x0, y0, w, h = area
    y_top = y0 + h - 1

    cells = [["." for _ in range(w)] for _ in range(h)]
    gem = None

    # 背景层先铺（一律非实体装饰），主图层后铺并覆盖
    for layer_name in ("Background", "BaseMap"):
        if layer_name not in layers:
            continue
        tiles, guids = layers[layer_name]
        for x, y, idx in tiles:
            guid = guids[idx] if 0 <= idx < len(guids) else None
            role = ROLES.get(guid, ".")
            r = (y_top - y) % h
            c = (x - x0) % w
            if layer_name == "Background":
                cells[r][c] = "v"
            elif role == "#":
                cells[r][c] = "#"
            elif role == "G":
                gem = [c, r]
                cells[r][c] = "."
            else:
                cells[r][c] = "v"

    # 岩体的裸露顶面画成地表瓦片，画面和现有关卡风格统一（碰撞不变）
    solid = [[cells[r][c] == "#" for c in range(w)] for r in range(h)]
    for r in range(h):
        for c in range(w):
            if solid[r][c] and not solid[(r - 1) % h][c]:
                cells[r][c] = "g"

    is_solid = lambda r, c: cells[r % h][c % w] in SOLID_CHARS
    prefer = prefer_col if prefer_col is not None else w // 3
    spawn = None
    best = None
    for c in range(w):
        for r in range(2, h):
            if is_solid(r, c) and not is_solid(r - 1, c) and not is_solid(r - 2, c):
                score = abs(c - prefer)
                if best is None or score < best[0]:
                    best = (score, [c, r - 2])
                break
    spawn = best[1] if best else [0, 0]

    piece = {
        "id": piece_id,
        "rows": ["".join(row) for row in cells],
        "spawn": spawn,
    }
    if gem is not None:
        piece["gem"] = gem

    counts = {ch: sum(row.count(ch) for row in piece["rows"]) for ch in "g#v."}
    open_left = sum(1 for r in range(h) if not is_solid(r, 0))
    open_right = sum(1 for r in range(h) if not is_solid(r, w - 1))
    print(
        f"  {scene_name:18s} -> {piece_id:10s} {w}x{h}  "
        f"地表g={counts['g']} 岩体#={counts['#']} 装饰v={counts['v']} 空气.={counts['.']}  "
        f"spawn={spawn} gem={gem} 左缘空行={open_left} 右缘空行={open_right}  "
        f"层={','.join(sorted(layers))}"
    )
    return piece


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    out_name, specs = sys.argv[1], sys.argv[2:]
    pieces = []
    for spec in specs:
        parts = spec.split(":")
        scene, pid = parts[0], parts[1]
        prefer = int(parts[2]) if len(parts) > 2 else None
        pieces.append(convert(scene, pid, prefer))

    level = {
        "name": LEVEL_NAMES.get(out_name, out_name),
        "maxWindows": len(pieces),
        "tiles": TILES,
        "pieces": pieces,
    }
    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, out_name + ".json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(level, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"\n写出 {out}  ({len(pieces)} 块地图, maxWindows={len(pieces)})")


if __name__ == "__main__":
    main()
