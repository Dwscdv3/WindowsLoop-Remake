using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WindowsLoop;

/// <summary>一次性音效事件：原版四类 + 窗口拼接相关事件。</summary>
internal enum Sfx
{
    Jump, Land, Gem,
    WindowOpen, Snap, Release, Transfer,
    Win,
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

    readonly string _dir;
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
        [Sfx.WindowOpen] = (AltName, 0.30f, 0.30f, false, 0f),
        [Sfx.Snap] = (JumpName, 0.38f, 0.16f, true, 0.05f),
        [Sfx.Release] = (LandName, 0.30f, 0.16f, true, 0.05f),
        [Sfx.Transfer] = (AltName, 0.42f, 0.28f, false, 0f),
        [Sfx.Win] = (GemName, 1.00f, 0f, false, 0f),
    };

    WasapiPlayer? _output;
    MixingSampleProvider? _mixer;
    Voice? _bgm, _walk;
    float _master = 0.85f;
    bool _muted, _disposed;
    float _walkLevel, _walkTarget;

    public bool Muted => _muted;
    public string AudioDir => _dir;

    /// <summary>BGM 放到这个路径就会自动接上（也认原名 windows-loop-bgm - final-fixed.*）。</summary>
    public string BgmExpectedPath => Path.Combine(_dir, "bgm.m4a");

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

    public AudioManager() => _dir = Game.AssetPath("audio");

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
    }

    Voice? TryOpen(string? path, bool loop, float volume)
    {
        if (path == null || _mixer == null) return null;
        try
        {
            var v = new Voice(path, loop, Effective(volume), MixFormat);
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

        string? path = Resolve(cfg.Name);
        if (path == null) return;
        _lastPlay[sfx] = now;
        _playCount[sfx] = PlayedCount(sfx) + 1;

        // 同一个事件不叠音：上一声还在响就先停掉
        if (cfg.Exclusive)
            for (int i = _oneShots.Count - 1; i >= 0; i--)
                if (_oneShots[i].Tag == sfx) Drop(_oneShots[i]);

        try
        {
            var v = new Voice(path, false, Effective(cfg.Volume), MixFormat)
            {
                MaxSeconds = cfg.MaxSeconds,
                Tag = sfx,
            };
            _mixer.AddMixerInput(v.Volume);
            _oneShots.Add(v);
        }
        catch { }
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
    public string? Find(string name) => Resolve(name);

    string? Resolve(string name)
    {
        foreach (string ext in Exts)
        {
            string p = Path.Combine(_dir, name + ext);
            if (File.Exists(p)) return p;
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

        public Voice(string path, bool loop, float volume, WaveFormat target)
        {
            BaseVolume = volume;
            _loop = loop;
            _reader = new MediaFoundationReader(path);

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
        };
        foreach (string name in names)
        {
            string? path = am.Find(name);
            if (path == null) { lines.Add($"{name}: (缺失)"); continue; }
            try
            {
                using var reader = new MediaFoundationReader(path);
                var channel = new SampleChannel(reader, false);
                var buffer = new float[4096];
                int read = channel.Read(buffer.AsSpan());
                reader.Position = 0;   // 循环播放会用到重定位
                lines.Add($"{Path.GetFileName(path)}: OK format={reader.WaveFormat} read={read} reposition=OK");
            }
            catch (Exception ex)
            {
                lines.Add($"{Path.GetFileName(path)}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
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
