using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WindowsLoop;

/// <summary>一次性音效事件：原版四类 + 窗口拼接 / 新生窗口相关事件。</summary>
internal enum Sfx
{
    Jump, Land, Gem,
    WindowOpen, Snap, Release, Transfer,
    Win,
    /// <summary>新窗口撞到屏幕边的那一声。</summary>
    Bump,
    /// <summary>吐出窗口前的蓄力"啊——"。</summary>
    Ahhh,
    /// <summary>窗口飞出去的那一记"啾"。</summary>
    Choo,
    /// <summary>窗口数到顶、开不出新窗口时的连续"啊"。</summary>
    CantOpen,
    /// <summary>关卡事件动作「播放音效」里可选的"开锁"（事件按 key 触发）。</summary>
    Unlock,
}

/// <summary>
/// 单实例音频管理：BGM 循环、脚步循环（0.1s 淡入淡出）、一次性音效叠加。
/// 素材按「基础名 + 任意扩展名」查找，所以之后把 wav 换成 aac / m4a / mp3 不用改代码。
/// 解码走 Windows 自带的 Media Foundation，不额外带 native 解码器。
///
/// 关键设计：整局只有一个 WASAPI 输出，所有声音都是同一个混音器的输入。
/// 早期版本给每个音效各开一个播放器，一次性音效结束销毁播放器时会干扰共享音频会话，
/// 表现就是「音效一停，BGM 也跟着停」；改成单一输出后这个耦合彻底消失。
/// </summary>
internal sealed class AudioManager : IDisposable
{
    // 优先找高效编码，找不到再退回 wav
    static readonly string[] Exts = { ".m4a", ".aac", ".mp3", ".wav", ".wma", ".flac" };

    const string BgmName = "windows-loop-bgm - final-fixed";
    const string WalkName = "walk-gravel-loop-200bpm";
    const string JumpName = "Land 1";
    const string LandName = "Land 4";
    const string GemName = "Game Item 32";
    const string AltName = "walk-loop";

    /// <summary>
    /// 窗口动作的音效。whoosh / bump 是 tools/gen_window_sfx.py 合成的；
    /// 三段喷嚏是从 Tom 猫视频里人工切出来的（见 tools/sfx-cutter.html），
    /// 哪段放哪个动作由文件名决定，换素材只要替换同名文件。
    /// </summary>
    const string WhooshName = "whoosh";
    const string BumpName = "bump";
    const string AhhhName = "sneeze-ahhh";                 // 吐出前的蓄力
    const string ChooName = "sneeze-choo";                 // 窗口飞出去
    const string CantOpenName = "sneeze-ah-ah-ahah-ahh-ahah"; // 到上限时的连续"啊"
    /// <summary>开锁：tools/gen_unlock_sfx.py 合成的金属"嗒"+ 锁舌 + 上行铃音。</summary>
    const string UnlockName = "unlock";

    const float BgmVolume = 0.34f;

    /// <summary>
    /// 走路循环的增益。素材本身比其它音效安静得多（RMS 约 -43.6 dBFS，
    /// 而落地音是 -29.9 dBFS，差 13 dB 以上），按普通 0.4 的音量在 BGM 底下等于听不见，
    /// 所以这里给到 1.5 倍增益，实际听感约比落地轻 6 dB。
    /// </summary>
    const float WalkVolume = 1.5f;

    const float FadeSeconds = 0.1f;

    /// <summary>混音格式：44.1kHz 立体声浮点，所有素材都对齐到这个格式再混。</summary>
    static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    readonly List<Voice> _oneShots = new();
    readonly Dictionary<Sfx, double> _lastPlay = new();
    readonly Dictionary<Sfx, int> _playCount = new();

