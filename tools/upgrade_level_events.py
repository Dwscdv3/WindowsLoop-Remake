#!/usr/bin/env python3
"""
一次性升级：把关卡里的事件从旧形状改成任务计划程序那套形状。

  action: {...}   →  actions: [ {...} ]
  when: [...]     →  conditions: [...]
  evt 层（字符=事件下标）→ 补一张 evtSeq 组表，字符变成"组下标"

脚本幂等：已经是新格式的关卡会原样跳过（只会打印 skip）。
原文件先备份到 .workbuddy/backup/evt-format-<时间戳>/。

  python tools/upgrade_level_events.py [--dry-run]
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


def upgrade(doc: dict) -> list[str]:
    notes: list[str] = []

    events = doc.get("events")
    if isinstance(events, list):
        for i, ev in enumerate(events):
            if not isinstance(ev, dict):
                continue
            if isinstance(ev.get("action"), dict):
                ev.setdefault("actions", [ev.pop("action")])
                notes.append(f"事件 #{i}: action -> actions")
            if isinstance(ev.get("when"), list):
                ev.setdefault("conditions", ev.pop("when"))
                notes.append(f"事件 #{i}: when -> conditions")

    # 旧 evt 层直接存事件下标，等价组表就是恒等映射
    if "evtSeq" not in doc:
        has_evt = any(
            p.get("evt") for p in doc.get("pieces", []) if isinstance(p, dict)
        )
        if has_evt:
            count = len(events or [])
            doc["evtSeq"] = [[i] for i in range(count)]
            notes.append(f"补 evtSeq（{count} 组，恒等映射）")

    return notes


def main() -> None:
    ap = argparse.ArgumentParser(description="upgrade level event format")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    if not LEVELS.exists():
        sys.exit(f"levels dir not found: {LEVELS}")

    files = sorted(f for f in LEVELS.glob("*.json") if "副本" not in f.name)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = BACKUP_ROOT / f"evt-format-{stamp}"
    if not args.dry_run:
        backup.mkdir(parents=True, exist_ok=True)

    for f in files:
        doc = json.loads(f.read_text(encoding="utf-8"))
        before = json.dumps(doc, ensure_ascii=False, sort_keys=True)
        notes = upgrade(doc)
        after = json.dumps(doc, ensure_ascii=False, sort_keys=True)

        if not notes or before == after:
            print(f"{f.name}: skip（已经是新格式）")
            continue

        if not args.dry_run:
            shutil.copy2(f, backup / f.name)
            f.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"{f.name}: " + "；".join(notes))

    print()
    print(f"{'[dry-run] ' if args.dry_run else ''}备份目录: {backup}")


if __name__ == "__main__":
    main()
