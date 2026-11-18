using System.Diagnostics;
using PtzStudio.Core.Models;
using PtzStudio.Core.Motion;
using PtzStudio.Core.Visca;

namespace PtzStudio.Core.Control;

/// <summary>
/// Converte entradas (analógico físico, joystick virtual, botões) em comandos VISCA, com as proteções:
/// parada ao neutro, reforço da parada, parada reafirmada quando a câmera volta, guardião independente da tela,
/// confirmação (ACK) e reenvio de comandos únicos e anti-repetição.
/// O controle físico e o joystick virtual ficam ATIVOS AO MESMO TEMPO. Para o movimento continuar proporcional, as duas
/// entradas nunca são somadas: a que saiu do centro primeiro mantém o comando até voltar ao centro (em caso de empate, o
/// controle físico vence). Um clique deliberado no joystick virtual sempre retoma o comando.
/// </summary>
public sealed class PtzController
{
    public const int WatchdogMs = 600;
    public const int KeepAliveMs = 400;
    /// <summary>Piso da zona morta usado só para decidir se uma entrada está "em uso" (evita que a folga do analógico em repouso tome o comando).</summary>
    private const double UseThreshold = 0.02;
    /// <summary>Depois de acionar um preset ou o Home, a parada reafirmada espera este tempo para não interromper o deslocamento.</summary>
    private const int TravelGraceSeconds = 10;

    private readonly IViscaSender _tx;
    private readonly Func<CameraConfig> _cam;
    private readonly Func<Profile> _profile;
    private readonly MotionProcessor _motion = new();
    private readonly object _lock = new();

    private double _px, _py, _pz;       // entrada do controle físico
    private double _vx, _vy, _vz;       // entrada do joystick virtual (mouse)
    private InputSource? _owner;        // quem está no comando agora (nulo = ninguém)
    private bool _suppressPhysical;     // o mouse retomou o comando: o físico é ignorado até voltar ao centro
    private int _digPan, _digTilt, _digZoom, _digFocus;
    private DateTime _lastInput = DateTime.MinValue;
    private int _sentPan, _sentTilt, _sentZoom, _sentFocus;
    private readonly StopRepeat _ptRep = new(), _zoomRep = new(), _focusRep = new();
    private DateTime _lastPt = DateTime.MinValue, _lastZoom = DateTime.MinValue;
    private bool _requireNeutral;
    private bool _presetLatch;          // preset/Home em andamento: o analógico só volta a mandar depois de passar pelo centro
    private int _virtFocus;             // foco pelos botões da tela (o do controle físico entra por SetDigital)
    private string _travelName = "";
    private double _trigZoom;
    private bool _trigZoomWasOn;
    private (int slot, DateTime when)? _saveWatch;
    private (double x, double y, double z, DateTime until)? _pulse;
    private (int dir, DateTime until)? _focusPulse;

    // guardião (thread independente da tela)
    private long _lastTickStamp;        // Stopwatch.GetTimestamp() do último ciclo de controle
    private int _tripFlag;              // 1 = o guardião mandou parar; o próximo ciclo exige neutro
    private long _lastTripStamp;

    // confirmação de comandos únicos (preset, Home)
    private sealed class PendingCmd
    {
        public byte[] Packet = Array.Empty<byte>();
        public string Name = "";
        public DateTime Sent;
        public int Retries;
        public int AcksSeen;
        public int AcksExpected;
        public bool Rejected;           // a câmera respondeu "buffer cheio" / "sem socket"
    }
    private PendingCmd? _pend;
    private bool _ackSupported;         // aprendido: a câmera já respondeu ACK a algum comando
    private DateTime _travelUntil = DateTime.MinValue;
    private DateTime _lastErrMsg = DateTime.MinValue;

