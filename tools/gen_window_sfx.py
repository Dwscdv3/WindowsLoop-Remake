"""
生成 / 转换窗口动作的音效（纯标准库 + 可选 ffmpeg）：

    assets/audio/whoosh.wav   吐出去的那一下 —— 带通噪声从低扫到高，像东西从缝里窜出去
    assets/audio/bump.wav     撞到屏幕边 —— 清脆的"嗒"，高频瞬态 + 快速衰减
    assets/audio/sneeze.wav   窗口数到顶的"憋喷嚏" —— 目前是从 CC0 素材截的吸气段

背景：原来 Sfx.WindowOpen 指向 walk-loop.wav（脚步循环），它前 0.3 秒峰值只有
-26 dBFS，乘上 0.30 音量再截断到 0.3 秒，实际等于没声音。这里按动作重新做。

── 换成自己的喷嚏素材 ──────────────────────────────────────────────────
最省事的路子：**直接把文件丢进 src/WindowsLoop/assets/audio/ 并命名为 sneeze**，
扩展名不限（.wav / .mp3 / .m4a / .aac 都认，播放走 Windows Media Foundation 解码），
然后跑 build.cmd。注意查找顺序是 m4a > aac > mp3 > wav，同名的旧 wav 记得删掉。

需要裁切 / 转格式 / 调电平时再用脚本：

    python tools/gen_window_sfx.py --sneeze 我的喷嚏.mp3                      # 整段转成 44.1k 立体声
    python tools/gen_window_sfx.py --sneeze 我的喷嚏.mp3 --from 0.3 --to 1.1  # 只取 0.3~1.1 秒
    python tools/gen_window_sfx.py --sneeze 我的喷嚏.wav --gain -1.5          # 换个峰值

跑完会把时长打出来，照着把 Game.cs 里的 SneezeLead 设成这个时长
（它的含义是"让声音在顶点前多久起播"，等于素材长度时，声音的最后一下正好落在顶点上）。

用法：
    python tools/gen_window_sfx.py                       # 只生成 whoosh + bump
    python tools/gen_window_sfx.py --sneeze 源文件 [--from A] [--to B] [--gain DB]
输出：覆盖 src/WindowsLoop/assets/audio/ 下的同名文件（build.cmd 会自动拷到输出目录）
"""

import argparse
import array
import math
import os
import random
import shutil
import subprocess
import tempfile
import wave

SR = 44100
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "src", "WindowsLoop", "assets", "audio")


# ── 基础工具 ────────────────────────────────────────────────────────────────

def read_wav(path):
    """读成 (左声道 float 列表, 右声道 float 列表, 采样率)，统一归一到 ±1。"""
    with wave.open(path) as w:
        ch, sw, sr, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(n)

    if sw == 1:
        vals = [(b - 128) / 128.0 for b in raw]
    elif sw == 2:
        a = array.array("h"); a.frombytes(raw); vals = [v / 32768.0 for v in a]
    elif sw == 4:
        a = array.array("i"); a.frombytes(raw); vals = [v / 2147483648.0 for v in a]
    else:
        raise ValueError(f"不支持的位深: {sw * 8} bit")

    left = vals[0::ch]
    right = vals[1::ch] if ch > 1 else left
    return left, right, sr


def resample(samples, src_sr, dst_sr=SR):
    """线性插值重采样（只用来对齐 48k/44.1k，音质损失可忽略）。"""
    if src_sr == dst_sr or not samples:
        return samples
    n_out = int(len(samples) * dst_sr / src_sr)
    ratio = src_sr / dst_sr
    out = []
    for i in range(n_out):
        x = i * ratio
        i0 = int(x)
        i1 = min(i0 + 1, len(samples) - 1)
        frac = x - i0
        out.append(samples[i0] * (1.0 - frac) + samples[i1] * frac)
    return out


def biquad_bandpass(samples, sr, f0, q):
    """RBJ cookbook 的带通（峰值增益 = 1）。"""
    w0 = 2.0 * math.pi * f0 / sr
    alpha = math.sin(w0) / (2.0 * q)
    b0, b1, b2 = alpha, 0.0, -alpha
    a0, a1, a2 = 1.0 + alpha, -2.0 * math.cos(w0), 1.0 - alpha
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0

    x1 = x2 = y1 = y2 = 0.0
    out = []
    for x in samples:
        y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, x
        y2, y1 = y1, y
        out.append(y)
    return out


