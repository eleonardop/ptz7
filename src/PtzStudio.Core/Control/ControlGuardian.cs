namespace PtzStudio.Core.Control;

/// <summary>
/// Guardião de parada: uma thread própria (independente da thread da tela) que vigia o ciclo de controle. Se o ciclo travar
/// com a câmera em movimento (a tela congelou, o disco demorou, um diálogo do sistema...), ele manda parar sozinho.
/// Não protege contra a queda do processo inteiro: para isso existe a parada no encerramento inesperado do programa.
/// </summary>
public sealed class ControlGuardian : IDisposable
{
    private readonly PtzController _controller;
    private readonly int _limitMs;
    private readonly Thread _thread;
    private volatile bool _stop;

    public ControlGuardian(PtzController controller, int limitMs = 500)
    {
        _controller = controller;
        _limitMs = Math.Max(100, limitMs);
        _thread = new Thread(Loop) { IsBackground = true, Name = "PTZStudio-Guardian", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(100);
            try { _controller.GuardianCheck(_limitMs); }
            catch { /* o guardião nunca pode cair */ }
        }
    }

    public void Dispose() => _stop = true;
}
