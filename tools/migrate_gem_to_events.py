#!/usr/bin/env python3
"""
一次性迁移：把关卡里的 "gem" 字段改成「宝石瓦片 + 该格一条网格事件」。

  python tools/migrate_gem_to_events.py [--dry-run]

为什么要迁：
  宝石以前是 Piece 上的一个特殊字段，游戏里单独一张贴图 + 专门的过关判定。
  现在宝石就是图集里的普通瓦片（sprite 93），"碰到会怎样"交给网格事件层（evt + events）。

迁移后的形状：
  · 该格换成宝石瓦片（图例里新加一个字符，指向 sprite 93）
  · piece 里多一层 evt，那一格写上事件下标
  · 关卡级 events 里多一条 {"trigger":"touch","action":{"type":"goto","level":""}}
    目标关卡**故意留空** —— 跳转链由关卡编辑器里逐关填，脚本不猜。

脚本是幂等的：没有 gem 字段的关卡原样不动。原文件先备份到
.workbuddy/backup/gem-migration-<时间戳>/。
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LEVELS = ROOT / "src" / "WindowsLoop" / "assets" / "levels"
BACKUP_ROOT = ROOT / ".workbuddy" / "backup"

GEM_SPRITE = 93
GEM_KEY_CANDIDATES = "@%&*+!:;?$^"
GEOM_BLANK = "."


def evt_char(index: int) -> str:
    """和引擎 / 编辑器同一套编码：0-9, a-z(10-35), A-Z(36-61)。"""
    if index < 0:
        return GEOM_BLANK
    if index < 10:
        return chr(ord("0") + index)
    if index < 36:
        return chr(ord("a") + index - 10)
    if index < 62:
        return chr(ord("A") + index - 36)
    return GEOM_BLANK


def pick_key(doc: dict) -> str | None:
    """挑一个这一关完全没用到的字符当宝石图例（图例里没有、格子内容里也没有）。"""
    used = set(doc.get("tiles", {}).keys())
    for p in doc.get("pieces", []):
        for row in p.get("rows", []):
            used.update(row)
    for c in GEM_KEY_CANDIDATES:
        if c not in used:
            return c
    return None


def pad_layer(layer: list[str] | None, h: int, w: int) -> list[str]:
    rows = list(layer or [])
    rows = [r.ljust(w, GEOM_BLANK)[:w] for r in rows]
    while len(rows) < h:
        rows.append(GEOM_BLANK * w)
    return rows[:h]


def set_char(rows: list[str], x: int, y: int, c: str) -> None:
    row = rows[y]
    rows[y] = row[:x] + c + row[x + 1:]


def migrate(path: Path, dry: bool) -> str:
    raw = path.read_text(encoding="utf-8")
    doc = json.loads(raw)

    gems = [(i, p) for i, p in enumerate(doc.get("pieces", [])) if p.get("gem")]
    if not gems:
        return "skip（没有 gem）"

    key = pick_key(doc)
    if key is None:
        return "SKIP（找不到空闲字符当宝石图例）"

    doc.setdefault("tiles", {})[key] = {"sprite": GEM_SPRITE}
    events = doc.setdefault("events", [])

    notes = []
    for _, piece in gems:
        gx, gy = piece["gem"]
        rows = piece["rows"]
        w, h = len(rows[0]), len(rows)

        if not (0 <= gy < h and 0 <= gx < w):
            notes.append(f"{piece.get('id')} 的 gem 超出地图，已跳过")
            del piece["gem"]
            continue

        before = rows[gy][gx]
        if before not in (".", " "):
            notes.append(f"{piece.get('id')} 的 gem 格原本是 '{before}'，已被宝石覆盖")

        evt = pad_layer(piece.get("evt"), h, w)
        index = len(events)
        if index > 61:
            notes.append(f"{piece.get('id')}：事件超过 62 条，这一格没挂上事件")
        else:
            set_char(rows, gx, gy, key)
            set_char(evt, gx, gy, evt_char(index))
            piece["evt"] = evt
            events.append({
                "name": "原宝石·触碰过关",
                "trigger": "touch",
                "action": {"type": "goto", "level": ""},
            })
            notes.append(f"{piece.get('id')} ({gx},{gy}) → 瓦片 '{key}' + 事件 #{index}（目标留空）")

        del piece["gem"]

    if not dry:
        out = json.dumps(doc, ensure_ascii=False, indent=2) + "\n"
        path.write_text(out, encoding="utf-8")
    return "；".join(notes)


def main() -> None:
    ap = argparse.ArgumentParser(description="migrate level gem -> gem tile + grid event")
    ap.add_argument("--dry-run", action="store_true", help="只打印计划，不写文件")
    args = ap.parse_args()

    if not LEVELS.exists():
        sys.exit(f"levels dir not found: {LEVELS}")

    files = sorted(f for f in LEVELS.glob("*.json") if "副本" not in f.name)
    if not files:
        sys.exit("no level json found")

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = BACKUP_ROOT / f"gem-migration-{stamp}"
    if not args.dry_run:
        backup.mkdir(parents=True, exist_ok=True)

    for f in files:
        if not args.dry_run:
            shutil.copy2(f, backup / f.name)
        print(f"{f.name}: {migrate(f, args.dry_run)}")

    print()
    print(f"{'[dry-run] ' if args.dry_run else ''}备份目录: {backup}")


if __name__ == "__main__":
    main()
