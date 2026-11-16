using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PtzStudio.Core;
using PtzStudio.Core.Models;
using PtzStudio.Core.Video;
using VlcCore = LibVLCSharp.Shared.Core;
using VlcFromType = LibVLCSharp.Shared.FromType;
using VlcLib = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcState = LibVLCSharp.Shared.VLCState;

namespace PtzStudio.App.Live;

/// <summary>
/// Vídeo ao vivo (RTSP) da câmera. Tudo que pode demorar (abrir, parar, reconectar) roda numa tarefa de fundo; a decodificação
/// roda nas threads do LibVLC. A thread da tela só copia um quadro por vez (prioridade menor que a dos comandos VISCA),
/// então o vídeo nunca segura os comandos de Pan/Tilt/Zoom. Sem ninguém olhando, o vídeo para sozinho.
/// </summary>
internal sealed class LiveVideo
{
    public static LiveVideo Instance { get; } = new();

    private sealed record Want(string Url, int MaxW, int MaxH, bool Hw = false, int CacheMs = 300);
    private static readonly Want Off = new("", 0, 0);
    private static readonly long ExpireTicks = TimeSpan.FromSeconds(2).Ticks;

    // ---- quadros (VLC escreve no buffer "de trás"; a tela lê o da "frente") ----
    private readonly object _sync = new();
    private readonly List<IntPtr> _allocs = new();
    private IntPtr _back, _front;
    private int _w, _h, _fw, _fh;
    private bool _ready;
    private int _maxW = 640, _maxH = 360;
    private long _lastFrameTicks;
    private WriteableBitmap? _bmp;
    private DispatcherTimer? _pump;

    // ---- player (só a tarefa de fundo mexe nestes) ----
    private VlcLib? _libvlc;
    private VlcPlayer? _player;
    private VlcMedia? _media;
    private bool _libReady;
    private string _libMsg = "";
    private Want _cur = Off;
    private DateTime _startedAt = DateTime.MinValue;
    private DateTime _retryAt = DateTime.MinValue;
    private int _fails;

    private volatile Want _want = Off;
    private volatile string _status = "Vídeo desligado.";
    private volatile bool _fullscreen;
    private long _touchTicks;
    private int _busy, _again;
    private readonly Timer _maintain;

    private readonly VlcPlayer.LibVLCVideoFormatCb _fmtCb;
    private readonly VlcPlayer.LibVLCVideoLockCb _lockCb;
    private readonly VlcPlayer.LibVLCVideoDisplayCb _displayCb;

    public event Action? FrameChanged;

