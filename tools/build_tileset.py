#!/usr/bin/env python3
"""
瓦片素材的切分与打包。

源素材是一张 8x8 网格的图集（8 列 x 12 行 = 96 格，每格 8x8 像素）。
本脚本把它拆成按材质族分目录的独立 PNG，赋予语义化文件名；
需要时再按**固定索引顺序**拼回一张图集，供引擎按数字索引取图。

  python tools/build_tileset.py           # 切分 + 打包（默认）
  python tools/build_tileset.py --split   # 只切分
  python tools/build_tileset.py --pack    # 只打包

索引顺序是稳定的契约：关卡 JSON 里存的是 sprite 数字，
所以 TILE_MAP 的键就是索引，改文件名不会影响任何关卡数据。
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required: python -m pip install pillow")

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "WindowsLoop" / "assets"
ATLAS = ASSETS / "tiles.png"
ATLAS_BACKUP = ASSETS / "tiles-original.png"
TILE_DIR = ASSETS / "tiles"

CELL = 8
COLS = 8
ROWS = 12
BASE_COUNT = COLS * ROWS          # 96
DERIVED_START = 96                # 补齐出来的切片从 96 开始

# ---------------------------------------------------------------- 命名表
# 键 = 图集索引（稳定契约），值 = 相对 assets/tiles 的路径（不含 .png）
TILE_MAP: dict[int, str] = {
    # ---- 洞穴底：无方向纹理，等价变体，编号即可 ----
    0: "cave/floor-00", 1: "cave/floor-01", 2: "cave/floor-02", 3: "cave/floor-03",
    4: "cave/floor-04", 5: "cave/floor-05", 6: "cave/floor-06", 7: "cave/floor-07",
    32: "cave/floor-08", 53: "cave/floor-09", 56: "cave/floor-10", 58: "cave/floor-11",
    59: "cave/floor-12", 61: "cave/floor-13", 62: "cave/floor-14", 66: "cave/floor-15",
    83: "cave/floor-16", 88: "cave/floor-17", 89: "cave/floor-18", 91: "cave/floor-19",
    57: "cave/floor-moss",
    69: "cave/floor-stars",
    90: "cave/floor-plain-blue",
    92: "cave/floor-red-band",

    # ---- 草地 3x3 ----
    8: "terrain/grass/grass-top-left", 9: "terrain/grass/grass-top-mid",
    10: "terrain/grass/grass-top-right",
    16: "terrain/grass/grass-mid-left", 17: "terrain/grass/grass-mid-mid",
    18: "terrain/grass/grass-mid-right",
    24: "terrain/grass/grass-bottom-left", 25: "terrain/grass/grass-bottom-mid",
    26: "terrain/grass/grass-bottom-right",
    85: "terrain/grass/grass-slope",
    86: "terrain/grass/grass-tuft-01", 87: "terrain/grass/grass-tuft-02",

    # ---- 灰石 3x3 + 大砖 + 竖条 ----
    11: "terrain/stone/stone-top-left", 12: "terrain/stone/stone-top-mid",
    13: "terrain/stone/stone-top-right",
    19: "terrain/stone/stone-mid-left", 20: "terrain/stone/stone-mid-mid",
    27: "terrain/stone/stone-bottom-left", 28: "terrain/stone/stone-bottom-mid",
    29: "terrain/stone/stone-bottom-right",
    33: "terrain/stone/stone-block-01", 34: "terrain/stone/stone-block-02",
    40: "terrain/stone/stone-pillar-left", 42: "terrain/stone/stone-pillar-right",
    50: "terrain/stone/stone-pillar-right-02",

    # ---- 砂岩 3x3（中右 / 右下 由补齐生成）----
    35: "terrain/sandstone/sandstone-top-left", 36: "terrain/sandstone/sandstone-top-mid",
    37: "terrain/sandstone/sandstone-top-right",
    43: "terrain/sandstone/sandstone-mid-left", 44: "terrain/sandstone/sandstone-mid-mid",
    51: "terrain/sandstone/sandstone-bottom-left", 52: "terrain/sandstone/sandstone-bottom-mid",

    # ---- 木质台面 / 木板 ----
    14: "terrain/wood/wood-beam-top", 15: "terrain/wood/wood-beam-slope",
    22: "terrain/wood/wood-beam-top-left", 23: "terrain/wood/wood-plank",
    30: "terrain/wood/wood-beam-right", 31: "terrain/wood/wood-plank-half",

    # ---- 单体物件 ----
    21: "prop/lamp-post", 45: "prop/torch",
    38: "prop/crystal-red-small", 39: "prop/crystal-red-large",
    41: "prop/crystal-blue-scatter", 48: "prop/crystal-blue-small",
    49: "prop/crystal-blue-large",
    54: "prop/ladder-wood", 55: "prop/ladder-stone",
    46: "prop/beam-wood", 47: "prop/beam-stone",
    64: "prop/alcove", 67: "prop/door-frame",
    60: "prop/coral-red", 77: "prop/rock-pile",

    # ---- 水面 / 瀑布 ----
    80: "water/surface", 81: "water/surface-crystal", 82: "water/surface-foam",
    63: "water/surface-top",
    65: "water/waterfall-narrow", 73: "water/waterfall-foam", 75: "water/waterfall-wide",

    # ---- 雪坡 ----
    70: "snow/snow-slope-right", 71: "snow/snow-slope-left",
    72: "snow/snow-slope-top-left", 74: "snow/snow-slope-bottom-left",
    78: "snow/snow-slope-mid", 79: "snow/snow-slope-right-02",

    # ---- 岩浆 ----
    68: "lava/lava-plain", 76: "lava/lava-flow", 84: "lava/lava-crust",

    # ---- 道具（素材来自图集外的独立 PNG，见 EXTERNAL）----
    93: "prop/gem",
}

# 原图集里这两格是空的（alpha=0），不导出
EMPTY = {94, 95}

# 有些精灵不是从原图集里切出来的，而是单独的 PNG（例如宝石）。
# 索引 → assets 下的源文件。split 直接拷过去、不从图集裁；pack 照常从 tiles/ 里读，
# 所以再跑多少次 pack 都不会把这块补丁冲掉。
EXTERNAL: dict[int, str] = {
    93: "gem.png",
}

# 透明键：这套素材统一用 (33,38,63) 的洞底纯色当背景。
#
# 整个图集一律键出。前提是游戏窗口底色也必须设成同一个 (33,38,63)（见
# GameWindow.Palette.BackgroundDark）——那样"没东西的地方"露出的还是这个色，
# 视觉和键出前完全一样；而有背景层的格子里，洞会透出被压暗的背景纹理，层次才对。
#
# 之前试过只键出装饰件、底色仍留 (18,21,32)，结果是草丛之类会显出一块比背景亮的
# 方块；反过来只改底色不键出，有背景层的关卡里前景洞底又会和压暗的背景打架。
# 两件事必须一起做。
COLOR_KEY = (33, 38, 63)

# 不做镜像补齐。这套素材本身只有边角/中心几个变体，方向差异由使用者在编辑器里
# 逐格翻转/旋转解决（关卡格式的 xf 层），素材层保持原样、不引入"凭空造出来的"图块。
DERIVED: dict[int, tuple[int, str, str]] = {}

ALL_COUNT = BASE_COUNT


def atlas_image() -> Image.Image:
    """读取图集；若存在备份则优先用备份，避免对已打包结果二次切分。"""
    src = ATLAS_BACKUP if ATLAS_BACKUP.exists() else ATLAS
    if not src.exists():
        sys.exit(f"atlas not found: {src}")
    im = Image.open(src).convert("RGBA")
    if im.width < COLS * CELL or im.height < ROWS * CELL:
        sys.exit(f"unexpected atlas size {im.size}, need at least {COLS * CELL}x{ROWS * CELL}")
    return im


def apply_color_key(tile: Image.Image) -> Image.Image:
    """把透明键颜色改成全透明。"""
    px = tile.load()
    for y in range(CELL):
        for x in range(CELL):
            if px[x, y][:3] == COLOR_KEY:
                px[x, y] = (0, 0, 0, 0)
    return tile


def cell_box(index: int) -> tuple[int, int, int, int]:
    x, y = index % COLS, index // COLS
    return x * CELL, y * CELL, x * CELL + CELL, y * CELL + CELL


def do_split() -> None:
    im = atlas_image()
    if not ATLAS_BACKUP.exists():
        shutil.copy2(ATLAS, ATLAS_BACKUP)
        print(f"[tiles] backup original atlas -> {ATLAS_BACKUP.relative_to(ROOT)}")

    written = 0
    for idx, rel in sorted(TILE_MAP.items()):
        out = TILE_DIR / f"{rel}.png"
        out.parent.mkdir(parents=True, exist_ok=True)

        # 图集外的独立素材：直接拷，别从原图集那块空位裁（会把宝石擦成透明）
        if idx in EXTERNAL:
            src = ASSETS / EXTERNAL[idx]
            if not src.exists():
                sys.exit(f"external tile missing: {src}")
            shutil.copy2(src, out)
            written += 1
            continue

        tile = apply_color_key(im.crop(cell_box(idx)))
        tile.save(out)
        written += 1

    for idx, (src_idx, op, rel) in DERIVED.items():
        out = TILE_DIR / f"{rel}.png"
        out.parent.mkdir(parents=True, exist_ok=True)
        tile = im.crop(cell_box(src_idx))
        if op == "flip-h":
            tile = tile.transpose(Image.FLIP_LEFT_RIGHT)
        elif op == "flip-v":
            tile = tile.transpose(Image.FLIP_TOP_BOTTOM)
        else:
            sys.exit(f"unknown transform {op}")
        tile.save(out)
        print(f"[tiles] derived {rel} <- {src_idx} ({op})")
        written += 1

    missing = [i for i in range(BASE_COUNT)
               if i not in TILE_MAP and i not in EMPTY and i not in EXTERNAL]
    if missing:
        print(f"[tiles] WARN {len(missing)} cells unnamed: {missing}")

    print(f"[tiles] split {written} files -> {TILE_DIR.relative_to(ROOT)}")


def do_pack() -> None:
    rows = (ALL_COUNT + COLS - 1) // COLS
    sheet = Image.new("RGBA", (COLS * CELL, rows * CELL), (0, 0, 0, 0))

    by_index: dict[int, str] = dict(TILE_MAP)
    for idx, (_, _, rel) in DERIVED.items():
        by_index[idx] = rel

    missing = []
    for idx in range(ALL_COUNT):
        if idx in EMPTY:
            continue
        rel = by_index.get(idx)
        if rel is None:
            missing.append(idx)
            continue
        path = TILE_DIR / f"{rel}.png"
        if not path.exists():
            missing.append(idx)
            continue
        tile = Image.open(path).convert("RGBA")
        if tile.size != (CELL, CELL):
            sys.exit(f"{path.name} is {tile.size}, must be {CELL}x{CELL}")
        sheet.paste(tile, (idx % COLS * CELL, idx // COLS * CELL))

    if missing:
        sys.exit(f"pack aborted, missing files for indices {missing}")

    try:
        sheet.save(ATLAS)
    except PermissionError:
        # 编辑器或游戏用 new Bitmap(path) 打开图集时会一直持有句柄，Windows 下连替换都不让
        sys.exit(f"[tiles] ERROR: {ATLAS.name} 被占用，先关掉关卡编辑器 / 游戏再打包")
    print(f"[tiles] packed {COLS}x{rows} = {ALL_COUNT} cells -> {ATLAS.relative_to(ROOT)}")


def main() -> None:
    ap = argparse.ArgumentParser(description="split / pack tile sheet")
    ap.add_argument("--split", action="store_true", help="split atlas into single files")
    ap.add_argument("--pack", action="store_true", help="pack single files back into atlas")
    args = ap.parse_args()

    do_split_only = args.split and not args.pack
    do_pack_only = args.pack and not args.split

    if do_pack_only:
        do_pack()
    elif do_split_only:
        do_split()
    else:
        do_split()
        do_pack()


if __name__ == "__main__":
    main()
