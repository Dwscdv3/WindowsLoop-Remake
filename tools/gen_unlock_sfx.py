"""
生成「开锁」音效：assets/audio/unlock.wav（纯标准库）

    python tools/gen_unlock_sfx.py

音效结构（约 1.0 秒）—— 先机械、后确认，听感上"锁开了"：
    1) 0.00s  钥匙齿插进锁芯的金属"嗒"（3.1kHz 窄带噪声，6ms 衰减）
    2) 0.05s  弹子落位第二声（1.9kHz，5ms 衰减），两声错开才有"机构在动"的感觉
    3) 0.11s  锁舌滑开：带通噪声 1.4kHz→420Hz 下行扫频，配一记 140Hz 的低频"咔"
    4) 0.24s  上行铃音 G5（784Hz）
       0.42s  再上一记 C6（1046Hz），指数衰减 —— 明确的正反馈"开了"

写法和 whoosh / bump 一致：44.1k / 16bit / 立体声，峰值 -1.5dBFS，
左右声道用不同噪声种子，铃音略偏左，听上去不会像一条单声道直线。

事件动作「播放音效」里选「开锁」时，游戏播放的就是这个文件；
换素材只要把同名文件丢进 src/WindowsLoop/assets/audio/ 并重新 build。
"""

import math
import os
import random

import gen_window_sfx as g

SR = g.SR
OUT_DIR = g.OUT_DIR


# ── 小工具：往一条总线上叠一层 ──────────────────────────────────────────────

def add_bell(buf, at, freq, gain, decay=0.16):
    """一记软起音的铃：基频 + 两个泛音，指数衰减。"""
    i0 = int(at * SR)
    for i in range(i0, len(buf)):
        t = (i - i0) / SR
        if t > 0.9:
            break
        v = (math.sin(2.0 * math.pi * freq * t)
             + 0.45 * math.sin(2.0 * math.pi * freq * 2.02 * t)
             + 0.18 * math.sin(2.0 * math.pi * freq * 3.01 * t))
        v *= math.exp(-t / decay) * min(1.0, t / 0.004)
        buf[i] += v * gain


def add_tick(buf, at, freq, q, decay, gain, seed):
    """金属"嗒"：窄带噪声 + 极短衰减。"""
    rnd = random.Random(seed)
    band = g.biquad_bandpass([rnd.uniform(-1.0, 1.0) for _ in range(int(0.12 * SR))],
                             SR, freq, q)
    i0 = int(at * SR)
    for k, v in enumerate(band):
        if i0 + k >= len(buf):
            break
        buf[i0 + k] += v * math.exp(-(k / SR) / decay) * gain


def add_slide(buf, at, dur, f0, f1, q, gain, seed):
    """机构滑动：带通噪声从 f0 扫到 f1，5ms 起音 + 缓慢衰减。"""
    rnd = random.Random(seed)
    noise = [rnd.uniform(-1.0, 1.0) for _ in range(int(dur * SR))]
    i0 = int(at * SR)
    for k, v in enumerate(g.sweep_bandpass(noise, SR, f0, f1, q)):
        if i0 + k >= len(buf):
            break
        t = k / SR
        env = min(1.0, t / 0.005) * math.exp(-t / (dur * 0.45))
        buf[i0 + k] += v * env * gain


def add_thump(buf, at, freq, decay, gain):
    """锁舌顶到底的一记低频闷响。"""
    i0 = int(at * SR)
    for i in range(i0, len(buf)):
        t = (i - i0) / SR
        if t > 0.4:
            break
        buf[i] += math.sin(2.0 * math.pi * freq * t) * math.exp(-t / decay) * gain


# ── 主合成 ─────────────────────────────────────────────────────────────────

def make_unlock(duration=1.0, seed=23):
    n = int(duration * SR)

    def one(seed_offset):
        buf = [0.0] * n

        # 1) 钥匙插入 / 弹子落位：两声错开的金属"嗒"
        add_tick(buf, 0.000, 3100.0, 1.4, 0.006, 0.85, seed + seed_offset + 1)
        add_tick(buf, 0.048, 1900.0, 1.2, 0.005, 0.62, seed + seed_offset + 2)

        # 2) 锁舌滑开 + 低频"咔"
        add_slide(buf, 0.105, 0.13, 1400.0, 420.0, 0.9, 0.70, seed + seed_offset + 3)
        add_thump(buf, 0.112, 140.0, 0.05, 0.42)

        # 3) 两记上行铃音：G5 → C6
        add_bell(buf, 0.24, 783.99, 0.80, 0.15)
        add_bell(buf, 0.42, 1046.50, 0.62, 0.19)

        return g.fade_edges(buf, SR, 0.002, 0.03)

    left = g.normalize(one(0), -1.5)
    right = g.normalize(one(10), -1.5)
    return left, right


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    left, right = make_unlock()
    g.write_wav_stereo(os.path.join(OUT_DIR, "unlock.wav"), left, right)
    g.report("unlock", left)


if __name__ == "__main__":
    main()
