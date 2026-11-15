import re, os, json

ROOT = r"D:/Workspaces/WindowsLoop-Remastered/ref-original"
OUT = r"D:/Workspaces/WindowsLoop-Remastered/assets"


def parse_sprites(meta_path):
    txt = open(meta_path, encoding="utf-8", errors="replace").read()
    out = {}
    for m in re.finditer(
        r"- serializedVersion: \d+\n\s+name: (.*?)\n(?:\s+rect:\n(?:\s+serializedVersion: \d+\n)?\s+x: (-?\d+)\n\s+y: (-?\d+)\n\s+width: (\d+)\n\s+height: (\d+).*?)?\s+alignment:.*?\n\s+spriteID: ([0-9a-f]{32})",
        txt, flags=re.S):
        name, x, y, w, h, sid = m.groups()
        if x is None:
            continue
        out[sid] = dict(name=name.strip(), x=int(x), y=int(y), w=int(w), h=int(h))
    return out


for meta in ["Assets/Resources/Tile/Blocks.psd.meta",
             "Assets/Resources/cavesofgallet/cavesofgallet_tiles.png.meta",
             "Assets/Resources/cavesofgallet/cavesofgallet.png.meta"]:
    p = os.path.join(ROOT, meta)
    sp = parse_sprites(p)
    print(os.path.basename(meta), "sprites:", len(sp))
    for k, v in list(sp.items())[:20]:
        print("   ", k, v)