    /// <summary>每个事件的播放配置；MinInterval &gt; 0 时，这段时间内的重复触发直接忽略。</summary>
    readonly Dictionary<Sfx, (string Name, float Volume, float MaxSeconds, bool Exclusive, float MinInterval)> _map = new()
    {
        [Sfx.Jump] = (JumpName, 0.75f, 0f, false, 0f),
        // 落地：判定在贴地边缘偶尔会连触，0.3s 内的重复直接不播（只留第一声）
        [Sfx.Land] = (LandName, 0.62f, 0f, true, 0.3f),
        [Sfx.Gem] = (GemName, 0.90f, 0f, false, 0f),
        [Sfx.WindowOpen] = (WhooshName, 0.50f, 0.45f, false, 0f),
        [Sfx.Snap] = (JumpName, 0.38f, 0.16f, true, 0.05f),
        [Sfx.Release] = (LandName, 0.30f, 0.16f, true, 0.05f),
        [Sfx.Transfer] = (AltName, 0.42f, 0.28f, false, 0f),
        [Sfx.Win] = (GemName, 1.00f, 0f, false, 0f),
        // 撞屏幕边：一声就够，重复触发直接忽略。
        // 音量 0.84 —— 对齐试听时的响度：压在喷嚏尾音上也听得清
        [Sfx.Bump] = (BumpName, 0.84f, 0.35f, true, 0.05f),
        // 蓄力 / 飞出 / 开不出新窗口：素材本身没有爆音，别截断。
        // 三段喷嚏素材的峰值和 RMS 都比合成音效高，整体比原调压低 15%（约 -1.4dB），
        // 落在 BGM 上面不会盖住走路音
        [Sfx.Ahhh] = (AhhhName, 0.72f, 0f, false, 0f),
        [Sfx.Choo] = (ChooName, 0.80f, 0f, false, 0f),
        [Sfx.CantOpen] = (CantOpenName, 0.68f, 0f, false, 0f),
        // 开锁：完整播放（铃音尾巴别截），0.06s 内重复触发算同一次，避免叠成糊响
        [Sfx.Unlock] = (UnlockName, 0.90f, 0f, false, 0.06f),
    };