def sweep_bandpass(samples, sr, f_start, f_end, q, block=32):
    """中心频率一路扫过去的带通：每 block 个样点重算一次系数。"""
    out = []
    n = len(samples)
    for start in range(0, n, block):
        chunk = samples[start:start + block]
        t = start / max(1, n - 1)
        f = f_start * (f_end / f_start) ** t          # 指数扫频，听感上更匀
        out.extend(biquad_bandpass(chunk, sr, f, q))
    return out


def normalize(samples, peak_db):
    peak = max(1e-9, max(abs(v) for v in samples))
    gain = (10.0 ** (peak_db / 20.0)) / peak
    return [v * gain for v in samples]


def fade_edges(samples, sr, fade_in=0.002, fade_out=0.02):
    n = len(samples)
    fi, fo = int(sr * fade_in), int(sr * fade_out)
    for i in range(min(fi, n)):
        samples[i] *= i / fi
    for i in range(min(fo, n)):
        samples[n - 1 - i] *= i / fo
    return samples


def write_wav_stereo(path, left, right, sr=SR):
    """16 位立体声，和 assets/audio 里其它素材的格式一致。"""
    n = min(len(left), len(right))
    data = array.array("h")
    for i in range(n):
        for v in (left[i], right[i]):
            data.append(max(-32768, min(32767, int(round(v * 32767)))))
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(data.tobytes())


def report(name, mono):
    peak = 20.0 * math.log10(max(1e-9, max(abs(v) for v in mono)))
    rms = math.sqrt(sum(v * v for v in mono) / len(mono))
    print(f"  {name}.wav  {len(mono) / SR:.2f}s  peak={peak:6.1f}dBFS  rms={rms:.3f}")


# ── 三个音效 ────────────────────────────────────────────────────────────────

def make_whoosh(duration=0.42, seed=7):
    """
    吐出去：噪声过带通、中心从 320Hz 扫到 2600Hz，配一条快速衰减的包络。
    左右声道用不同种子 → 有一点宽度，听起来是"一团空气"而不是一条线。
    """
    n = int(SR * duration)

    def one(seed_offset):
        rnd = random.Random(seed + seed_offset)
        noise = [rnd.uniform(-1.0, 1.0) for _ in range(n)]
        swept = sweep_bandpass(noise, SR, 320.0, 2600.0, q=1.1)
        out = []
        for i, v in enumerate(swept):
            t = i / SR
            # 8ms 起音 + 指数衰减；尾巴拉长一点，别像被切断
            attack = min(1.0, t / 0.008)
            env = attack * math.exp(-t / 0.115)
            # 起点补一点低频"噗"，让声音有体积
            body = 0.35 * math.sin(2.0 * math.pi * 95.0 * t) * math.exp(-t / 0.035)
            out.append(v * env * 1.2 + body * env)
        return out

    left = normalize(one(0), -3.5)
    right = normalize(one(1), -3.5)
    return fade_edges(left, SR), fade_edges(right, SR)


def make_bump(duration=0.32, seed=11):
    """
    撞到屏幕边：一块木板被撞到的"啪"。

    三层：
      1. 窄带噪声 800Hz（Q=2.0）、衰减 18ms —— 板材被击的那一下
      2. 点击 1800Hz、衰减 5ms、量 0.75 —— 边缘的锐利感
      3. 正弦 220Hz、衰减 55ms、量 0.45 —— 板子后面的空腔共鸣
    结尾过 tanh 软饱和：峰值压住、RMS 抬起来（同样峰值下响度 +4dB），
    顺带给撞击加一点"硬"的谐波。
    """
    n = int(SR * duration)

    # 注意：Random 必须在推导式外面建一次，写在里面会让每个采样点都取同一个首值（等于直流）
    rnd_band = random.Random(seed * 10)
    rnd_tick = random.Random(seed * 11)
    band = biquad_bandpass([rnd_band.uniform(-1.0, 1.0) for _ in range(n)], SR, 800.0, q=2.0)
    tick = biquad_bandpass([rnd_tick.uniform(-1.0, 1.0) for _ in range(n)], SR, 1800.0, q=1.0)

    out = []
    for i in range(n):
        t = i / SR
        v = band[i] * math.exp(-t / 0.018) * 1.00
        v += tick[i] * math.exp(-t / 0.005) * 0.75
        v += math.sin(2.0 * math.pi * 220.0 * t) * math.exp(-t / 0.055) * 0.45
        out.append(v)

    out = normalize(out, 0.0)
    drive = 2.4
    out = [math.tanh(v * drive) / math.tanh(drive) for v in out]
    out = normalize(out, -1.0)

    mono = fade_edges(out, SR, 0.0005, 0.03)
    return mono, list(mono)


