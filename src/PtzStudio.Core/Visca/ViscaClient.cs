using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PtzStudio.Core.Models;

namespace PtzStudio.Core.Visca;

public enum ConnectionState { Desconectado, Conectando, Conectado, ErroDeComunicacao }

public interface IViscaSender
{
    void Send(byte[] payload);
}

/// <summary>Quem envia comandos e também faz consultas (a câmera responde a uma consulta com o valor pedido).</summary>
public interface IViscaInquirer : IViscaSender
{
    ConnectionState State { get; }
    Task<ViscaResponse?> InquireAsync(byte[] inquiry, int timeoutMs = 0, Func<byte[], bool>? accept = null);
}

/// <summary>Quem recebe as respostas da câmera (ACK, conclusão, erro, consulta).</summary>
public interface IViscaFeedback
{
    event Action<ViscaResponse>? ResponseReceived;
}

/// <summary>
/// Cliente VISCA sobre UDP: envia comandos, recebe respostas e monitora a conexão.
/// Não guarda histórico de comandos (TX/RX); apenas avisa (ErrorReported) quando há erro de comunicação.
/// Recupera-se sozinho: se a câmera deixa de responder, a rede muda de endereço ou um envio falha, o socket é recriado.
/// </summary>
public sealed class ViscaClient : IViscaInquirer, IViscaFeedback, IDisposable
{
    /// <summary>Número máximo de verificações sem resposta antes de "Desconectado" (cerca de 7 s no total). O valor da câmera nunca passa disto.</summary>
    public const int MaxFailuresBeforeLost = 3;
    /// <summary>A verificação periódica nunca espera mais que isto por uma resposta (a queda aparece depressa).</summary>
    public const int HealthProbeTimeoutMs = 1200;

    private readonly object _gate = new();
    private readonly object _errLock = new();
    private string _lastErr = "";
    private DateTime _lastErrAt = DateTime.MinValue;
    private volatile UdpClient? _udp;
    private volatile CameraConfig _cfg = new();
    private CancellationTokenSource? _cts;
    private uint _seq;
    private DateTime _probeSentUtc;
    private volatile TaskCompletionSource<bool>? _probe;
    private volatile int _failures;
    private long _lastRxTicks;   // instante (MonoClock) do último datagrama recebido; qualquer resposta prova que a câmera está viva (M1)
    private int _gen;                 // identifica a conexão atual: laços de saúde antigos se encerram sozinhos
    private int _rebuild;             // 1 = pedir ao laço de saúde para recriar o socket
    private int _rebuildCount;
    private int _state = (int)ConnectionState.Desconectado;
    private volatile string _stateDetail = "Sem conexão";
    private volatile PendingInquiry? _inq;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    /// <summary>Uma consulta à espera de resposta: casa a resposta pelo número de sequência (VISCA over IP) e pelo formato.</summary>
    private sealed class PendingInquiry
    {
        public readonly TaskCompletionSource<ViscaResponse> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Seq = -1;                       // -1 = UDP puro (a resposta não carrega número)
        public Func<byte[], bool>? Accept;          // formato esperado da resposta
    }

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public string StateDetail => _stateDetail;
    public double LastRoundTripMs { get; private set; } = -1;
    /// <summary>Quantas vezes o socket foi recriado (diagnóstico e testes).</summary>
    public int RebuildCount => Volatile.Read(ref _rebuildCount);
    public event Action<ConnectionState, string>? StateChanged;
    public event Action<string>? ErrorReported;
    public event Action<ViscaResponse>? ResponseReceived;