    /// <summary>
    /// 事件动作「播放音效」的 key → 音效；key 表在 <c>Shared\SfxCatalog.cs</c>，
    /// 两边必须对得上 —— 编辑器下拉里出现、这里没注册的 key，游戏里按"没声音"处理。
    /// </summary>
    static readonly Dictionary<string, Sfx> SfxByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unlock"] = Sfx.Unlock,
        ["jump"] = Sfx.Jump,
        ["land"] = Sfx.Land,
        ["gem"] = Sfx.Gem,
        ["win"] = Sfx.Win,
        ["window"] = Sfx.WindowOpen,
        ["snap"] = Sfx.Snap,
        ["release"] = Sfx.Release,
        ["transfer"] = Sfx.Transfer,
        ["bump"] = Sfx.Bump,
        ["ahhh"] = Sfx.Ahhh,
        ["choo"] = Sfx.Choo,
        ["cantopen"] = Sfx.CantOpen,
    };

    WasapiPlayer? _output;
    MixingSampleProvider? _mixer;
    Voice? _bgm, _walk;
    float _master = 0.85f;
    bool _muted, _disposed;
    float _walkLevel, _walkTarget;

    /// <summary>
    /// 测试用：把某个音效按**游戏里实际的音量**完整读一遍，回报峰值/RMS。
    /// 用来排查"代码调了但没有声音"——如果这里读出来是静音，问题在素材或解析；
    /// 如果读出来电平正常，那就只剩播放时机和混音环境的问题。
    /// </summary>
    public (float PeakDb, float Rms, float Seconds, string Note) ProbeSfx(Sfx sfx)
    {
        if (!_map.TryGetValue(sfx, out var cfg)) return (0f, 0f, 0f, "没在 _map 里注册");
        var clip = Resolve(cfg.Name);
        if (clip == null) return (0f, 0f, 0f, $"找不到素材 {cfg.Name}");

        Voice voice;
        try { voice = new Voice(clip, false, Math.Max(0.0001f, cfg.Volume), MixFormat); }
        catch (Exception ex) { return (0f, 0f, 0f, "解码失败 " + ex.GetType().Name); }

        try
        {
            var buf = new float[4096];
            double sum = 0;
            float peak = 0f;
            long count = 0;
            int read;
            while ((read = voice.Volume.Read(buf.AsSpan())) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    float a = MathF.Abs(buf[i]);
                    if (a > peak) peak = a;
                    sum += buf[i] * (double)buf[i];
                }
                count += read;
            }

            int channels = Math.Max(1, voice.Volume.WaveFormat.Channels);
            float seconds = count / (float)(MixFormat.SampleRate * channels);
            float peakDb = peak > 0 ? 20f * MathF.Log10(peak) : -999f;
            float rms = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
            return (peakDb, rms, seconds, clip.Name);
        }
        finally { voice.Dispose(); }
    }

    /// <summary>
    /// 某个音效素材的时长（秒）；找不到或读不出来返回 0。
    /// 用来把音效对齐到动画节拍（换素材之后不用改代码）。
    /// </summary>
    public float SfxLength(Sfx sfx)
    {
        if (_lengthCache.TryGetValue(sfx, out float cached)) return cached;

        float len = 0f;
        if (_map.TryGetValue(sfx, out var cfg))
        {
            var clip = Resolve(cfg.Name);
            if (clip != null)
            {
                try
                {
                    using var reader = clip.Open();
                    len = (float)(reader.Length / (double)Math.Max(1, reader.WaveFormat.AverageBytesPerSecond));
                }
                catch { }
            }
        }

        _lengthCache[sfx] = len;
        return len;
    }

    readonly Dictionary<Sfx, float> _lengthCache = new();
    readonly Dictionary<Sfx, float[]> _envCache = new();

    /// <summary>
    /// 音效的音量包络（0..1，每秒 <paramref name="perSecond"/> 格）。
    /// 窗口抖动的强弱直接查这张表，于是素材里有几下"啊"、抖就几下 ——
    /// 换素材不用改一行代码。启动时在 <see cref="Start"/> 里预热，动画期间零开销。
    /// </summary>
    public float[] SfxEnvelope(Sfx sfx, int perSecond = 100)
    {
        if (_envCache.TryGetValue(sfx, out var cached)) return cached;

        var result = Array.Empty<float>();
        if (_map.TryGetValue(sfx, out var cfg))
        {
            var clip = Resolve(cfg.Name);
            if (clip != null)
            {
                try
                {
                    using var reader = clip.Open();
                    ISampleProvider src = new SampleChannel(reader, false);
                    if (src.WaveFormat.Channels == 1) src = new MonoToStereoSampleProvider(src);

                    int sr = src.WaveFormat.SampleRate;
                    int cell = Math.Max(1, sr / perSecond) * Math.Max(1, src.WaveFormat.Channels);

                    var values = new List<float>(512);
                    var buf = new float[4096];
                    double sum = 0; int n = 0;
                    int read;
                    while ((read = src.Read(buf.AsSpan())) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            sum += buf[i] * (double)buf[i];
                            if (++n < cell) continue;
                            values.Add((float)Math.Sqrt(sum / n));
                            sum = 0; n = 0;
                        }
                    }

                    if (values.Count > 0)
                    {
                        float max = 0.0001f;
                        foreach (var v in values) if (v > max) max = v;

                        // 过门槛才算数：素材里"不够响"的段落直接当 0，窗口就别抖了。
                        // 不过门槛的话，气声的小起伏也会被放大成肉眼可见的位移。
                        const float Threshold = 0.40f;

                        var env = new float[values.Count];
                        for (int i = 0; i < env.Length; i++)
                        {
                            float above = (values[i] / max - Threshold) / (1f - Threshold);
                            env[i] = above > 0f ? above : 0f;
                        }

                        // 两遍三点平滑，幅度不会一格一跳（也会把门槛边缘磨软成 30ms 的斜坡）
                        for (int pass = 0; pass < 2; pass++)
                        {
                            float prev = env[0];
                            for (int i = 1; i < env.Length; i++)
                            {
                                float cur = env[i];
                                env[i] = (prev + cur * 2f) / 3f;
                                prev = cur;
                            }
                        }
                        result = env;
                    }
                }
                catch { }
            }
        }

        _envCache[sfx] = result;
        return result;
    }

    public bool Muted => _muted;

    /// <summary>诊断用：素材来源。单文件分发后音频全部来自程序集内嵌资源。</summary>
    public string AudioDir => "(embedded) asset/audio/";

    /// <summary>诊断用：BGM 当前播放位置（毫秒），-1 表示没有 BGM。</summary>
    public double BgmPositionMs => _bgm?.PositionMs ?? -1;

    /// <summary>诊断用：走路循环是否建立成功。</summary>
    public bool HasWalk => _walk != null;

    /// <summary>诊断用：走路循环当前播放位置（毫秒）。</summary>
    public double WalkPositionMs => _walk?.PositionMs ?? -1;

    /// <summary>诊断用：走路循环当前实际施加的增益。</summary>
    public float WalkGain => _walk?.Volume.Volume ?? -1f;

    /// <summary>诊断用：某个事件实际被接纳播放了多少次（被去抖挡下的不计数）。</summary>
    public int PlayedCount(Sfx sfx) => _playCount.TryGetValue(sfx, out int n) ? n : 0;

    public AudioManager() { }

    public void Start()
    {
        try
        {
            _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            _output = new WasapiPlayerBuilder().Build();
            _output.Init(_mixer.ToWaveProvider());
            _output.Play();
        }
        catch
        {
            // 没有音频设备时静默降级，不影响游戏
            _output?.Dispose();
            _output = null;
            _mixer = null;
            return;
        }

        _bgm = TryOpen(Resolve(BgmName) ?? Resolve("bgm"), loop: true, BgmVolume);
        _walk = TryOpen(Resolve(WalkName), loop: true, 0f);

        // 预热三个"动作音效"的包络：素材都只有零点几到两三秒，解码一遍是毫秒级，
        // 换到这里做，动画开始时就不用临时算
        SfxEnvelope(Sfx.Ahhh);
        SfxEnvelope(Sfx.Choo);
        SfxEnvelope(Sfx.CantOpen);
    }

    Voice? TryOpen(AudioClip? clip, bool loop, float volume)
    {
        if (clip == null || _mixer == null) return null;
        try
        {
            var v = new Voice(clip, loop, Effective(volume), MixFormat);
            _mixer.AddMixerInput(v.Volume);
            return v;
        }
        catch { return null; }
    }

    public void Update(float dt)
    {
        if (_disposed) return;

        // 脚步是常驻循环，靠音量淡入淡出模拟「开始走 / 停下」，和原版一致
        if (_walk != null)
        {
            float step = dt / FadeSeconds;
            float diff = _walkTarget - _walkLevel;
            _walkLevel = MathF.Abs(diff) <= step ? _walkTarget : _walkLevel + MathF.Sign(diff) * step;
            _walk.SetVolume(Effective(_walkLevel * WalkVolume));
        }

        for (int i = _oneShots.Count - 1; i >= 0; i--)
        {
            var v = _oneShots[i];
            v.Elapsed += dt;
            bool expired = v.MaxSeconds > 0f && v.Elapsed >= v.MaxSeconds;
            if (!expired && !v.ReachedEnd) continue;
            Drop(v);
        }
    }

    public void Play(Sfx sfx)
    {
        if (_disposed || _muted || _mixer == null) return;
        if (!_map.TryGetValue(sfx, out var cfg)) return;

        // 去抖：同一个事件在 MinInterval 内的重复触发直接忽略（落地音用 0.3s）
        double now = Environment.TickCount64 / 1000.0;
        if (cfg.MinInterval > 0f &&
            _lastPlay.TryGetValue(sfx, out double last) &&
            now - last < cfg.MinInterval)
            return;

        var clip = Resolve(cfg.Name);
        if (clip == null) return;
        _lastPlay[sfx] = now;
        _playCount[sfx] = PlayedCount(sfx) + 1;

        // 同一个事件不叠音：上一声还在响就先停掉
        if (cfg.Exclusive)
            for (int i = _oneShots.Count - 1; i >= 0; i--)
                if (_oneShots[i].Tag == sfx) Drop(_oneShots[i]);

        try
        {
            var v = new Voice(clip, false, Effective(cfg.Volume), MixFormat)
            {
                MaxSeconds = cfg.MaxSeconds,
                Tag = sfx,
            };
            _mixer.AddMixerInput(v.Volume);
            _oneShots.Add(v);
        }
        catch { }
    }

    /// <summary>自测用：这个 key 注册过没有（目录和注册表漂了就会在这里露出来）。</summary>
    public static bool IsKnownKey(string key) => SfxByKey.ContainsKey(key ?? "");

    /// <summary>
    /// 事件动作「播放音效」：按关卡 JSON 里的 key（见 Shared\SfxCatalog.cs）播放一次。
    /// 返回 false 表示这个 key 没注册 —— 游戏里静默跳过，不当错误处理。
    /// </summary>
    public bool PlayByKey(string key)
    {
        if (!SfxByKey.TryGetValue(key ?? "", out Sfx sfx)) return false;
        Play(sfx);
        return true;
    }

    /// <summary>把一条通道从混音器摘掉并释放；摘除动作只影响它自己。</summary>
    void Drop(Voice v)
    {
        try { _mixer?.RemoveMixerInput(v.Volume); } catch { }
        v.Dispose();
        _oneShots.Remove(v);
    }

    public void SetWalking(bool walking) => _walkTarget = walking ? 1f : 0f;

    public void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
    }

    public void SetMasterVolume(float v)
    {
        _master = Math.Clamp(v, 0f, 1f);
        ApplyVolume();
    }

    void ApplyVolume()
    {
        _bgm?.SetVolume(Effective(BgmVolume));
        _walk?.SetVolume(Effective(_walkLevel * WalkVolume));
        foreach (var v in _oneShots) v.SetVolume(Effective(v.BaseVolume));
    }

    float Effective(float v) => _muted ? 0f : v * _master;

    /// <summary>给解码自测用：按基础名找素材。</summary>
    public AudioClip? Find(string name) => Resolve(name);

    /// <summary>
    /// 按「基础名 + 扩展名优先级」在程序集内嵌资源里找。原来这里是 File.Exists 探测，
    /// 改成查 Assets 的表之后，单文件分发不再需要 exe 旁边有 assets\audio 目录。
    /// </summary>
    AudioClip? Resolve(string name)
    {
        foreach (string ext in Exts)
        {
            string rel = "audio/" + name + ext;
            byte[]? data = Assets.Read(rel);
            if (data != null) return new AudioClip(name + ext, data);
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var v in _oneShots.ToArray()) Drop(v);
        _oneShots.Clear();

        foreach (var v in new[] { _bgm, _walk })
        {
            if (v == null) continue;
            try { _mixer?.RemoveMixerInput(v.Volume); } catch { }
            v.Dispose();
        }
        _bgm = _walk = null;

        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _mixer = null;
    }

    /// <summary>
    /// 一条混音输入：解码 →（必要时重采样/声道对齐）→（可选循环）→ 音量。
    /// 它自己不持有任何播放器，所以增删一条通道不会动到别的声音。
    /// </summary>
    sealed class Voice : IDisposable
    {
        readonly MediaFoundationReader _reader;
        readonly bool _loop;

        public readonly VolumeSampleProvider Volume;
        public Sfx Tag;
        public float MaxSeconds;
        public float Elapsed;
        public float BaseVolume { get; }

        public Voice(AudioClip clip, bool loop, float volume, WaveFormat target)
        {
            BaseVolume = volume;
            _loop = loop;
            _reader = clip.Open();

            ISampleProvider src = new SampleChannel(_reader, false);
            if (src.WaveFormat.SampleRate != target.SampleRate)
                src = new WdlResamplingSampleProvider(src, target.SampleRate);
            if (src.WaveFormat.Channels == 1 && target.Channels == 2)
                src = new MonoToStereoSampleProvider(src);
            if (loop) src = new LoopSampleProvider(src, _reader);

            Volume = new VolumeSampleProvider(src) { Volume = Math.Max(0f, volume) };
        }

        public double PositionMs => _reader.Position * 1000.0 / Math.Max(1, _reader.WaveFormat.AverageBytesPerSecond);

        /// <summary>一次性音效是否已经读到底（循环通道永远为 false）。</summary>
        public bool ReachedEnd => !_loop && _reader.Position >= _reader.Length;

        public void SetVolume(float v) => Volume.Volume = Math.Clamp(v, 0f, 4f);   // 允许 >1：有的素材本身太轻

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>读到末尾就回到开头，用于 BGM 和脚步循环（NAudio 3.x 的 ISampleProvider 基于 Span）。</summary>
    sealed class LoopSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly MediaFoundationReader _reader;

        public WaveFormat WaveFormat => _src.WaveFormat;

        public LoopSampleProvider(ISampleProvider src, MediaFoundationReader reader)
        {
            _src = src;
            _reader = reader;
        }

        public int Read(Span<float> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = _src.Read(buffer[total..]);
                if (read == 0)
                {
                    try { _reader.Position = 0; } catch { return total; }
                    continue;
                }
                total += read;
            }
            return total;
        }
    }
}