    /// <summary>
    /// Reforço da PARADA. UDP não garante entrega: se os poucos pacotes de parada se perderem, a câmera seguiria andando.
    /// Depois de parar um eixo, a parada é repetida em intervalos crescentes (cerca de 5,5 s no total) — só enquanto
    /// aquele eixo estiver parado e nenhum comando novo tiver saído. Acaba sozinho: não "briga" com o controle remoto da câmera.
    /// </summary>
    private sealed class StopRepeat
    {
        private static readonly int[] Schedule = { 30, 90, 250, 600, 1200, 2000, 3500, 5500 };   // ms depois da primeira parada
        private DateTime _at;
        private bool _anchored;
        private int _step = Schedule.Length;                                                       // nada pendente

        /// <summary>Começa a contar a partir do próximo ciclo (usa o relógio do Tick, não o do sistema).</summary>
        public void Arm() { _step = 0; _anchored = false; }
        public void Cancel() => _step = Schedule.Length;

        public bool Due(DateTime now)
        {
            if (_step >= Schedule.Length) return false;
            if (!_anchored) { _at = now; _anchored = true; return false; }
            if ((now - _at).TotalMilliseconds < Schedule[_step]) return false;
            _step++;
            return true;
        }
    }

    /// <summary>Trava leve (aguarda o neutro): usada em paradas de segurança automáticas (watchdog, controle desconectado...).</summary>
    public bool SafetyLatched => _requireNeutral;
    public int SentPan => _sentPan;
    public int SentTilt => _sentTilt;
    public int SentZoom => _sentZoom;
    /// <summary>Verdadeiro depois que a câmera respondeu ACK a algum comando (só então o reenvio por falta de ACK é ligado).</summary>
    public bool CameraAcksCommands => _ackSupported;
    /// <summary>Preset ou Home em andamento: o analógico fica ignorado até voltar ao centro, para não cortar o deslocamento.</summary>
    public bool WaitingNeutralAfterPreset { get { lock (_lock) return _presetLatch; } }
    /// <summary>Nome do destino ("preset 3", "Home") enquanto a câmera está a caminho; nulo se não há deslocamento em curso.</summary>
    public string? Traveling { get { lock (_lock) return MonoClock.UtcNow < _travelUntil ? _travelName : null; } }
    public event Action<string>? Message;

    /// <summary>Qual entrada está no comando agora (Fisico, Virtual) ou nulo se nenhuma está em uso.</summary>
    public InputSource? InControl { get { lock (_lock) return _owner; } }

    public PtzController(IViscaSender tx, Func<CameraConfig> cam, Func<Profile> profile)
    {
        _tx = tx; _cam = cam; _profile = profile;
    }

    /// <summary>Liga as respostas da câmera (ACK, erro) ao controlador, para confirmar e reenviar comandos únicos.</summary>
    public void AttachFeedback(IViscaFeedback feedback) => feedback.ResponseReceived += OnResponse;

    /// <summary>Recebe a posição do analógico físico ou do joystick virtual. As duas fontes são sempre aceitas.</summary>
    public void SetAnalog(InputSource src, double x, double y, double z, DateTime now)
    {
        lock (_lock)
        {
            if (src == InputSource.Fisico) { _px = x; _py = y; _pz = z; }
            else { _vx = x; _vy = y; _vz = z; }
            _lastInput = now;
        }
    }

    public void SetDigital(int pan, int tilt, int zoom, int focus)
    {
        lock (_lock) { _digPan = pan; _digTilt = tilt; _digZoom = zoom; _digFocus = focus; }
    }

    /// <summary>Foco pelos botões da tela (−1 perto, +1 longe, 0 parar). Soma-se ao foco do controle físico.</summary>
    /// <summary>
    /// Zoom pelos gatilhos analógicos (RT/LT): −1..+1 proporcional à pressão. Soma-se ao zoom dos botões. Ao soltar (volta a 0)
    /// a câmera recebe a parada de zoom no mesmo ciclo, sem rampa.
    /// </summary>
    public void SetTriggerZoom(double z)
    {
        lock (_lock) _trigZoom = double.IsNaN(z) ? 0 : Math.Clamp(z, -1, 1);
    }