    private LiveVideo()
    {
        _fmtCb = OnFormat;
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _maintain = new Timer(_ => { if (_want.Url.Length > 0 || _player != null) Kick(); }, null, 2000, 2000);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    // ---------------- interface para as telas ----------------
    public ImageSource? Frame => _bmp;

    /// <summary>Verdadeiro enquanto a janela de tela cheia estiver aberta (usa o fluxo principal e imagem maior).</summary>
    public bool FullscreenActive { get => _fullscreen; set => _fullscreen = value; }

    public bool IsLive
    {
        get
        {
            long t = Interlocked.Read(ref _lastFrameTicks);
            return t != 0 && (MonoClock.UtcNow.Ticks - t) < ExpireTicks;
        }
    }

    public string StatusText => IsLive ? $"Ao vivo · {_fw}×{_fh}" : _status;

    /// <summary>Chamado pela tela a cada atualização (só na thread da tela). Liga, troca ou desliga o vídeo conforme a câmera ativa.</summary>
    public void Ensure(CameraConfig cam)
    {
        Interlocked.Exchange(ref _touchTicks, MonoClock.UtcNow.Ticks);
        EnsurePump();
        var want = Decide(cam);
        if (want == _want) return;
        _want = want;
        Kick();
    }

    private Want Decide(CameraConfig cam)
    {
        if (!cam.VideoEnabled) { _status = "Vídeo desligado."; return Off; }
        string main = (cam.RtspUrl ?? "").Trim(), sub = (cam.RtspSubUrl ?? "").Trim();
        string url;
        if (_fullscreen) url = main.Length > 0 ? main : sub;
        else if (cam.VideoUsePreviewStream) url = sub.Length > 0 ? sub : main;
        else url = main.Length > 0 ? main : sub;
        if (url.Length == 0) { _status = "Informe o endereço RTSP em Ajustes (ou use Descobrir)."; return Off; }
        if (!RtspAddress.IsValid(url)) { _status = "Endereço RTSP inválido. Use o formato rtsp://192.168.1.100/1"; return Off; }
        url = RtspAddress.WithCredentials(url, cam.RtspUser, cam.RtspPassword);
        int cache = cam.VideoLowLatency ? 120 : 300;      // menos atraso ajuda a enquadrar à mão; mais cache aguenta melhor uma rede ruim
        return _fullscreen ? new Want(url, 1280, 720, cam.VideoHardwareDecode, cache) : new Want(url, 640, 360, cam.VideoHardwareDecode, cache);
    }

    // ---------------- cópia dos quadros para a tela (prioridade baixa) ----------------
    private void EnsurePump()
    {
        if (_pump != null) return;
        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
    }

    private void Pump()
    {
        if (_want.Url.Length > 0 && MonoClock.UtcNow.Ticks - Interlocked.Read(ref _touchTicks) > ExpireTicks)
        {
            _want = Off;                                    // ninguém está vendo: para o vídeo (poupa CPU e rede)
            _status = "Vídeo em pausa (a tela do Controle PTZ não está visível).";
            Kick();
            return;
        }
        bool replaced = false;
        lock (_sync)
        {
            if (!_ready || _front == IntPtr.Zero || _fw <= 0 || _fh <= 0) return;
            if (_bmp == null || _bmp.PixelWidth != _fw || _bmp.PixelHeight != _fh)
            {
                _bmp = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Bgr32, null);
                replaced = true;
            }
            _bmp.WritePixels(new Int32Rect(0, 0, _fw, _fh), _front, _fw * _fh * 4, _fw * 4);
            _ready = false;
        }
        if (replaced) FrameChanged?.Invoke();
    }

