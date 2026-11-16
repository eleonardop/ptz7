namespace PtzStudio.App.Engine;

/// <summary>
/// Falhas inesperadas fora do ciclo de controle (tela, tarefas de fundo). Elas NUNCA abrem uma caixa de diálogo no meio da
/// transmissão: ficam guardadas aqui e a faixa de alerta da janela principal mostra um aviso que não bloqueia ninguém.
/// </summary>
public static class AppErrors
{
    private static readonly object L = new();
    private static string? _text;
    private static DateTime _at = DateTime.MinValue;

    /// <summary>Registra a falha mais recente (as repetidas em menos de 2 s não substituem o aviso).</summary>
    public static void Report(string text)
    {
        lock (L)
        {
            if (_text != null && (DateTime.UtcNow - _at).TotalSeconds < 2) return;
            _text = text; _at = DateTime.UtcNow;
        }
    }

    /// <summary>Texto do aviso pendente (nulo se não há).</summary>
    public static string? Pending { get { lock (L) return _text; } }

    /// <summary>O operador leu o aviso.</summary>
    public static void Dismiss() { lock (L) _text = null; }
}