    public void SetVirtualFocus(int dir)
    {
        lock (_lock) _virtFocus = Math.Sign(dir);
    }

    /// <summary>
    /// O joystick virtual foi pressionado de propósito: ele assume o comando agora e o controle físico é ignorado até voltar ao centro.
    /// Garante que o mouse sempre consiga retomar o comando de um controle travado.
    /// </summary>
    public void RequestVirtualControl()
    {
        lock (_lock) { _owner = InputSource.Virtual; _suppressPhysical = true; }
    }

    /// <summary>Teste de direção: injeta uma entrada por um curto tempo (passa pelas inversões do perfil).</summary>
    public void Pulse(double x, double y, double z, int ms, DateTime now)
    {
        lock (_lock) { _pulse = (x, y, z, now.AddMilliseconds(ms)); _lastInput = now; }
    }

    /// <summary>Teste de foco: aciona o foco por um curto tempo e para (a parada é repetida por segurança).</summary>
    public void PulseFocus(int dir, int ms, DateTime now)
    {
        lock (_lock) { _focusPulse = (Math.Sign(dir), now.AddMilliseconds(ms)); _lastInput = now; }
    }

    private static bool IsActive(double x, double y, double z, MotionSettings m) =>
        Math.Abs(x) > Math.Max(UseThreshold, m.Pan.Deadzone) ||
        Math.Abs(y) > Math.Max(UseThreshold, m.Tilt.Deadzone) ||
        Math.Abs(z) > Math.Max(UseThreshold, m.Zoom.Deadzone);

    /// <summary>
    /// Escolhe a entrada que comanda a câmera. Nunca soma as duas: a que saiu do centro primeiro segura o comando até voltar
    /// ao centro (zona morta do perfil); a outra é ignorada nesse período. Empate no mesmo ciclo: o controle físico.
    /// Exceção: depois de RequestVirtualControl, o físico é ignorado até voltar ao centro.
    /// </summary>
    private (double x, double y, double z) PickInput(MotionSettings m)
    {
        bool physical = IsActive(_px, _py, _pz, m);
        bool virt = IsActive(_vx, _vy, _vz, m);
        if (_suppressPhysical)
        {
            if (!physical) _suppressPhysical = false;
            else physical = false;
        }
        if (_owner == InputSource.Fisico && !physical) _owner = null;
        if (_owner == InputSource.Virtual && !virt) _owner = null;
        if (_owner == null)
        {
            if (physical) _owner = InputSource.Fisico;
            else if (virt) _owner = InputSource.Virtual;
        }
        if (_owner == InputSource.Virtual) return (_vx, _vy, _vz);
        if (_owner == InputSource.Fisico) return (_px, _py, _pz);
        return (0, 0, 0);       // ninguém no comando (ex.: físico ainda ignorado depois que o mouse soltou): nada se move
    }

