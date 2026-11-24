"""把原版 Unity 场景的 Tilemap 逐格还原成 WindowsLoop 关卡 JSON（输出 classic-1）。

和旧的 import_classic.py 的区别：那份只按「实体 / 装饰」二分，把整片地图塌成
g / # / v 三个图例，具体用哪块瓦片交给坐标哈希——参考工程里作者是**手工摆**
不同边缘块和材质变体的，一塌就全乱了。这份已经取代了它，classic-1 就是本脚本的产物。

这份改成逐格还原：每个格子直接取它引用的瓦片资产对应的图集索引。

对应关系的推导
--------------
参考工程有两张 8×12 的图集（每格 8×8）：

    cavesofgallet_tiles.png           —— 基准表
    cavesofgallet_reverse_tiles.png   —— **整张水平镜像**的同一份素材

上面这行是关键。reverse 表不是"另一套图"，它满足
    reverse[(r, c)] == 水平镜像( base[(r, 7 - c)] )
96 格逐格完全相等（脚本启动时会断言）。作者是整张翻过来用的：
同一块瓦片的"朝左 / 朝右"两个版本分别落在两张表的镜像列上。

本项目的 assets/tiles-original.png 与基准表**逐像素相同**，索引按「英文书写顺序」排：

    index = row * 8 + col        （row 从上往下，col 从左往右）

Unity 的 sprite rect 原点在左下角，所以：

    index = (SHEET_ROWS - 1 - rect.y / 8) * 8 + rect.x / 8

实测：基准表里所有 cavesofgallet_tiles_N 反算出来的索引都等于 N（脚本会断言）。

于是每个格子要落到两类数据上：

    基准表 + rect            -> sprite = index,            xf = 0
    reverse 表 + rect        -> sprite = r*8 + (7 - c),    xf = 4（水平镜像）

xf 编码见 Level.cs：低两位是顺时针 90° 步数，bit2 是水平镜像；4 就是纯水平镜像。

阻挡：以瓦片资产的 m_ColliderType 为准（0 = 不挡）。参考工程的 Background 层
没有碰撞体，按纯画面处理。

注意：场景里 1147 个瓦片条目的 m_TileMatrixIndex 全是 0，Tile 资产自带的 m_Transform
也全是单位矩阵——参考工程没有用 Tilemap 的翻转功能，方向全靠上面两张表。

用法：
    python tools/import_classic2.py
"""

import glob
import json
import os
import re
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required: python -m pip install pillow")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ORIG = os.path.join(ROOT, "ref-original")
SCENE_DIR = os.path.join(ORIG, "Assets", "Scenes")
OUT_DIR = os.path.join(ROOT, "src", "WindowsLoop", "assets", "levels")

SHEET_CELL = 8
SHEET_COLS = 8
SHEET_ROWS = 12

OUT_NAME = "classic-1"
LEVEL_NAME = "经典第 1 关 · 起点与洞穴"

# 场景:地块id[:出生列] —— 顺序就是 pieces 数组的顺序。
# 游戏按 PieceForSlot(Windows.Count) 取块，第一个窗口拿 pieces[0]，
# 出生点也取自那一块，所以把 start 放前面 = 开局站在小台地上。
PIECES = [
    ("Level-0-Assist", "start", 11),
    ("Level-0", "cavern", 36),
]

# 宝石不是图集里的普通格，而是独立的 GemTile 资产；在本项目里固定用 93 号图
GEM_SPRITE = 93

SHEET_DIR = os.path.join(ORIG, "Assets", "Resources", "cavesofgallet")
BASE_SHEET = "cavesofgallet_tiles.png"
REVERSE_SHEET = "cavesofgallet_reverse_tiles.png"