    public ViscaClient()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _rebuild, 1);

    /// <summary>Quantas verificações sem resposta a câmera aguenta antes de ser dada como desconectada (1 a 3).</summary>
    public static int EffectiveAttempts(CameraConfig cfg) => Math.Clamp(cfg.ReconnectAttempts, 1, MaxFailuresBeforeLost);

    public void Connect(CameraConfig cfg)
    {
        int gen;
        bool ok = true;
        string failure = "";
        lock (_gate)
        {
            Close();
            _cfg = cfg.Clone();
            _failures = 0;
            gen = Interlocked.Increment(ref _gen);
            Interlocked.Exchange(ref _rebuild, 0);
            SetState(ConnectionState.Conectando, $"Conectando a {cfg.IpAddress}:{cfg.Port}...");
            try
            {
                OpenSocket();
            }
            catch (Exception e)
            {
                ReportError("Falha ao abrir socket: " + e.Message);
                ok = false; failure = e.Message;
            }
            if (ok)
            {
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _ = Task.Run(() => HealthLoop(ct, gen));
            }
        }
        if (!ok) SetState(ConnectionState.ErroDeComunicacao, failure);
    }

    /// <summary>Abre o socket (sempre dentro da trava _gate). No VISCA over IP, pede à câmera para zerar o número de sequência.</summary>
    private void OpenSocket()
    {
        // Só endereço IP (nada de nome de máquina): resolver nomes (DNS) travaria a tela por vários segundos.
        if (!IPAddress.TryParse((_cfg.IpAddress ?? "").Trim(), out var ip))
            throw new ArgumentException($"Endereço IP inválido: \"{_cfg.IpAddress}\". Use o formato 192.168.1.100.");
        var udp = new UdpClient(ip.AddressFamily);
        // Windows: sem isto, um ICMP "porta inalcançável" faz o PRÓXIMO envio/recepção falhar (SIO_UDP_CONNRESET).
        try { udp.Client.IOControl(-1744830452, new byte[4], null); } catch { }
        udp.Connect(ip, _cfg.Port);
        _udp = udp;
        _seq = 0;
        if (_cfg.Transport == ViscaTransportMode.ViscaOverIp)
        {
            // Sem isto, uma câmera que guardou a sequência da sessão anterior pode ignorar os comandos novos até "alcançá-la".
            try { var reset = ViscaFraming.ResetSequence(); udp.Send(reset, reset.Length); } catch { }
        }
        _ = Task.Run(() => ReceiveLoop(udp));
    }

    private void Close()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        _udp = null;
        _cts = null;
    }

    /// <summary>Fecha e abre de novo só o socket (sem mexer no laço de saúde). Usado quando a rede mudou ou a câmera parou de responder.</summary>
    private bool RebuildSocket(string why)
    {
        bool ok;
        string error = "";
        lock (_gate)
        {
            try { _udp?.Close(); } catch { }
            _udp = null;
            try { OpenSocket(); ok = true; }
            catch (Exception e) { ok = false; error = e.Message; }
        }
        Interlocked.Increment(ref _rebuildCount);
        if (ok) ReportError("Conexão com a câmera recriada (" + why + ").");
        else { ReportError("Falha ao recriar a conexão: " + error); SetState(ConnectionState.ErroDeComunicacao, error); }
        return ok;
    }

    /// <summary>Recria o socket agora (pedido do operador, ou o Windows voltou da suspensão) e volta a esperar a resposta da câmera.</summary>
    public void ForceReconnect()
    {
        _failures = 0;
        if (RebuildSocket("pedido do operador")) SetState(ConnectionState.Conectando, "Reconectando...");
    }

    public void Send(byte[] payload) => SendRaw(payload, ViscaPayloadType.Command);

    /// <summary>Envia um pacote. Devolve o número de sequência usado (VISCA over IP) ou -1 (UDP puro, ou sem socket).</summary>
    private long SendRaw(byte[] payload, ViscaPayloadType type)
    {
        UdpClient? udp;
        byte[] data;
        long seq = -1;
        lock (_gate)
        {
            udp = _udp;
            if (udp == null) return -1;
            if (_cfg.Transport == ViscaTransportMode.ViscaOverIp)
            {
                seq = _seq;
                data = ViscaFraming.Wrap(payload, _seq++, type);
            }
            else data = payload;
        }
        try
        {
            udp.Send(data, data.Length);
        }
        catch (Exception e)
        {
            ReportError("Envio falhou: " + e.Message);
            Interlocked.Exchange(ref _rebuild, 1);          // o socket pode estar inutilizável: o laço de saúde o recria
            SetState(ConnectionState.ErroDeComunicacao, e.Message);
        }
        return seq;
    }

    private async Task ReceiveLoop(UdpClient udp)
    {
        while (true)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e)
            {
                // Em UDP, "porta inalcançável" aparece aqui quando o destino recusa o pacote.
                ReportError("Recepção: " + e.Message);
                await Task.Delay(500).ConfigureAwait(false);
                if (udp != _udp) return;
                continue;
            }
            catch (Exception e)
            {
                ReportError("Recepção: " + e.Message);
                await Task.Delay(500).ConfigureAwait(false);
                if (udp != _udp) return;
                continue;
            }

            // Um assinante com defeito nunca pode derrubar o laço de recepção.
            try { HandleDatagram(r.Buffer); }
            catch (Exception e) { ReportError("Tratamento de resposta: " + e.Message); }
        }
    }

    /// <summary>
    /// A resposta serve à consulta pendente? No VISCA over IP o número de sequência tem de ser o da consulta; nos dois modos o
    /// formato tem de ser o esperado. Erros só valem se forem de "comando inválido/indisponível": os de fila de comandos
    /// (buffer cheio, cancelado) pertencem a outro comando e não podem ser tomados como resposta de uma consulta.
    /// </summary>
    public static bool AcceptsForInquiry(ViscaResponse resp, long inquirySeq, Func<byte[], bool>? accept)
    {
        if (inquirySeq >= 0 && resp.Seq is uint rs && rs != (uint)inquirySeq) return false;
        if (resp.Kind == ViscaResponseKind.Inquerito) return accept == null || accept(resp.Payload);
        if (resp.Kind == ViscaResponseKind.Erro)
        {
            byte code = resp.Payload.Length >= 4 ? resp.Payload[2] : (byte)0;
            return code == 0x01 || code == 0x02 || code == 0x41;
        }
        return false;
    }

    private void HandleDatagram(byte[] buffer)
    {
        var resp = ViscaResponse.Parse(buffer, _cfg.Transport);
        var inq = _inq;
        if (inq != null && AcceptsForInquiry(resp, inq.Seq, inq.Accept)) inq.Tcs.TrySetResult(resp);
        var probe = _probe;
        if (probe != null)
        {
            LastRoundTripMs = (MonoClock.UtcNow - _probeSentUtc).TotalMilliseconds;
            probe.TrySetResult(true);
        }
        _failures = 0;
        Volatile.Write(ref _lastRxTicks, MonoClock.UtcNow.Ticks);
        if (State != ConnectionState.Conectado) SetState(ConnectionState.Conectado, "Câmera respondendo");
        ResponseReceived?.Invoke(resp);
    }

    /// <summary>Envia uma consulta de versão e espera qualquer resposta dentro do prazo (padrão: o timeout configurado da câmera).</summary>
    public async Task<bool> TestConnectionAsync(int timeoutMs = 0)
    {
        if (_udp == null) return false;
        int wait = timeoutMs > 0 ? timeoutMs : _cfg.TimeoutMs;
        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _probe = tcs;
            _probeSentUtc = MonoClock.UtcNow;
            SendRaw(ViscaCommands.VersionInquiry(_cfg.ViscaAddress), ViscaPayloadType.Inquiry);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(wait)).ConfigureAwait(false);
            _probe = null;
            bool ok = done == tcs.Task && tcs.Task.Result;
            if (!ok) LastRoundTripMs = -1;
            return ok;
        }
        finally { _probeGate.Release(); }
    }

    /// <summary>
    /// Envia uma consulta VISCA e devolve a resposta, ou null se a câmera não responder no prazo.
    /// Uma consulta por vez (compartilha a trava com o teste de conexão, para as respostas não se misturarem).
    /// <paramref name="accept"/> descreve o formato da resposta esperada: respostas de outro formato (de uma consulta anterior
    /// que chegou atrasada, por exemplo) são ignoradas. No VISCA over IP a resposta também precisa trazer o número de sequência da consulta.
    /// </summary>
    public async Task<ViscaResponse?> InquireAsync(byte[] inquiry, int timeoutMs = 0, Func<byte[], bool>? accept = null)
    {
        if (_udp == null) return null;
        int wait = timeoutMs > 0 ? timeoutMs : _cfg.TimeoutMs;
        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pend = new PendingInquiry { Accept = accept };
            _inq = pend;
            pend.Seq = SendRaw(inquiry, ViscaPayloadType.Inquiry);
            var done = await Task.WhenAny(pend.Tcs.Task, Task.Delay(wait)).ConfigureAwait(false);
            if (done == pend.Tcs.Task) return pend.Tcs.Task.Result;
            // Sem resposta: uma resposta que chegue um instante depois não pode ser confundida com a da PRÓXIMA consulta.
            await Task.Delay(120).ConfigureAwait(false);
            return null;
        }
        finally { _inq = null; _probeGate.Release(); }
    }

    private async Task HealthLoop(CancellationToken ct, int gen)
    {
        try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested && gen == Volatile.Read(ref _gen))
        {
            int pause = 3000;
            try
            {
                // rede mudou ou um envio falhou: recria o socket
                if (Interlocked.Exchange(ref _rebuild, 0) == 1) RebuildSocket("a rede mudou ou um envio falhou");

                var cfg = _cfg;
                if (cfg.HealthCheck)
                {
                    var probeStart = MonoClock.UtcNow;
                    bool ok = await TestConnectionAsync(Math.Min(Math.Max(100, cfg.TimeoutMs), HealthProbeTimeoutMs)).ConfigureAwait(false);
                    // Câmera que responde aos comandos mas não à consulta de versão: qualquer datagrama recebido durante a espera conta como vida (M1)
                    if (!ok && Volatile.Read(ref _lastRxTicks) >= probeStart.Ticks) ok = true;
                    if (gen != Volatile.Read(ref _gen)) return;          // uma conexão mais nova assumiu: este laço é velho
                    if (!ok)
                    {
                        int failures = ++_failures;
                        int limit = EffectiveAttempts(cfg);
                        pause = 1500;                                    // depois de uma falha, confere de novo mais depressa
                        // a cada 2 falhas seguidas, recria o socket (o endereço local do PC pode ter mudado)
                        if (failures >= 2 && failures % 2 == 0) RebuildSocket($"sem resposta há {failures} verificações");
                        if (failures >= limit)
                            SetState(ConnectionState.Desconectado, "Sem resposta da câmera (recriando a conexão e tentando de novo...)");
                        else
                            SetState(ConnectionState.ErroDeComunicacao, $"Sem resposta ({failures}/{limit})");
                    }
                }
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { ReportError("Monitor: " + e.Message); try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; } }
        }
    }

    private void SetState(ConnectionState s, string detail)
    {
        var old = (ConnectionState)Interlocked.Exchange(ref _state, (int)s);
        _stateDetail = detail;
        if (old == s) return;
        try { StateChanged?.Invoke(s, detail); }
        catch (Exception e) { ReportError("Evento de estado: " + e.Message); }     // um assinante com defeito não pode derrubar o cliente
    }

    /// <summary>Registra um erro de comunicação. Erros idênticos e repetidos em menos de 5 s não se acumulam.</summary>
    private void ReportError(string text)
    {
        bool raise;
        lock (_errLock)
        {
            var now = MonoClock.UtcNow;
            raise = text != _lastErr || (now - _lastErrAt).TotalSeconds > 5;
            if (raise)
            {
                _lastErr = text; _lastErrAt = now;
            }
        }
        if (!raise) return;
        try { ErrorReported?.Invoke(text); } catch { }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        lock (_gate) Close();
    }
}