    public void Tick(double dt, DateTime now)
    {
        Interlocked.Exchange(ref _lastTickStamp, Stopwatch.GetTimestamp());
        lock (_lock)
        {
            if (Interlocked.Exchange(ref _tripFlag, 0) == 1) ConsumeTrip();
            ServicePending(MonoClock.UtcNow);

            var cam = _cam();
            var prof = _profile();
            double ax, ay, az;

            if (_pulse is { } pl)
            {
                if (now < pl.until) { ax = pl.x; ay = pl.y; az = pl.z; _lastInput = now; }
                else { _pulse = null; ax = ay = az = 0; }
            }
            else
            {
                (ax, ay, az) = PickInput(prof.Motion);
                ax = Math.Clamp(ax + _digPan, -1, 1);
                ay = Math.Clamp(ay + _digTilt, -1, 1);
                az = Math.Clamp(az + _digZoom + _trigZoom, -1, 1);
            }

            int focus = Math.Clamp(_digFocus + _virtFocus, -1, 1);
            if (_focusPulse is { } fp)
            {
                if (now < fp.until) { focus = fp.dir; _lastInput = now; }
                else _focusPulse = null;
            }

            // "Neutro" = dentro da zona morta do perfil. A folga normal de um analógico em repouso (acima de 0,001 mas abaixo da zona
            // morta) não impede mais o controle de voltar depois de uma parada de segurança ou de um preset.
            bool neutral = focus == 0 && !IsActive(ax, ay, az, prof.Motion);
            if (_requireNeutral || _presetLatch)
            {
                if (neutral) { _requireNeutral = false; _presetLatch = false; }
                else { Settle(cam, now); return; }
            }

            bool moving = _sentPan != 0 || _sentTilt != 0 || _sentZoom != 0;
            if (moving && _pulse == null && (now - _lastInput).TotalMilliseconds > WatchdogMs)
            {
                SafetyStopLocked("sem atualização de entrada (watchdog)");
                return;
            }

            // Gatilho de zoom solto: parar na hora, sem a rampa de desaceleração do perfil.
            if (_trigZoom != 0) _trigZoomWasOn = true;
            else if (_trigZoomWasOn)
            {
                _trigZoomWasOn = false;
                if (!IsActive(0, 0, az, prof.Motion)) _motion.ResetZoom();
            }

            var o = _motion.Process(ax, ay, az, dt, prof.Motion);
            // Pan/Tilt: porcentagem do perfil (1% a 100%). Zoom: independente — só o joystick virtual tem a sua intensidade própria.
            double ptScale = Math.Clamp(prof.Motion.PanTiltSpeedPercent, 1, 100) / 100.0;
            double zf = prof.Motion.ReducePanTiltWithZoom ? ZoomSpeedFactor(ZoomFraction, prof.Motion.ZoomReductionMinPercent) : 1.0;
            Volatile.Write(ref _zoomFactorBits, BitConverter.DoubleToInt64Bits(zf));
            ptScale *= zf;
            double zoomScale = _owner == InputSource.Virtual ? Math.Clamp(prof.Motion.VirtualZoomPercent, 1, 100) / 100.0 : 1.0;
            int pan = ToInt(o.Pan, ptScale, cam.MaxPanSpeed);
            int tilt = ToInt(o.Tilt, ptScale, cam.MaxTiltSpeed);
            int zoom = ToInt(o.Zoom, zoomScale, cam.MaxZoomSpeed);

            ApplyPanTilt(cam, pan, tilt, now);
            ApplyZoom(cam, zoom, now);
            ApplyFocus(cam, focus, now);
        }
    }

    /// <summary>Único ponto de saída de comandos do controlador (exceto a parada fora de banda do guardião).</summary>
    private void Tx(byte[] packet) => _tx.Send(packet);

    // ---------------- redução de Pan/Tilt com o zoom ----------------
    private long _zoomFracBits = BitConverter.DoubleToInt64Bits(-1);
    private long _zoomFactorBits = BitConverter.DoubleToInt64Bits(1);

    /// <summary>Posição do zoom lida da câmera (0 = aberto, 1 = máximo; -1 = desconhecida). Pode ser atualizada de qualquer thread.</summary>
    public double ZoomFraction
    {
        get => BitConverter.Int64BitsToDouble(Volatile.Read(ref _zoomFracBits));
        set => Volatile.Write(ref _zoomFracBits, BitConverter.DoubleToInt64Bits(double.IsNaN(value) ? -1 : value));
    }

    /// <summary>Multiplicador aplicado agora à velocidade de Pan/Tilt pela redução com o zoom (1 = sem redução).</summary>
    public double PanTiltZoomFactor => BitConverter.Int64BitsToDouble(Volatile.Read(ref _zoomFactorBits));

