namespace PtzStudio.App.Engine;

/// <summary>
/// Ponto único para mandar a câmera parar quando o programa encerra de forma inesperada (exceção fatal, saída do processo).
/// O motor registra a ação ao iniciar. Não cobre falha nativa que mata o processo sem aviso.
/// </summary>
public static class CrashGuard
{
    public static Action? StopAction { get; set; }

    public static void Trip()
    {
        try { StopAction?.Invoke(); } catch { }
    }
}