def to_wav_path(path):
    """不是 wav 就用 ffmpeg 转一份 44.1k / 16bit / 立体声的临时 wav 出来。"""
    if path.lower().endswith(".wav"):
        return path, False

    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise SystemExit(f"输入不是 wav，且没找到 ffmpeg，无法解码：{path}\n"
                         f"要么先自己转成 wav，要么把 ffmpeg 放进 PATH。")

    tmp = os.path.join(tempfile.gettempdir(), "wl_sneeze_in.wav")
    cmd = [ffmpeg, "-y", "-v", "error", "-i", path,
           "-ar", str(SR), "-ac", "2", "-c:a", "pcm_s16le", tmp]
    subprocess.run(cmd, check=True)
    return tmp, True


def make_sneeze_from(src_path, t0=None, t1=None, gain_db=-3.5):
    """
    把任意喷嚏素材转成游戏能用的 sneeze.wav：44.1k / 16bit / 立体声。
    t0 / t1 给了就裁切（秒），不给就整段。两端各加淡入淡出，避免爆音。
    """
    wav_path, temp = to_wav_path(src_path)
    try:
        left, right, sr = read_wav(wav_path)
    finally:
        if temp:
            try: os.remove(wav_path)
            except OSError: pass

    left = resample(left, sr)
    right = resample(right, sr)

    total = len(left) / SR
    i0 = 0 if t0 is None else max(0, int(t0 * SR))
    i1 = len(left) if t1 is None else min(len(left), int(t1 * SR))
    if i1 <= i0:
        raise SystemExit(f"裁切区间为空：from={t0} to={t1}，素材总长 {total:.2f}s")

    seg_l = left[i0:i1]
    seg_r = right[i0:i1]

    # 淡入淡出在**归一之前**做，末端的淡出就不会连累整体电平
    fade_out = 0.03 if t1 is None else 0.015
    seg_l = fade_edges(seg_l, SR, 0.005, fade_out)
    seg_r = fade_edges(seg_r, SR, 0.005, fade_out)

    peak = max(max(abs(v) for v in seg_l), 1e-9)
    gain = (10.0 ** (gain_db / 20.0)) / peak
    return [v * gain for v in seg_l], [v * gain for v in seg_r]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sneeze", metavar="源文件", help="喷嚏素材；给了才会生成 sneeze.wav")
    ap.add_argument("--from", dest="t0", type=float, metavar="秒", help="裁切起点（默认整段）")
    ap.add_argument("--to", dest="t1", type=float, metavar="秒", help="裁切终点（默认整段）")
    ap.add_argument("--gain", dest="gain_db", type=float, default=-3.5, metavar="dBFS",
                    help="峰值电平，默认 -3.5")
    ap.add_argument("--out", metavar="名字",
                    help="输出名（不带扩展名），默认 sneeze；切成两段时用它分开存")
    args = ap.parse_args()

    os.makedirs(OUT_DIR, exist_ok=True)

    jobs = {"whoosh": make_whoosh(), "bump": make_bump()}
    if args.sneeze:
        jobs[args.out or "sneeze"] = make_sneeze_from(args.sneeze, args.t0, args.t1, args.gain_db)

    for name, (l, r) in jobs.items():
        write_wav_stereo(os.path.join(OUT_DIR, name + ".wav"), l, r)
        report(name, l)

    if args.sneeze:
        name = args.out or "sneeze"
        print(f"\n  记一下：把 Game.cs 里的 SneezeLead 设成 {len(jobs[name][0]) / SR:.2f}f，"
              f"声音的最后一下就会落在僵住那一拍上。")


if __name__ == "__main__":
    main()

