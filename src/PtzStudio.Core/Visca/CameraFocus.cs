using PtzStudio.Core.Models;

namespace PtzStudio.Core.Visca;

public enum FocusMode { Desconhecido, Automatico, Manual }

/// <summary>
/// Foco automático da câmera. O estado NÃO é presumido: é lido da própria câmera (consulta VISCA 04 38) ao conectar, antes de
/// cada troca e depois dela para conferir. Se a câmera não responder à consulta, o estado mostrado é só o último comando enviado
/// e fica marcado como "não confirmado".
///   Autofoco ligado/desligado = modo de foco automático (contínuo) ou manual.
///   Focar agora (1 toque)     = foca uma vez e mantém (VISCA One Push AF); passa a câmera para o modo manual.
/// A interface e os botões do controle usam estes mesmos métodos, então o estado mostrado é sempre o mesmo.
/// </summary>
public sealed class CameraFocus
{
    private readonly IViscaInquirer _client;
    private readonly Func<CameraConfig> _cam;
    private int _mode;                 // FocusMode
    private int _confirmed;            // 1 = o modo foi lido da câmera
    private int _version;
    private int _busy;

    public CameraFocus(IViscaInquirer client, Func<CameraConfig> cam) { _client = client; _cam = cam; }

    public FocusMode Mode => (FocusMode)Volatile.Read(ref _mode);
    /// <summary>Verdadeiro se o modo mostrado foi lido da câmera (e não apenas presumido pelo último comando enviado).</summary>
    public bool Confirmed => Volatile.Read(ref _confirmed) == 1;
    /// <summary>Muda a cada atualização: a tela usa para saber quando redesenhar.</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Avisos curtos para o operador (podem chegar de qualquer thread).</summary>
    public event Action<string>? Notice;

    private void Set(FocusMode m, bool confirmed)
    {
        Volatile.Write(ref _mode, (int)m);
        Volatile.Write(ref _confirmed, confirmed ? 1 : 0);
        Interlocked.Increment(ref _version);
    }

    private void Say(string text) { try { Notice?.Invoke(text); } catch { } }

    /// <summary>Esquece o que foi lido (ao trocar de câmera).</summary>
    public void Reset() => Set(FocusMode.Desconhecido, false);

    /// <summary>Pergunta à câmera em que modo de foco ela está. Devolve falso se ela não respondeu à consulta.</summary>
    public async Task<bool> RefreshAsync()
    {
        try
        {
            var cam = _cam();
            var r = await _client.InquireAsync(ViscaCommands.FocusModeInquiry(cam.ViscaAddress), Math.Max(800, cam.TimeoutMs), ViscaCommands.IsOnOffReply).ConfigureAwait(false);
            if (r != null && r.Kind == ViscaResponseKind.Inquerito && ViscaCommands.TryReadOnOff(r.Payload, out bool on))
            {
                Set(on ? FocusMode.Automatico : FocusMode.Manual, true);
                return true;
            }
        }
        catch { }
        Volatile.Write(ref _confirmed, 0);
        Interlocked.Increment(ref _version);
        return false;
    }

    /// <summary>Liga o autofoco se estiver desligado e vice-versa, de acordo com o estado REAL da câmera. Devolve o modo resultante.</summary>
    public async Task<FocusMode> ToggleAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Mode;          // clique repetido enquanto a troca ainda acontece
        try
        {
            await RefreshAsync().ConfigureAwait(false);                     // sempre confere antes: o controle remoto da câmera pode ter mudado o modo
            bool turnOn = Mode != FocusMode.Automatico;
            SetAuto(turnOn);
            return Mode;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>Liga ou desliga o autofoco e, logo depois, lê o modo da câmera para conferir se ela aceitou.</summary>
    public void SetAuto(bool on)
    {
        var cam = _cam();
        _client.Send(ViscaCommands.AutoFocus(cam.ViscaAddress, on));
        Set(on ? FocusMode.Automatico : FocusMode.Manual, false);
        _ = ConfirmLaterAsync(on ? FocusMode.Automatico : FocusMode.Manual);
    }

    /// <summary>Foca uma vez e mantém (One Push AF). Com o autofoco ligado, a câmera passa antes para o modo manual.</summary>
    public async Task OnePushAsync()
    {
        var cam = _cam();
        if (Mode != FocusMode.Manual)
        {
            _client.Send(ViscaCommands.AutoFocus(cam.ViscaAddress, false));
            Set(FocusMode.Manual, false);
            await Task.Delay(80).ConfigureAwait(false);                      // o foco de um toque só vale no modo manual
        }
        _client.Send(ViscaCommands.FocusOnePush(cam.ViscaAddress));
        Say("Foco de um toque enviado. O autofoco contínuo ficou desligado (foco manual).");
        _ = ConfirmLaterAsync(FocusMode.Manual);
    }

    private async Task ConfirmLaterAsync(FocusMode expected)
    {
        try
        {
            await Task.Delay(600).ConfigureAwait(false);
            if (await RefreshAsync().ConfigureAwait(false) && Mode != expected)
                Say("A câmera não aceitou a troca do foco automático (continua em modo " + (Mode == FocusMode.Automatico ? "automático" : "manual") + ").");
        }
        catch { }
    }
}
