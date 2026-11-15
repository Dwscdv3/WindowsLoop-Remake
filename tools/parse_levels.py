"""Extract tilemap layouts from Unity scenes -> readable ASCII maps."""
import re, sys, os, glob

ROOT = r"D:/Workspaces/WindowsLoop-Remastered/ref-original"

# guid -> asset path
guid2path = {}
for meta in glob.glob(os.path.join(ROOT, "Assets", "**", "*.asset.meta"), recursive=True):
    txt = open(meta, encoding="utf-8").read()
    m = re.search(r"guid: ([0-9a-f]{32})", txt)
    if m:
        guid2path[m.group(1)] = meta[:-5]

# classify tile assets
ROLE = {}
for guid, path in guid2path.items():
    txt = open(path, encoding="utf-8", errors="replace").read()
    name = re.search(r"m_Name: (.*)", txt)
    name = name.group(1).strip() if name else os.path.basename(path)
    ct = re.search(r"m_ColliderType: (\d+)", txt)
    ct = int(ct.group(1)) if ct else 0
    if "Gem" in name:
        ROLE[guid] = ("G", "gem:" + name)
    elif ct != 0:
        ROLE[guid] = ("#", "solid:" + name)
    else:
        ROLE[guid] = (".", "deco:" + name)


def doc_split(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    parts = re.split(r"^--- !u!(\d+) &(\d+).*$", txt, flags=re.M)
    docs = []
    for i in range(1, len(parts), 3):
        docs.append((int(parts[i]), parts[i + 1], parts[i + 2]))
    return docs


def block_lines(text, key):
    lines = text.splitlines()
    out = []
    inside = False
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
    body = m.group(1)
    tiles = []
    for blk in re.finditer(
        r"  - first: \{x: (-?\d+), y: (-?\d+), z: -?\d+\}\n    second:\n((?:      .*\n)+)",
        body,
    ):
        x, y = int(blk.group(1)), int(blk.group(2))
        idx = re.search(r"m_TileIndex: (-?\d+)", blk.group(3))
        tiles.append((x, y, int(idx.group(1)) if idx else -1))
    arr = block_lines(obj_text, "m_TileAssetArray")
    guids = []
    for ln in arr:
        if "m_Data:" in ln:
            g = re.search(r"guid: ([0-9a-f]{32})", ln)
            guids.append(g.group(1) if g else None)
    return tiles, guids


def render(tiles, guids, label, show_legend=False):
    if not tiles:
        return
    xs = [t[0] for t in tiles]
    ys = [t[1] for t in tiles]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    grid = [[" "] * (x1 - x0 + 1) for _ in range(y1 - y0 + 1)]
    legend = {}
    for x, y, i in tiles:
        g = guids[i] if 0 <= i < len(guids) else None
        ch, desc = ROLE.get(g, ("?", "unknown"))
        legend[ch] = desc
        grid[y1 - y][x - x0] = ch
    print(f"\n### {label}  tiles={len(tiles)}  bounds x[{x0},{x1}] y[{y0},{y1}]  size {x1-x0+1}x{y1-y0+1}")
    if show_legend:
        for ch, desc in sorted(legend.items()):
            print(f"    '{ch}' = {desc}")
    print("      " + "".join(str((x0 + i) // 10 % 10) for i in range(x1 - x0 + 1)))
    for row_i, row in enumerate(grid):
        y = y1 - row_i
        print(f"  y{y:>4}|" + "".join(row) + "|")


for scene in sys.argv[1:]:
    print("=" * 100)
    print(os.path.basename(scene))
    txt = open(scene, encoding="utf-8", errors="replace").read()
    loop = re.search(
        r"LoopArea:\n    x: (-?\d+)\n    y: (-?\d+)\n    width: (\d+)\n    height: (\d+)", txt
    )
    if loop:
        print("LoopArea:", loop.groups())
    docs = doc_split(scene)
    names = {}
    for cid, fid, body in docs:
        if cid == 1:
            nm = re.search(r"m_Name: (.*)", body)
            if nm:
                names[fid] = nm.group(1)
    for cid, fid, body in docs:
        if cid == 1839735485:
            go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
            goid = go.group(1) if go else "?"
            res = parse_tilemap(body)
            if res:
                render(res[0], res[1], f"Tilemap go={names.get(goid, goid)}")