    /// <summary>
    /// Quanto da velocidade de Pan/Tilt sobra com o zoom nesta posição: 1 com o zoom aberto, caindo devagar no começo e mais
    /// no fim, até <paramref name="minPercent"/> no zoom máximo. Posição desconhecida = sem redução.
    /// O movimento nunca para por causa disso: a menor velocidade enviada continua sendo 1.
    /// </summary>
    public static double ZoomSpeedFactor(double fraction, int minPercent)
    {
        if (double.IsNaN(fraction) || fraction < 0) return 1.0;
        double min = Math.Clamp(minPercent, 5, 100) / 100.0;
        double f = Math.Clamp(fraction, 0, 1);
        return 1.0 - (1.0 - min) * Math.Pow(f, 1.5);
    }

    private static int ToInt(double speed, double level, int cameraMax)
    {
        if (Math.Abs(speed) < 0.5) return 0;
        int i = (int)Math.Max(1, Math.Round(Math.Abs(speed) * level));
        return Math.Sign(speed) * Math.Min(i, Math.Max(1, cameraMax));
    }

    private void ApplyPanTilt(CameraConfig cam, int pan, int tilt, DateTime now)
    {
        if (pan == 0 && tilt == 0)
        {
            if (_sentPan != 0 || _sentTilt != 0)
            {
                Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress));
                _sentPan = _sentTilt = 0; _lastPt = now; _ptRep.Arm();
            }
            else if (_ptRep.Due(now)) Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress));
            return;
        }
        bool changed = pan != _sentPan || tilt != _sentTilt;
        double since = (now - _lastPt).TotalMilliseconds;
        if ((changed && since >= cam.CommandIntervalMs) || (!changed && since >= KeepAliveMs))
        {
            Tx(ViscaCommands.PanTiltDrive(cam.ViscaAddress, pan, tilt, cam.MaxPanSpeed, cam.MaxTiltSpeed));
            _sentPan = pan; _sentTilt = tilt; _lastPt = now; _ptRep.Cancel();
            _pend = null; _travelUntil = DateTime.MinValue;    // movimento manual: o operador assumiu, o preset deixa de ser reenviado
        }
    }

    private void ApplyZoom(CameraConfig cam, int zoom, DateTime now)
    {
        if (zoom == 0)
        {
            if (_sentZoom != 0)
            {
                Tx(ViscaCommands.Zoom(cam.ViscaAddress, 0));
                _sentZoom = 0; _lastZoom = now; _zoomRep.Arm();
            }
            else if (_zoomRep.Due(now)) Tx(ViscaCommands.Zoom(cam.ViscaAddress, 0));
            return;
        }
        bool changed = zoom != _sentZoom;
        double since = (now - _lastZoom).TotalMilliseconds;
        if ((changed && since >= cam.CommandIntervalMs) || (!changed && since >= KeepAliveMs))
        {
            Tx(ViscaCommands.Zoom(cam.ViscaAddress, zoom, cam.MaxZoomSpeed));
            _sentZoom = zoom; _lastZoom = now; _zoomRep.Cancel();
            _pend = null; _travelUntil = DateTime.MinValue;
        }
    }

    private void ApplyFocus(CameraConfig cam, int dir, DateTime now)
    {
        if (dir != _sentFocus)
        {
            Tx(ViscaCommands.Focus(cam.ViscaAddress, dir));
            _sentFocus = dir;
            if (dir == 0) _focusRep.Arm(); else _focusRep.Cancel();
        }
        else if (dir == 0 && _focusRep.Due(now))
            Tx(ViscaCommands.Focus(cam.ViscaAddress, 0)); // UDP não garante entrega: um foco "preso" é pior que um pacote a mais
    }

    // Repete as paradas pendentes enquanto a parada de segurança aguarda o neutro.
    private void Settle(CameraConfig cam, DateTime now)
    {
        if (_ptRep.Due(now)) Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress));
        if (_zoomRep.Due(now)) Tx(ViscaCommands.Zoom(cam.ViscaAddress, 0));
        if (_focusRep.Due(now)) Tx(ViscaCommands.Focus(cam.ViscaAddress, 0));
    }

    // ---------------- guardião: parada fora do ciclo normal ----------------
    private static CameraConfig SafeCam(Func<CameraConfig> f)
    {
        try { return f(); } catch { return new CameraConfig(); }
    }

    /// <summary>
    /// Manda parar Pan/Tilt, Zoom e Foco SEM depender da trava do controlador nem da thread da tela (usada pelo guardião e pelo
    /// encerramento inesperado do programa). O próximo ciclo de controle passa a exigir o neutro.
    /// </summary>
    public void ForceStopOutOfBand(string reason)
    {
        var cam = SafeCam(_cam);
        for (int i = 0; i < 2; i++)
        {
            try
            {
                _tx.Send(ViscaCommands.PanTiltStop(cam.ViscaAddress));
                _tx.Send(ViscaCommands.Zoom(cam.ViscaAddress, 0));
                _tx.Send(ViscaCommands.Focus(cam.ViscaAddress, 0));
            }
            catch { }
            if (i == 0) Thread.Sleep(40);
        }
        Volatile.Write(ref _tripFlag, 1);
        try { Message?.Invoke("Parada de proteção: " + reason); } catch { }
    }

    /// <summary>
    /// Chamado pelo guardião a cada ~100 ms. Se o ciclo de controle ficou parado por mais de limitMs com a câmera em movimento,
    /// envia a parada por conta própria (repete a cada 1,5 s enquanto o congelamento durar). Devolve true se agiu.
    /// </summary>
    public bool GuardianCheck(int limitMs)
    {
        long last = Interlocked.Read(ref _lastTickStamp);
        if (last == 0) return false;                                            // o ciclo ainda não começou
        long nowStamp = Stopwatch.GetTimestamp();
        double ageMs = (nowStamp - last) * 1000.0 / Stopwatch.Frequency;
        if (ageMs < limitMs) return false;
        bool moving = Volatile.Read(ref _sentPan) != 0 || Volatile.Read(ref _sentTilt) != 0 ||
                      Volatile.Read(ref _sentZoom) != 0 || Volatile.Read(ref _sentFocus) != 0;
        if (!moving && Volatile.Read(ref _tripFlag) == 0) return false;         // nada a proteger
        long lastTrip = Interlocked.Read(ref _lastTripStamp);
        if (lastTrip != 0 && (nowStamp - lastTrip) * 1000.0 / Stopwatch.Frequency < 1500) return false;
        Interlocked.Exchange(ref _lastTripStamp, nowStamp);
        ForceStopOutOfBand("o ciclo de controle parou de responder");
        return true;
    }

    private void ConsumeTrip()
    {
        _motion.Reset();
        _sentPan = _sentTilt = _sentZoom = _sentFocus = 0;
        _owner = null;
        _requireNeutral = true;
        _pend = null;
        _ptRep.Arm(); _zoomRep.Arm(); _focusRep.Arm();
    }

    /// <summary>
    /// A câmera voltou a responder (ou é a primeira conexão): se não há movimento comandado nem preset a caminho, reafirma a parada.
    /// Cobre o caso de uma parada perdida durante uma queda de rede e o de uma câmera que ficou andando depois de uma falha do programa.
    /// </summary>
    public void ReassertStopIfIdle(string reason)
    {
        lock (_lock)
        {
            bool moving = _sentPan != 0 || _sentTilt != 0 || _sentZoom != 0 || _sentFocus != 0;
            if (moving || MonoClock.UtcNow < _travelUntil) return;
            var cam = _cam();
            Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress));
            Tx(ViscaCommands.Zoom(cam.ViscaAddress, 0));
            Tx(ViscaCommands.Focus(cam.ViscaAddress, 0));
            _ptRep.Arm(); _zoomRep.Arm(); _focusRep.Arm();
        }
    }

    // ---------------- confirmação e reenvio de comandos únicos ----------------
    private void BeginPending(byte[] packet, int expectedAcks, string name)
    {
        _pend = new PendingCmd { Packet = packet, Name = name, Sent = MonoClock.UtcNow, AcksExpected = expectedAcks };
        _travelUntil = MonoClock.UtcNow.AddSeconds(TravelGraceSeconds);
    }

    private void OnResponse(ViscaResponse r)
    {
        lock (_lock)
        {
            if (r.Kind == ViscaResponseKind.Ack)
            {
                _ackSupported = true;
                if (_pend != null) _pend.AcksSeen++;
            }
            else if (r.Kind == ViscaResponseKind.Erro)
            {
                byte code = r.Payload.Length >= 4 ? r.Payload[2] : (byte)0;
                var nowM = MonoClock.UtcNow;
                if (code == 0x02 || code == 0x41)
                {
                    // Comando recusado (sintaxe / não executável). Para gravar ou acionar preset, quase sempre é um número que a câmera não tem.
                    if (_saveWatch is { } sw && (nowM - sw.when).TotalSeconds < 2)
                    {
                        _saveWatch = null;
                        Say($"A câmera RECUSOU gravar o preset {sw.slot}. Confira se esse número existe nela (aba Câmera > maior número de preset).");
                        return;
                    }
                    if (_pend != null && (nowM - _pend.Sent).TotalSeconds < 2)
                    {
                        string what = _pend.Name; _pend = null; _travelUntil = DateTime.MinValue;
                        Say($"A câmera RECUSOU {what}. Confira se esse número existe nela (aba Câmera > maior número de preset).");
                        return;
                    }
                }
                if (_pend != null && (code == 0x03 || code == 0x05)) _pend.Rejected = true;   // buffer cheio / sem socket: tenta de novo
                if (code == 0x04 || code == 0x03 || code == 0x05) return;                     // normais quando outro comando assume
                if ((MonoClock.UtcNow - _lastErrMsg).TotalSeconds < 10) return;
                _lastErrMsg = MonoClock.UtcNow;
                try { Message?.Invoke("A câmera recusou um comando: " + DescribeError(code) + "."); } catch { }
            }
        }
    }

    private void Say(string text) { try { Message?.Invoke(text); } catch { } }

    private static string DescribeError(byte code) => code switch
    {
        0x02 => "erro de sintaxe",
        0x03 => "memória de comandos cheia",
        0x04 => "comando cancelado",
        0x05 => "sem comando para cancelar",
        0x41 => "comando não executável agora",
        _ => $"erro 0x{code:X2}"
    };

    /// <summary>Reenvia preset/Home se a câmera não confirmou (só quando ela já confirmou comandos antes) ou recusou por buffer cheio.</summary>
    public void ServicePending(DateTime utcNow)
    {
        lock (_lock)
        {
            var p = _pend;
            if (p == null) return;
            double age = (utcNow - p.Sent).TotalMilliseconds;
            if (p.Rejected)
            {
                if (age < 80) return;
                Resend(p, utcNow);
                return;
            }
            if (!_ackSupported) { if (age > 2000) _pend = null; return; }      // câmera que não confirma comandos: nada a esperar
            if (p.AcksSeen >= p.AcksExpected) { _pend = null; return; }
            if (age < 250) return;
            Resend(p, utcNow);
        }
    }

    private void Resend(PendingCmd p, DateTime utcNow)
    {
        if (p.Retries >= 2)
        {
            _pend = null;
            try { Message?.Invoke($"Sem confirmação da câmera para {p.Name}. Confira a câmera e tente de novo."); } catch { }
            return;
        }
        p.Retries++; p.Sent = utcNow; p.AcksSeen = 0; p.AcksExpected = 1; p.Rejected = false;
        Tx(p.Packet);
    }

    // ---------------- paradas e comandos diretos ----------------
    /// <summary>Para tudo imediatamente, sem trava.</summary>
    public void StopAll(string reason)
    {
        lock (_lock) StopAllLocked(reason);
    }

    /// <summary>Parada de segurança automática (watchdog, controle desconectado, câmera sem resposta): só volta a mover depois que todas as entradas voltarem ao neutro.</summary>
    public void SafetyStop(string reason)
    {
        lock (_lock) SafetyStopLocked(reason);
    }

    private void CancelStopRepeats() { _ptRep.Cancel(); _zoomRep.Cancel(); _focusRep.Cancel(); }

    private void StopAllLocked(string reason)
    {
        var cam = _cam();
        _px = _py = _pz = 0;
        _vx = _vy = _vz = 0;
        _owner = null;
        _suppressPhysical = false;
        _presetLatch = false;
        _virtFocus = 0;
        _travelUntil = DateTime.MinValue;
        _pulse = null;
        _focusPulse = null;
        _pend = null;
        _motion.Reset();
        Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress));
        Tx(ViscaCommands.Zoom(cam.ViscaAddress, 0));
        Tx(ViscaCommands.Focus(cam.ViscaAddress, 0));
        _sentPan = _sentTilt = _sentZoom = _sentFocus = 0;
        _ptRep.Arm(); _zoomRep.Arm(); _focusRep.Arm();
        Message?.Invoke("Parado: " + reason);
    }

    private void SafetyStopLocked(string reason)
    {
        StopAllLocked(reason);
        var cam = _cam();
        Tx(ViscaCommands.PanTiltStop(cam.ViscaAddress)); // repetição deliberada: UDP não garante entrega
        _requireNeutral = true;
        Message?.Invoke("Parada de segurança: " + reason);
    }

    /// <summary>Grava a posição atual no preset. Devolve falso (e avisa) se o número está fora do que esta câmera aceita.</summary>
    public bool SavePreset(int slot)
    {
        lock (_lock)
        {
            var cam = _cam();
            int max = Math.Clamp(cam.PresetSlotMax, 0, 254);
            if (slot < 0 || slot > max) { Say($"O preset {slot} está fora da faixa desta câmera (0 a {max}). Nada foi gravado."); return false; }
            Tx(ViscaCommands.PresetSet(cam.ViscaAddress, slot));
            _saveWatch = (slot, MonoClock.UtcNow);
            return true;
        }
    }

    /// <summary>
    /// Aciona o preset. Regra: o preset GANHA do analógico — se o analógico estiver inclinado quando o preset é acionado, ele fica
    /// ignorado até voltar ao centro (senão o movimento manual cancelaria o deslocamento no meio do caminho).
    /// </summary>
    public bool RecallPreset(int slot)
    {
        lock (_lock)
        {
            var cam = _cam();
            int max = Math.Clamp(cam.PresetSlotMax, 0, 254);
            if (slot < 0 || slot > max) { Say($"O preset {slot} está fora da faixa desta câmera (0 a {max})."); return false; }
            StopAllLocked("antes de acionar preset");
            var pkt = ViscaCommands.PresetRecall(cam.ViscaAddress, slot);
            Tx(pkt);
            CancelStopRepeats();   // uma parada repetida agora interromperia o deslocamento até o preset
            _presetLatch = true;
            BeginPending(pkt, 4, $"o preset {slot}");     // 3 paradas + o recall: 4 confirmações esperadas
            _travelName = $"preset {slot}";
            return true;
        }
    }

    public void Home()
    {
        lock (_lock)
        {
            StopAllLocked("antes de ir ao Home");
            var pkt = ViscaCommands.Home(_cam().ViscaAddress);
            Tx(pkt);
            CancelStopRepeats();
            _presetLatch = true;
            BeginPending(pkt, 4, "o Home");
            _travelName = "Home";
        }
    }
}
