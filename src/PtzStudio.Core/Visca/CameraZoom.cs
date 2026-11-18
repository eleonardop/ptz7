using PtzStudio.Core.Models;

namespace PtzStudio.Core.Visca;

/// <summary>
/// Posição do zoom LIDA DA CÂMERA (consulta VISCA 04 47), usada para reduzir a velocidade de Pan/Tilt com o zoom fechado.
/// Nada é presumido: sem resposta da câmera, a posição fica "desconhecida" e a redução não é aplicada.
/// </summary>
public sealed class CameraZoom
{
    /// <summary>Posição de zoom óptico máximo mais comum no VISCA (câmeras 20x/30x). Se a câmera informar valores maiores (zoom digital), o maior visto passa a valer.</summary>
    public const int DefaultTeleMax = 0x4000;

    private readonly IViscaInquirer _client;
    private readonly Func<CameraConfig> _cam;
    private int _pos = -1;
    private int _max = DefaultTeleMax;
    private int _fails;

    public CameraZoom(IViscaInquirer client, Func<CameraConfig> cam) { _client = client; _cam = cam; }

    /// <summary>Última posição informada pela câmera, ou -1 se desconhecida.</summary>
    public int Position => Volatile.Read(ref _pos);
    /// <summary>Consultas seguidas sem resposta válida.</summary>
    public int Failures => Volatile.Read(ref _fails);

    /// <summary>0 = zoom todo aberto, 1 = zoom máximo; -1 = posição desconhecida.</summary>
    public double Fraction
    {
        get
        {
            int p = Position;
            return p < 0 ? -1 : Math.Clamp(p / (double)Volatile.Read(ref _max), 0, 1);
        }
    }

    /// <summary>Esquece a posição (troca de câmera, conexão perdida).</summary>
    public void Reset() { Volatile.Write(ref _pos, -1); Volatile.Write(ref _max, DefaultTeleMax); Volatile.Write(ref _fails, 0); }

    /// <summary>Registra uma posição informada pela câmera.</summary>
    public void Accept(int position)
    {
        if (position < 0) return;
        if (position > Volatile.Read(ref _max)) Volatile.Write(ref _max, position);
        Volatile.Write(ref _pos, position);
        Volatile.Write(ref _fails, 0);
    }

    /// <summary>Pergunta à câmera a posição do zoom. Depois de 3 falhas seguidas a posição passa a "desconhecida".</summary>
    public async Task<bool> RefreshAsync(int timeoutMs = 500)
    {
        try
        {
            var cam = _cam();
            var r = await _client.InquireAsync(ViscaCommands.ZoomPositionInquiry(cam.ViscaAddress), timeoutMs, ViscaCommands.IsValueReply).ConfigureAwait(false);
            if (r != null && r.Kind == ViscaResponseKind.Inquerito && ViscaCommands.TryReadZoomPosition(r.Payload, out int pos))
            {
                Accept(pos);
                return true;
            }
        }
        catch { }
        if (Interlocked.Increment(ref _fails) >= 3) Volatile.Write(ref _pos, -1);
        return false;
    }
}