    // ---------------- callbacks do LibVLC (threads do VLC) ----------------
    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        double sw = Math.Max(16.0, (double)width), sh = Math.Max(16.0, (double)height);
        double k = Math.Min(1.0, Math.Min(_maxW / sw, _maxH / sh));      // reduz mantendo a proporção; nunca aumenta
        int w = Math.Max(16, (int)(sw * k) / 8 * 8);
        int h = Math.Max(16, (int)(sh * k) / 2 * 2);
        width = (uint)w; height = (uint)h; pitches = (uint)(w * 4); lines = (uint)h;
        int size = w * h * 4;
        lock (_sync)
        {
            var a = Marshal.AllocHGlobal(size);
            var b = Marshal.AllocHGlobal(size);
            _allocs.Add(a); _allocs.Add(b);
            _back = a; _front = b; _w = w; _h = h; _ready = false;
        }
        return 1;
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _back);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            (_back, _front) = (_front, _back);
            _fw = _w; _fh = _h; _ready = true;
        }
        Interlocked.Exchange(ref _lastFrameTicks, MonoClock.UtcNow.Ticks);
    }

    // ---------------- tarefa de fundo: abre, troca, reconecta e para ----------------
    private void Kick() => Task.Run(Apply);

    private void Apply()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) { Volatile.Write(ref _again, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref _again, 0);
                try { ApplyOnce(_want); }
                catch (Exception ex) { _status = "Erro no vídeo: " + ex.Message; }
            } while (Volatile.Read(ref _again) == 1);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
        if (Volatile.Read(ref _again) == 1) Kick();
    }

    private void ApplyOnce(Want want)
    {
        var now = MonoClock.UtcNow;
        if (want == _cur)
        {
            if (want.Url.Length == 0) return;
            if (_player != null && !IsBad(now))
            {
                if (Interlocked.Read(ref _lastFrameTicks) > _startedAt.Ticks) _fails = 0;
                return;
            }
            if (now < _retryAt) return;
            _fails++;
            _status = "Sem imagem da câmera. Tentando reconectar… (confira o endereço RTSP, o usuário e a senha)";
        }
        else _fails = 0;

        StopCore();
        _cur = want;
        if (want.Url.Length == 0) return;
        StartCore(want);
    }

    private bool IsBad(DateTime now)
    {
        var p = _player;
        if (p == null) return true;
        if ((now - _startedAt).TotalSeconds < 3) return false;           // dá um tempo para a conexão abrir
        try
        {
            var st = p.State;
            if (st == VlcState.Error || st == VlcState.Ended || st == VlcState.Stopped) return true;
        }
        catch { return true; }
        long last = Interlocked.Read(ref _lastFrameTicks);
        var reference = last > _startedAt.Ticks ? new DateTime(last, DateTimeKind.Utc) : _startedAt;
        return (now - reference).TotalSeconds > 12;                      // sem quadros novos há muito tempo
    }

    private bool EnsureLib()
    {
        if (_libReady) return true;
        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string dir = Path.Combine(baseDir, "libvlc", "win-x64");
        if (!File.Exists(Path.Combine(dir, "libvlc.dll")))
        {
            _libMsg = "O componente de vídeo (pasta libvlc ao lado do PTZStudio.exe) não foi encontrado.";
            return false;
        }
        try { VlcCore.Initialize(dir); _libReady = true; return true; }
        catch (Exception ex) { _libMsg = "Não foi possível carregar o componente de vídeo: " + ex.Message; return false; }
    }

    private void StartCore(Want want)
    {
        _retryAt = MonoClock.UtcNow.AddSeconds(Math.Min(15, 2 + 2 * _fails));
        if (!EnsureLib()) { _status = _libMsg; return; }
        try
        {
            _libvlc ??= new VlcLib("--no-audio", "--no-osd");
            _maxW = want.MaxW; _maxH = want.MaxH;
            var media = new VlcMedia(_libvlc, want.Url, VlcFromType.FromLocation);
            media.AddOption(":no-audio");                 // o som da câmera não toca no computador
            media.AddOption(":rtsp-tcp");                 // TCP: sem perda de pacotes nem problema de firewall
            media.AddOption($":network-caching={want.CacheMs}");      // atraso de rede (padrão 300 ms; modo baixa latência 120 ms)
            media.AddOption(want.Hw ? ":avcodec-hw=any" : ":avcodec-hw=none");   // por padrão sem decodificação pela placa de vídeo (mais estável)
            var player = new VlcPlayer(_libvlc);
            player.SetVideoFormatCallbacks(_fmtCb, null);
            player.SetVideoCallbacks(_lockCb, null, _displayCb);
            player.Media = media;
            _media = media; _player = player;
            _startedAt = MonoClock.UtcNow;
            _status = "Conectando ao vídeo…";
            player.Play();
        }
        catch (Exception ex)
        {
            _status = "Não foi possível iniciar o vídeo: " + ex.Message;
            StopCore();
        }
    }

    private void StopCore()
    {
        var p = _player; var m = _media;
        _player = null; _media = null;
        try { p?.Stop(); } catch { }
        try { p?.Dispose(); } catch { }
        try { m?.Dispose(); } catch { }
        lock (_sync)
        {
            foreach (var ptr in _allocs) Marshal.FreeHGlobal(ptr);
            _allocs.Clear();
            _back = _front = IntPtr.Zero; _ready = false;
        }
        Interlocked.Exchange(ref _lastFrameTicks, 0);
    }

    private void Shutdown()
    {
        _want = Off;
        try { Task.Run(Apply).Wait(1500); } catch { }
    }
}
