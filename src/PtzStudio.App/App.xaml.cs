using System.IO;
using System.Windows;
using PtzStudio.App.Engine;
using PtzStudio.Core.Storage;

namespace PtzStudio.App;

public partial class App : Application
{
    private static Mutex? _single;
    private EventWaitHandle? _showSignal;

    /// <summary>
    /// Só uma cópia do programa pode controlar a câmera. Se o usuário abrir outra (por exemplo, com a primeira escondida
    /// ao lado do relógio), a nova apenas pede à primeira para aparecer e fecha.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Global\PTZStudio.SingleInstance", out bool first);
        if (!first)
        {
            // Cópia de OUTRA sessão do Windows (acesso remoto, troca rápida de usuário): o sinal não chega até lá. Avisa em vez de sumir.
            bool signalled = false;
            try { EventWaitHandle.OpenExisting(@"Global\PTZStudio.Show").Set(); signalled = true; } catch { }
            if (!signalled)
                MessageBox.Show("O PTZ Studio já está aberto em outra sessão do Windows (outro usuário ou acesso remoto). Só uma cópia pode controlar a câmera. Feche-o lá antes de abrir aqui.",
                    "PTZ Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\PTZStudio.Show");
        var waiter = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as PtzStudio.App.MainWindow)?.ShowFromTray()));
        }) { IsBackground = true, Name = "PTZStudio-Show" };
        waiter.Start();
        Ui.ProcessTuning.Apply();
        base.OnStartup(e);
    }

    /// <summary>Só para falhas inesperadas (não há registro contínuo de uso). O arquivo é pequeno e limitado a 200 KB.</summary>
    private static void LogError(string text)
    {
        try
        {
            var dir = Path.Combine(SettingsStore.DefaultDirectory(), "logs");
            var file = Path.Combine(dir, "erros.log");
            Directory.CreateDirectory(dir);
            if (!File.Exists(file) || new FileInfo(file).Length < 200_000)
                File.AppendAllText(file, $"{DateTime.Now:s} {text}\n\n");
        }
        catch { }
    }

    public App()
    {
        // Erro fatal em qualquer thread, ou saída do processo: a câmera não pode ficar andando.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashGuard.Trip();
            LogError("Falha fatal: " + e.ExceptionObject);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashGuard.Trip();
        // Falha em tarefa de fundo que ninguém aguardou: registra e segue (não derruba o programa).
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError("Falha em tarefa de fundo: " + e.Exception);
            AppErrors.Report("Falha em uma tarefa de fundo (o programa continua funcionando): " + (e.Exception.InnerException?.Message ?? e.Exception.Message));
            e.SetObserved();
        };

        DispatcherUnhandledException += (_, e) =>
        {
            LogError(e.Exception.ToString());
            // Nada de caixa de diálogo: ela exigiria um clique no meio da transmissão. A faixa de alerta avisa sem bloquear.
            AppErrors.Report("Erro inesperado (o programa continua funcionando): " + e.Exception.Message);
            e.Handled = true;
        };
    }
}