/// <summary>不带界面地验证每个素材能否被 Media Foundation 解码：--audio-selftest。</summary>
internal static class AudioSelfTest
{
    public static void Run()
    {
        var am = new AudioManager();
        var lines = new List<string> { "audio dir: " + am.AudioDir };
        string[] names =
        {
            "windows-loop-bgm - final-fixed", "bgm",
            "walk-gravel-loop-200bpm", "walk-loop",
            "Land 1", "Land 4", "Game Item 32",
            "whoosh", "bump", "sneeze-ahhh", "sneeze-choo", "sneeze-ah-ah-ahah-ahh-ahah",
            "unlock",
        };
        foreach (string name in names)
        {
            var clip = am.Find(name);
            if (clip == null) { lines.Add($"{name}: (缺失)"); continue; }
            try
            {
                using var reader = clip.Open();
                var channel = new SampleChannel(reader, false);
                var buffer = new float[4096];
                int read = channel.Read(buffer.AsSpan());
                reader.Position = 0;   // 循环播放会用到重定位
                lines.Add($"{clip.Name}: OK format={reader.WaveFormat} read={read} reposition=OK");
            }
            catch (Exception ex)
            {
                lines.Add($"{clip.Name}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 按游戏里的实际音量把几个新音效完整读一遍：核对"代码调了但听不见"到底是素材、
        // 音量还是时机的问题
        foreach (var (sfx, label) in new[]
                 {
                     (Sfx.WindowOpen, "WindowOpen/吐出"),
                     (Sfx.Bump, "Bump/撞边"),
                     (Sfx.Ahhh, "Ahhh/蓄力"),
                     (Sfx.Choo, "Choo/飞出"),
                     (Sfx.CantOpen, "CantOpen/开不出"),
                     (Sfx.Unlock, "Unlock/开锁"),
                 })
        {
            var (peakDb, rms, sec, note) = am.ProbeSfx(sfx);
            lines.Add($"{label}: {note} 峰值={peakDb:F1}dBFS rms={rms:F3} 时长={sec:F2}s");
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "windowsloop-audio-selftest.txt"), lines);
    }

    /// <summary>
    /// 走路音回归测试：--audio-walk-test。确认走路循环通道建起来了、增益够、播放位置在推进。
    /// </summary>
    public static void RunWalkTest()
    {
        var lines = new List<string>();
        var am = new AudioManager();
        try
        {
            am.Start();
            lines.Add($"walk voice: {(am.HasWalk ? "created" : "MISSING")}");
            am.SetWalking(false);
            for (int i = 0; i < 20; i++) { am.Update(1f / 60f); Thread.Sleep(16); }
            lines.Add($"idle gain: {am.WalkGain:F3} (should be 0)");

            double p0 = am.WalkPositionMs;
            am.SetWalking(true);
            for (int i = 0; i < 60; i++) { am.Update(1f / 60f); Thread.Sleep(16); }
            double p1 = am.WalkPositionMs;
            lines.Add($"walking gain: {am.WalkGain:F3} (should be ~{1.5f * 0.85f:F3})");
            lines.Add($"walk position: {p0:F0} -> {p1:F0} ms (advancing: {(p1 != p0 ? "YES" : "NO")})");

            am.SetWalking(false);
            for (int i = 0; i < 30; i++) { am.Update(1f / 60f); Thread.Sleep(16); }
            lines.Add($"after stop gain: {am.WalkGain:F3} (should be 0)");
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { am.Dispose(); }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "windowsloop-audio-walk-test.txt"), lines);
    }

    /// <summary>
    /// 落地音去抖测试：--audio-land-test。0.3s 内的重复触发必须被忽略。
    /// </summary>
    public static void RunLandTest()
    {
        var lines = new List<string>();
        var am = new AudioManager();
        try
        {
            am.Start();
            Thread.Sleep(100);

            am.Play(Sfx.Land);
            int a = am.PlayedCount(Sfx.Land);
            am.Play(Sfx.Land);
            int b = am.PlayedCount(Sfx.Land);
            Thread.Sleep(120);
            am.Play(Sfx.Land);
            int c = am.PlayedCount(Sfx.Land);
            Thread.Sleep(320);
            am.Play(Sfx.Land);
            int d = am.PlayedCount(Sfx.Land);

            lines.Add($"首次: {a} 次（期望 1）");
            lines.Add($"立即重播: {b} 次（期望仍为 1，被忽略）");
            lines.Add($"120ms 后重播: {c} 次（期望仍为 1，0.3s 内）");
            lines.Add($"再等 320ms: {d} 次（期望 2）");
            lines.Add($"verdict: {(a == 1 && b == 1 && c == 1 && d == 2 ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { am.Dispose(); }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "windowsloop-audio-land-test.txt"), lines);
    }

    /// <summary>
    /// 混音回归测试：--audio-mix-test。开 BGM、隔一会儿放一个一次性音效，
    /// 等音效自然结束并释放后再看 BGM 位置是否还在前进（旧实现这里会掉到 0）。
    /// </summary>
    public static void RunMixTest()
    {
        var lines = new List<string>();
        var am = new AudioManager();
        try
        {
            am.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float dt = 1f / 60f;

            void Pump(int ms)
            {
                int frames = ms / 16;
                for (int i = 0; i < frames; i++)
                {
                    am.Update(dt);
                    Thread.Sleep(16);
                }
            }

            Pump(600);
            double before = am.BgmPositionMs;
            lines.Add($"bgm after start: {before:F0} ms");

            am.Play(Sfx.Gem);
            Pump(3200);   // 音效 1.9s，结束后再等一会儿
            double afterSfx = am.BgmPositionMs;
            lines.Add($"bgm after sfx ended: {afterSfx:F0} ms");

            am.Play(Sfx.Jump);
            Pump(1200);
            double after2 = am.BgmPositionMs;
            lines.Add($"bgm after 2nd sfx: {after2:F0} ms");

            bool advancing = afterSfx > before + 1000 && after2 > afterSfx + 500;
            lines.Add($"bgm still advancing: {(advancing ? "YES" : "NO")} (elapsed {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { am.Dispose(); }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "windowsloop-audio-mix-test.txt"), lines);
    }
}

/// <summary>
/// 一段音频素材。单文件分发后素材编在程序集里，所以这里拿的是字节而不是路径。
/// 解码仍旧交给 Windows 自带的 Media Foundation，只是换成走内存的
/// <see cref="StreamMediaFoundationReader"/>，不需要在磁盘上落任何临时文件。
/// </summary>
internal sealed class AudioClip
{
    readonly byte[] _data;

    /// <summary>文件名，只用于日志和自测输出。</summary>
    public string Name { get; }

    public AudioClip(string name, byte[] data)
    {
        Name = name;
        _data = data;
    }

    /// <summary>每次播放 / 分析都开一条新解码器：读端有游标，不能共用。</summary>
    public MediaFoundationReader Open() =>
        new StreamMediaFoundationReader(new MemoryStream(_data, writable: false));
}
