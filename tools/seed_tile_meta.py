#!/usr/bin/env python3
"""
生成 assets/tiles.meta.json 的初值（名称 / 分组 / 默认阻挡）。

这只是**一次性播种**：文件生成后就由 WindowsLoop.TileEditor 手工维护，
不要把它挂进构建流程，否则会覆盖你在工具里做的修改。

  python tools/seed_tile_meta.py            # 写文件（已存在时先备份为 .bak）
  python tools/seed_tile_meta.py --force    # 覆盖，不备份
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "WindowsLoop" / "assets"
META = ASSETS / "tiles.meta.json"

G_GROUND = "地面"
G_STONE = "石墙"
G_SAND = "砂岩"
G_WOOD = "木构"
G_CAVE = "洞壁"
G_PROP = "物件"
G_WATER = "水"
G_SNOW = "雪"
G_LAVA = "岩浆"

# sprite: (名称, 分组, solid, water, climb, spring, note)
#
# 判定口径：**默认阻挡 = 这块瓦片整体看起来是一坨实心材质**
#   - 满铺不透明的岩体/深蓝底        → 实心
#   - 有边、能拼成地形的结构块（草地/石墙/砂岩/木台/雪坡） → 实心
#   - 墙上花纹（碎石斑、蓝竖条、红带）、液体、物件        → 不实心
# 这只是初值：工具里随时改，改完游戏和编辑器都跟着变。
T = {
    # ── 洞壁：洞穴里的岩石纹理。大多是"深底 + 碎石斑"，当花纹用，不实心；
    #    只有两种满铺的深蓝板（83、90）是真正的岩体。
    0: ("洞壁·空", G_CAVE, False, False, False, False, "整格全透明，跟背景同色，只适合当看不见的填充"),
    1: ("洞壁·碎石 1", G_CAVE, False, False, False, False, None),
    2: ("洞壁·碎石 2", G_CAVE, False, False, False, False, None),
    3: ("洞壁·碎石 3", G_CAVE, False, False, False, False, None),
    4: ("洞壁·碎石 4", G_CAVE, False, False, False, False, None),
    5: ("洞壁·碎石 5", G_CAVE, False, False, False, False, None),
    6: ("洞壁·碎石 6", G_CAVE, False, False, False, False, None),
    7: ("洞壁·碎石 7", G_CAVE, False, False, False, False, None),

    # ── 草地 3x3 + 斜坡：地形骨架
    8: ("草地·左上", G_GROUND, True, False, False, False, None),
    9: ("草地·上", G_GROUND, True, False, False, False, None),
    10: ("草地·右上", G_GROUND, True, False, False, False, None),
    16: ("草地·左", G_GROUND, True, False, False, False, None),
    17: ("草地·中", G_GROUND, True, False, False, False, None),
    18: ("草地·右", G_GROUND, True, False, False, False, None),
    24: ("草地·左下", G_GROUND, True, False, False, False, None),
    25: ("草地·下", G_GROUND, True, False, False, False, None),
    26: ("草地·右下", G_GROUND, True, False, False, False, None),
    85: ("草地·斜坡", G_GROUND, True, False, False, False, "引擎没有斜坡物理，实际按整格实心算"),
    86: ("草丛 1", G_GROUND, False, False, False, False, "纯装饰，站在上面不会有碰撞"),
    87: ("草丛 2", G_GROUND, False, False, False, False, None),

    # ── 石墙 3x3 + 毛石 + 立柱
    11: ("石墙·左上", G_STONE, True, False, False, False, None),
    12: ("石墙·上", G_STONE, True, False, False, False, None),
    13: ("石墙·右上", G_STONE, True, False, False, False, None),
    19: ("石墙·左", G_STONE, True, False, False, False, None),
    20: ("石墙·中", G_STONE, True, False, False, False, None),
    27: ("石墙·左下", G_STONE, True, False, False, False, None),
    28: ("石墙·下", G_STONE, True, False, False, False, None),
    32: ("石墙·裂纹", G_STONE, True, False, False, False, None),
    33: ("石墙·毛石 1", G_STONE, True, False, False, False, None),
    34: ("石墙·毛石 2", G_STONE, True, False, False, False, None),
    40: ("石柱·左", G_STONE, True, False, False, False, None),
    42: ("石柱·右", G_STONE, True, False, False, False, None),
    50: ("石柱·转角", G_STONE, True, False, False, False, None),

    # ── 砂岩 3x3
    35: ("砂岩·左上", G_SAND, True, False, False, False, None),
    36: ("砂岩·上", G_SAND, True, False, False, False, None),
    37: ("砂岩·右上", G_SAND, True, False, False, False, None),
    43: ("砂岩·左", G_SAND, True, False, False, False, None),
    44: ("砂岩·中", G_SAND, True, False, False, False, None),
    51: ("砂岩·左下", G_SAND, True, False, False, False, None),
    52: ("砂岩·下", G_SAND, True, False, False, False, None),

    # ── 木质台面 / 木板
    14: ("木台·上", G_WOOD, True, False, False, False, None),
    15: ("木台·斜撑", G_WOOD, True, False, False, False, None),
    22: ("木台·左上", G_WOOD, True, False, False, False, None),
    23: ("木板", G_WOOD, True, False, False, False, None),
    30: ("木柱", G_WOOD, True, False, False, False, None),

    # ── 梯子：可攀爬，不实心
    54: ("木梯", G_WOOD, False, False, True, False, "真正的梯子贴图；level2 / bg-test 里那个 'L' 还是旧的 65（水纹）"),
    55: ("石梯", G_WOOD, False, False, True, False, None),

    # ── 液体：都不实心
    29: ("水洼·碎石", G_WATER, False, True, False, False, None),
    63: ("水面·顶", G_WATER, False, True, False, False, None),
    65: ("瀑布·细", G_WATER, False, True, False, False, None),
    73: ("瀑布·沫", G_WATER, False, True, False, False, None),
    75: ("瀑布·宽", G_WATER, False, True, False, False, None),
    80: ("水面", G_WATER, False, True, False, False, None),
    81: ("水面·晶", G_WATER, False, True, False, False, None),
    82: ("水面·沫", G_WATER, False, True, False, False, None),

    # ── 岩浆：引擎没有伤害，当液体处理
    68: ("岩浆·满", G_LAVA, False, False, False, False, None),
    76: ("岩浆·流", G_LAVA, False, False, False, False, None),
    84: ("岩浆·壳", G_LAVA, False, False, False, False, None),

    # ── 雪坡
    70: ("雪坡·右", G_SNOW, True, False, False, False, None),
    71: ("雪坡·左", G_SNOW, True, False, False, False, None),
    72: ("雪坡·左上", G_SNOW, True, False, False, False, None),
    74: ("雪坡·左下", G_SNOW, True, False, False, False, None),
    78: ("雪坡·上", G_SNOW, True, False, False, False, None),
    79: ("雪坡·右上", G_SNOW, True, False, False, False, None),

    # ── 物件：一律不实心
    21: ("灯柱", G_PROP, False, False, False, False, None),
    31: ("红晶簇", G_PROP, False, False, False, False, None),
    38: ("红晶·小", G_PROP, False, False, False, False, None),
    39: ("红晶·大", G_PROP, False, False, False, False, None),
    41: ("蓝晶碎", G_PROP, False, False, False, False, None),
    45: ("火把", G_PROP, False, False, False, False, None),
    46: ("木梁·横", G_PROP, False, False, False, False, "看着像能站的横梁，实际很薄；要站人请用 S 手动标阻挡"),
    47: ("石梁·横", G_PROP, False, False, False, False, None),
    48: ("蓝晶·小", G_PROP, False, False, False, False, None),
    49: ("蓝晶·大", G_PROP, False, False, False, False, "level2 里的 'J' 弹簧用的是这块，贴图其实不是弹簧"),
    60: ("红珊瑚", G_PROP, False, False, False, False, None),
    62: ("水晶·碎片", G_PROP, False, False, False, False, None),
    64: ("壁龛·有水", G_PROP, False, False, False, False, None),
    67: ("红幔", G_PROP, False, False, False, False, None),
    77: ("石堆", G_PROP, True, False, False, False, "一堆挡路的巨石，默认实心"),

    # ── 洞壁的其它花色
    53: ("洞壁·碎点", G_CAVE, False, False, False, False, "几乎空，基本没东西"),
    56: ("洞壁·蓝块", G_CAVE, False, False, False, False, None),
    57: ("洞壁·苔块", G_CAVE, False, False, False, False, None),
    58: ("洞壁·蓝斑", G_CAVE, False, False, False, False, None),
    59: ("洞壁·碎石带", G_CAVE, False, False, False, False, None),
    61: ("洞壁·岩块", G_CAVE, False, False, False, False, None),
    66: ("洞壁·岩块 2", G_CAVE, False, False, False, False, None),
    69: ("洞壁·碎石底", G_CAVE, False, False, False, False, None),
    83: ("洞壁·深蓝板", G_CAVE, True, False, False, False, "满铺不透明，当岩体用"),
    88: ("洞壁·蓝斑 2", G_CAVE, False, False, False, False, None),
    89: ("洞壁·蓝竖条", G_CAVE, False, False, False, False, None),
    90: ("洞壁·纯色板", G_CAVE, True, False, False, False, "满铺不透明，当岩体用"),
    91: ("洞壁·蓝竖条 2", G_CAVE, False, False, False, False, None),
    92: ("洞壁·红带", G_CAVE, False, False, False, False, None),
}


def build() -> dict:
    from PIL import Image  # 只用来确认图集里确实有这么多格

    sheet = Image.open(ASSETS / "tiles.png")
    count = (sheet.width // 8) * (sheet.height // 8)

    tiles = []
    for sprite in sorted(T):
        name, group, solid, water, climb, spring, note = T[sprite]
        if sprite >= count:
            sys.exit(f"sprite {sprite} 超出图集范围 (0..{count - 1})")
        entry = {
            "sprite": sprite,
            "name": name,
            "group": group,
            "solid": solid,
            "water": water,
            "climb": climb,
            "spring": spring,
        }
        if note:
            entry["note"] = note
        tiles.append(entry)

    missing = [i for i in range(count) if i not in T]
    if missing:
        print(f"[seed] WARN 图集里还有 {len(missing)} 格没写初值：{missing}")
    return {"version": 1, "tiles": tiles}


def main() -> None:
    data = build()
    if META.exists():
        if "--force" in sys.argv:
            pass
        else:
            backup = META.with_suffix(".json.bak")
            backup.write_bytes(META.read_bytes())
            print(f"[seed] 已有文件，先备份到 {backup.relative_to(ROOT)}")
    META.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"[seed] 写入 {META.relative_to(ROOT)}：{len(data['tiles'])} 条")


if __name__ == "__main__":
    main()
