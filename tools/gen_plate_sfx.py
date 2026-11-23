"""
生成压力板「踩下去 / 松开」的音效（纯标准库）：

    python tools/gen_plate_sfx.py

输出：
    src/WindowsLoop/assets/audio/plate-down.wav   踩下去
    src/WindowsLoop/assets/audio/plate-up.wav     松开

听感对齐 Minecraft 石质压力板：一记极短的干"咔"，没有金属尾音、没有铃声。
脚下那块板贴图是「洞壁·碎石底」，所以主能量压在中高频（碎石摩擦的"脆"），
再补一点 80Hz 的闷响当作"压到底"的实心感。

按下 / 松开怎么区分：
    按下 —— 主带 2.2kHz，更沉、更短促，带一记 82Hz 的身体感；
    松开 —— 主带 2.9kHz，音量低一档、衰减更快，只留很淡的 64Hz，
             听感上像"弹起来"，和按下能明确分开。
两段都压在 0.1 秒内 —— 箱子和玩家在板子上抖动时不能糊成一片。

换素材：把同名 .wav 丢进 src/WindowsLoop/assets/audio/ 并重新 build 即可，
AudioManager 里按「基础名 + 扩展名优先级」查找，文件名别改。
"""

import math
import os
import random

import gen_window_sfx as g

SR = g.SR
OUT_DIR = g.OUT_DIR


# ── 两块积木：脆响 + 闷响 ───────────────────────────────────────────────────

def add_click(buf, at, freq, q, decay, gain, seed, tail=0.06):
    """
    石质脆响：带通噪声 + 1.5ms 起音 + 指数衰减。

    起音必须够快，慢了就变成"呼"而不是"咔"；
    decay 给 5~10ms，尾音一长就会沾上金属味，不像石头。
    """
    rnd = random.Random(seed)
    noise = [rnd.uniform(-1.0, 1.0) for _ in range(int(tail * SR))]
    band = g.biquad_bandpass(noise, SR, freq, q)

    i0 = int(at * SR)
    for k, v in enumerate(band):
        if i0 + k >= len(buf):
            break
        t = k / SR
        attack = min(1.0, t / 0.0015)
        buf[i0 + k] += v * attack * math.exp(-t / decay) * gain


def add_body(buf, at, freq, decay, gain):
    """压到底的那一点低频体积感。频率低于 100Hz 才像"实心的东西被压住"。"""
    i0 = int(at * SR)
    for i in range(i0, len(buf)):
        t = (i - i0) / SR
        if t > 0.25:
            break
        attack = min(1.0, t / 0.002)
        buf[i] += math.sin(2.0 * math.pi * freq * t) * attack * math.exp(-t / decay) * gain


# ── 两个音效 ────────────────────────────────────────────────────────────────

def make_down(seed=31, duration=0.10):
    """踩下去：三簇脆响叠在一起 + 一记低频，重心偏沉。"""
    n = int(duration * SR)

    def one(off):
        buf = [0.0] * n
        # 主"咔"：碎石互相挤了一下
        add_click(buf, 0.000, 2200.0, 1.5, 0.0070, 1.00, seed + off + 1)
        # 第二簇：稍低、稍晚 4ms，两声错开才有"碎了"的颗粒感，而不是一个纯音
        add_click(buf, 0.004, 1150.0, 1.2, 0.0100, 0.55, seed + off + 2)
        # 高频脆皮：很轻很短，只负责"锐"，音量给大了会变成电音咔哒
        add_click(buf, 0.000, 4300.0, 2.0, 0.0025, 0.28, seed + off + 3)
        # 压到底的闷响
        add_body(buf, 0.000, 82.0, 0.012, 0.55)
        return g.fade_edges(buf, SR, 0.0008, 0.012)

    left = g.normalize(one(0), -2.0)
    right = g.normalize(one(10), -2.0)
    return left, right


def make_up(seed=41, duration=0.085):
    """松开：更轻、偏高一点，低频几乎撤掉 —— 弹起来的感觉。"""
    n = int(duration * SR)

    def one(off):
        buf = [0.0] * n
        add_click(buf, 0.000, 2900.0, 1.7, 0.0055, 0.72, seed + off + 1)
        add_click(buf, 0.003, 1600.0, 1.3, 0.0070, 0.38, seed + off + 2)
        add_body(buf, 0.000, 64.0, 0.008, 0.26)
        return g.fade_edges(buf, SR, 0.0008, 0.010)

    left = g.normalize(one(0), -3.0)
    right = g.normalize(one(10), -3.0)
    return left, right


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    down_l, down_r = make_down()
    g.write_wav_stereo(os.path.join(OUT_DIR, "plate-down.wav"), down_l, down_r)
    g.report("plate-down", down_l)

    up_l, up_r = make_up()
    g.write_wav_stereo(os.path.join(OUT_DIR, "plate-up.wav"), up_l, up_r)
    g.report("plate-up", up_l)


if __name__ == "__main__":
    main()
