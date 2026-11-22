namespace PtzStudio.Core.Input;

/// <summary>Acesso às portas XInput do Windows (0 a 3). Separado para os testes não dependerem do Windows.</summary>
public interface IXInputPorts
{
    /// <summary>Verdadeiro se há um controle XInput conectado nesta porta.</summary>
    bool IsConnected(int port);
    /// <summary>Envia vibração com intensidade ZERO nos dois motores. Verdadeiro se o Windows aceitou.</summary>
    bool SendZeroVibration(int port);
}

/// <summary>
/// Tentativa de manter o controle acordado: a cada 3 minutos envia, a cada controle XInput conectado, um comando de vibração
/// com intensidade zero (o motor não gira). Não lê nem altera Pan/Tilt/Zoom, não depende da câmera e não esconde um controle
/// desconectado: a leitura do controle (DirectInput) e a detecção de desconexão continuam exatamente como estão.
/// Funciona só com o controle em modo XInput (luz verde no GameSir), por cabo ou receptor. Se o firmware do controle não
/// contar esse comando como atividade, nada muda (ele desliga como antes).
/// </summary>
public sealed class ControllerKeepAwake
{
    public const int Ports = 4;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private readonly IXInputPorts _ports;
    private DateTime? _next;

    public ControllerKeepAwake(IXInputPorts ports) { _ports = ports; }

    /// <summary>Quando o último sinal foi enviado (null = nunca).</summary>
    public DateTime? LastSent { get; private set; }
    /// <summary>Quantos controles XInput receberam o último sinal.</summary>
    public int LastCount { get; private set; }
    /// <summary>Quantos sinais foram enviados desde a abertura (para conferir no Diagnóstico).</summary>
    public int Rounds { get; private set; }

    /// <summary>Verdadeiro se, com a opção ligada, já está na hora do próximo sinal (consulta barata, sem acessar o Windows).</summary>
    public bool IsDue(DateTime now, bool enabled) => enabled && (_next is not DateTime n || now >= n);

    /// <summary>Recomeça a contagem (o próximo sinal sai na próxima chamada de <see cref="Tick"/>).</summary>
    public void Reset() => _next = null;

    /// <summary>
    /// Chamado periodicamente. Envia o sinal na primeira chamada e depois a cada 3 minutos; com a opção desligada não envia nada.
    /// Devolve verdadeiro quando enviou nesta chamada.
    /// </summary>
    public bool Tick(DateTime now, bool enabled)
    {
        if (!enabled) { _next = null; return false; }
        if (_next is DateTime n && now < n) return false;
        _next = now + Interval;
        int count = 0;
        for (int i = 0; i < Ports; i++)
        {
            try { if (_ports.IsConnected(i) && _ports.SendZeroVibration(i)) count++; }
            catch { }
        }
        LastSent = now; LastCount = count; Rounds++;
        return true;
    }
}
