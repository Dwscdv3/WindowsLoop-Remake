import re, os

scene = r"D:/Workspaces/WindowsLoop-Remastered/ref-original/Assets/Scenes/Level-0.unity"
txt = open(scene, encoding="utf-8", errors="replace").read()
lines = txt.splitlines()
starts = [i for i, ln in enumerate(lines) if ln.strip().startswith("m_TileAssetArray:")]
for s in starts:
    print("---- at line", s)
    print("\n".join(lines[s:s + 12]))
    # find next top-level key
    j = s + 1
    n = 0
    while j < len(lines) and (lines[j].startswith("  - ") or lines[j].startswith("    ")):
        n += 1
        j += 1
    print(f"... entries lines: {n}")