# 图例字符的挑选顺序：优先用语义对得上的预设字符（sprite 完全一致才用），
# 其余按这个池子顺序分配。池子里刻意避开 TilePresets 已占用的字符，
# 免得编辑器里显示出一个对不上的名字。
PRESET_EXACT = {
    7: "#",    # 岩体（暗岩底一族，参考工程的主力填充）
    9: "g",    # 草土地面
    11: "=",   # 石砖墙
    45: "c",   # 火把
    86: "v",   # 草丛
    GEM_SPRITE: "@",  # 宝石
}
CHAR_POOL = (
    list("ABCDEFGHIJKLMNOPQRSTUVWXYZ")
    + list("abdefhjkl nqtuyz".replace(" ", ""))
    + list("67890")
    + list("+-*/()[]{}<>,;:!?|_^&$%")
)


def load_tile_meta():
    """assets/tiles.meta.json 的默认属性：sprite -> {solid, platform, water, ...}。

    关卡图例没写的属性会回落到这张表（见 Level.ReadFlag），所以凡是这里默认 true、
    而参考工程其实没有的语义（压力板 / 水 / 单向平台…），必须在图例里显式写 false，
    否则会悄悄多出一条玩法。
    """
    path = os.path.join(ROOT, "src", "WindowsLoop", "assets", "tiles.meta.json")
    if not os.path.exists(path):
        return {}
    data = json.load(open(path, encoding="utf-8"))
    flags = ("solid", "platform", "water", "climb", "spring", "plate")
    return {t["sprite"]: {f: bool(t.get(f)) for f in flags} for t in data.get("tiles", [])}


TILE_META = load_tile_meta()

# 参考工程只用到碰撞，没有平台 / 水 / 攀爬 / 弹跳 / 压力板这些玩法属性
GAMEPLAY_FLAGS = ("platform", "water", "climb", "spring", "plate")


# ---------------------------------------------------------------- 图集

def parse_sheet_meta(meta_path):
    """返回 (sheet_guid, name -> rect, sprite fileID -> name)。"""
    txt = open(meta_path, encoding="utf-8", errors="replace").read()
    g = re.search(r"^guid: ([0-9a-f]{32})", txt, re.M)
    rects = {}
    for m in re.finditer(
        r"  - serializedVersion: \d+\s*\n\s*name: (\S+)\s*\n(.*?)rect:\s*\n"
        r"\s*serializedVersion: 2\s*\n\s*x: (-?\d+)\s*\n\s*y: (-?\d+)\s*\n"
        r"\s*width: (\d+)\s*\n\s*height: (\d+)",
        txt, re.S,
    ):
        rects[m.group(1)] = tuple(int(m.group(i)) for i in range(3, 7))
    ids = {m.group(1): m.group(2) for m in
           re.finditer(r"- first:\s*\n\s*213: (-?\d+)\s*\n\s*second: (\S+)", txt)}
    return (g.group(1) if g else None), rects, ids


def rect_index(rect):
    """Unity 左下角原点的 rect -> 英文书写顺序索引。"""
    x, y, w, h = rect
    if w != SHEET_CELL or h != SHEET_CELL:
        sys.exit(f"非 8x8 的 sprite：{rect}")
    return (SHEET_ROWS - 1 - y // SHEET_CELL) * SHEET_COLS + x // SHEET_CELL


def cell_image(img, index):
    x, y = index % SHEET_COLS, index // SHEET_COLS
    return img.crop((x * SHEET_CELL, y * SHEET_CELL,
                     x * SHEET_CELL + SHEET_CELL, y * SHEET_CELL + SHEET_CELL))


def build_sheets():
    """guid -> dict(name, rects, ids, img)。"""
    out = {}
    for path in glob.glob(os.path.join(SHEET_DIR, "*.png")):
        meta = path + ".meta"
        if not os.path.exists(meta):
            continue
        guid, rects, ids = parse_sheet_meta(meta)
        if not rects:
            continue
        out[guid] = dict(name=os.path.basename(path), rects=rects, ids=ids,
                         img=Image.open(path).convert("RGBA"))
    return out


SHEETS = build_sheets()
BASE_GUID = next(g for g, s in SHEETS.items() if s["name"] == BASE_SHEET)
BASE_IMG = SHEETS[BASE_GUID]["img"]

# 基准表的自检：sprite 名里的编号应等于由 rect 反算的索引
for name, rect in SHEETS[BASE_GUID]["rects"].items():
    n = int(name.rsplit("_", 1)[1])
    idx = rect_index(rect)
    if n != idx:
        sys.exit(f"{BASE_SHEET}: {name} 反算出 {idx}，与名字不符，映射规则有问题")


def detect_mirror_sheets():
    """找出哪些表是「基准表的整张水平镜像」，返回 set(guid)。

    判据：对全部 96 格都满足 cell[(r,c)] == 水平镜像( base[(r, 7-c)] )。
    """
    mirrored = set()
    for guid, sh in SHEETS.items():
        if guid == BASE_GUID:
            continue
        ok = True
        for i in range(SHEET_COLS * SHEET_ROWS):
            r, c = divmod(i, SHEET_COLS)
            want = cell_image(BASE_IMG, r * SHEET_COLS + (SHEET_COLS - 1 - c))
            want = want.transpose(Image.FLIP_LEFT_RIGHT)
            if sh["rects"] and cell_image(sh["img"], i).tobytes() != want.tobytes():
                ok = False
                break
        if ok:
            mirrored.add(guid)
    return mirrored


MIRROR_SHEETS = detect_mirror_sheets()
if REVERSE_SHEET and not any(SHEETS[g]["name"] == REVERSE_SHEET for g in MIRROR_SHEETS):
    sys.exit(f"{REVERSE_SHEET} 不再满足「整张水平镜像」的假设，先重新核对对应关系")
print("[sheets] 镜像表:", sorted(SHEETS[g]["name"] for g in MIRROR_SHEETS))


def build_tile_index():
    """瓦片资产 guid -> dict(sprite, xf, collider, name, gem)。"""
    out = {}
    for asset in glob.glob(os.path.join(ORIG, "Assets", "**", "*.asset"), recursive=True):
        meta = asset + ".meta"
        if not os.path.exists(meta):
            continue
        txt = open(asset, encoding="utf-8", errors="replace").read()
        tile_guid = re.search(r"^guid: ([0-9a-f]{32})", open(meta, encoding="utf-8", errors="replace").read(), re.M)
        sprite = re.search(r"m_Sprite: \{fileID: (-?\d+), guid: ([0-9a-f]{32})", txt)
        if not tile_guid or not sprite:
            continue
        name = re.search(r"m_Name: (.*)", txt).group(1).strip()
        ct = re.search(r"m_ColliderType: (\d+)", txt)
        sh = SHEETS.get(sprite.group(2))
        sprite_name = sh["ids"].get(sprite.group(1)) if sh else None
        rect = sh["rects"].get(sprite_name) if sprite_name else None

        entry = dict(name=name, sprite=None, xf=0,
                     collider=int(ct.group(1)) if ct else 0, gem="Gem" in name)
        if rect:
            idx = rect_index(rect)
            if sprite.group(2) in MIRROR_SHEETS:
                # 镜像表：内容 = 水平镜像( 基准表同行的镜像列 )
                r, c = divmod(idx, SHEET_COLS)
                entry["sprite"] = r * SHEET_COLS + (SHEET_COLS - 1 - c)
                entry["xf"] = 4
            else:
                entry["sprite"] = idx
        out[tile_guid.group(1)] = entry
    return out


TILES = build_tile_index()


# ---------------------------------------------------------------- 场景解析

def doc_split(text):
    parts = re.split(r"^--- !u!(\d+) &(\d+).*$", text, flags=re.M)
    return [(int(parts[i]), parts[i + 1], parts[i + 2]) for i in range(1, len(parts), 3)]


def block_lines(text, key):
    out, inside = [], False
    for ln in text.splitlines():
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


def parse_tilemap(body):
    m = re.search(r"^  m_Tiles:\n(.*?)^  m_AnimatedTiles", body, flags=re.M | re.S)
    if not m:
        return None
    tiles = []
    for blk in re.finditer(
        r"  - first: \{x: (-?\d+), y: (-?\d+), z: -?\d+\}\n    second:\n((?:      .*\n)+)",
        m.group(1),
    ):
        second = blk.group(3)
        mx = re.search(r"m_TileMatrixIndex: (\d+)", second)
        if mx and int(mx.group(1)) != 0:
            sys.exit("场景里有翻转 / 旋转的瓦片（m_TileMatrixIndex != 0），"
                     "需要先把矩阵换算成 xf 编码再继续")
        tiles.append((int(blk.group(1)), int(blk.group(2)),
                      int(re.search(r"m_TileIndex: (-?\d+)", second).group(1))))
    guids = []
    for ln in block_lines(body, "m_TileAssetArray"):
        if "m_Data:" in ln:
            g = re.search(r"guid: ([0-9a-f]{32})", ln)
            guids.append(g.group(1) if g else None)
    return tiles, guids


def read_scene(name):
    text = open(os.path.join(SCENE_DIR, name + ".unity"), encoding="utf-8", errors="replace").read()
    loop = re.search(
        r"LoopArea:\n    x: (-?\d+)\n    y: (-?\d+)\n    width: (\d+)\n    height: (\d+)", text)
    if not loop:
        raise SystemExit(f"{name}: 找不到 LoopArea")
    area = tuple(int(v) for v in loop.groups())

    docs = doc_split(text)
    names = {}
    for cid, _fid, body in docs:
        if cid == 1:
            nm = re.search(r"m_Name: (.*)", body)
            if nm:
                names[_fid] = nm.group(1).strip()

    layers = {}
    for cid, _fid, body in docs:
        if cid != 1839735485:      # Tilemap
            continue
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
        layer = names.get(go.group(1), "") if go else ""
        res = parse_tilemap(body)
        if res:
            layers[layer] = res

    # 只有带碰撞体的那层参与阻挡：Background 层在参考工程里没挂 Collider
    solid_layers = set()
    for cid, _fid, body in docs:
        if cid != 1839735485:
            continue
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
        gid = go.group(1) if go else None
        for cid2, _f2, b2 in docs:
            if cid2 in (50, 66, 61, 19719996):   # Rigidbody2D / *Collider2D
                if gid and f"m_GameObject: {{fileID: {gid}}}" in b2:
                    solid_layers.add(names.get(gid, ""))
    return area, layers, solid_layers


# ---------------------------------------------------------------- 生成关卡

def build_grid(scene):
    area, layers, solid_layers = read_scene(scene)
    x0, y0, w, h = area
    y_top = y0 + h - 1

    if "BaseMap" not in solid_layers:
        sys.exit(f"{scene}: 没认出哪层带碰撞体（solid_layers={solid_layers}），"
                 "阻挡会全丢，先修 read_scene 再跑")

    cells = [[None] * w for _ in range(h)]   # (sprite, solid, xf)
    gem = None

    # 背景层先铺、主图层后铺并覆盖（与参考工程的绘制顺序一致）
    for layer in ("Background", "BaseMap"):
        if layer not in layers:
            continue
        tiles, guids = layers[layer]
        for x, y, ti in tiles:
            guid = guids[ti] if 0 <= ti < len(guids) else None
            info = TILES.get(guid)
            if not info:
                sys.exit(f"瓦片资产 {guid} 没解析出来，不能静默跳过")
            r = (y_top - y) % h
            c = (x - x0) % w
            if info["gem"]:
                gem = [c, r]
                cells[r][c] = (GEM_SPRITE, False, 0)
                continue
            if info["sprite"] is None:
                sys.exit(f"瓦片 {info['name']} 找不到对应图集索引")
            if cells[r][c] is not None and layer == "Background":
                continue
            cells[r][c] = (info["sprite"], bool(info["collider"]) and layer in solid_layers,
                           info["xf"])
    return cells, (x0, y0, w, h), gem


def pick_spawn(cells, w, h, prefer_col):
    is_solid = lambda r, c: cells[r % h][c % w] is not None and cells[r % h][c % w][1]
    best = None
    for c in range(w):
        for r in range(2, h):
            if is_solid(r, c) and not is_solid(r - 1, c) and not is_solid(r - 2, c):
                score = abs(c - prefer_col)
                if best is None or score < best[0]:
                    best = (score, [c, r - 2])
                break
    return best[1] if best else [0, 0]


def main():
    grids = []
    for scene, pid, prefer in PIECES:
        cells, area, gem = build_grid(scene)
        grids.append((pid, cells, area, gem, prefer))

    # 图例：按 (sprite, solid) 分配字符，先分出现次数多的。朝向走 xf 层，不进图例。
    usage = {}
    for _pid, cells, _area, _gem, _p in grids:
        for row in cells:
            for cell in row:
                if cell:
                    key = (cell[0], cell[1])
                    usage[key] = usage.get(key, 0) + 1
    ordered = sorted(usage, key=lambda k: (-int(k[1]), -usage[k], k[0]))

    legend, taken = {}, set()
    for key in ordered:
        ch = PRESET_EXACT.get(key[0])
        if ch and ch not in taken:
            legend[key] = ch
            taken.add(ch)
    for key in ordered:
        if key in legend:
            continue
        for ch in CHAR_POOL:
            if ch not in taken and ch not in (".", " "):
                legend[key] = ch
                taken.add(ch)
                break
        else:
            sys.exit("图例字符不够用了")

    tiles_json = {}
    for key in ordered:
        ch = legend[key]
        sprite, solid = key
        meta = TILE_META.get(sprite, {})
        defn = {"sprite": sprite, "solid": bool(solid)}
        # 抵消瓦片元数据里那些会破坏还原结果的默认玩法属性
        for flag in GAMEPLAY_FLAGS:
            if meta.get(flag):
                defn[flag] = False
        tiles_json[ch] = defn

    pieces = []
    for pid, cells, (x0, y0, w, h), gem, prefer in grids:
        rows = ["".join(legend[(c[0], c[1])] if c else "." for c in row) for row in cells]
        xf = ["".join(("01234567"[c[2]] if c[2] else ".") if c else "."
                      for c in row) for row in cells]
        spawn = pick_spawn(cells, w, h, prefer)
        piece = {"id": pid, "rows": rows, "spawn": spawn}
        if any(ch != "0" for line in xf for ch in line if ch != "."):
            piece["xf"] = xf
        if gem:
            # evt 层里写的是「事件组下标」的字符，组 0 用 '0'
            piece["evt"] = [("." * gem[0] + "0" + "." * (w - gem[0] - 1)) if y == gem[1]
                            else "." * w for y in range(h)]
        pieces.append(piece)
        n_mirror = sum(1 for row in cells for c in row if c and c[2] & 4)
        print(f"  {pid:8s} {w}x{h}  spawn={spawn}  gem={gem}  "
              f"格数={sum(1 for r in cells for c in r if c)}  镜像格={n_mirror}")

    level = {
        "name": LEVEL_NAME,
        "maxWindows": len(pieces),
        "tiles": tiles_json,
        "pieces": pieces,
        "events": [
            {"name": "原宝石·触碰过关", "trigger": "touch",
             "actions": [{"type": "goto", "level": ""}]},
        ],
        "evtSeq": [[0]],
    }

    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, OUT_NAME + ".json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(level, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print(f"\n图例 {len(tiles_json)} 条：")
    for key in ordered:
        ch = legend[key]
        print(f"  {ch}  sprite={key[0]:<3d} solid={int(key[1])}  x{usage[key]}")
    print(f"\n写出 {out}")


if __name__ == "__main__":
    main()
