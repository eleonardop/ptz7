using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PtzStudio.App.Engine;

namespace PtzStudio.App.Live;

/// <summary>
/// Vídeo em tela cheia. Esc, F11 ou duplo clique fecham. O controle físico (GameSir) segue comandando a câmera;
/// o joystick virtual só existe na tela normal. Ao abrir, usa o fluxo principal (se houver) em imagem maior.
/// </summary>
internal sealed class VideoFullscreenWindow : Window
{
    private static VideoFullscreenWindow? _open;
    private readonly AppEngine _e;
    private readonly TextBlock _status;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _openedAt = DateTime.UtcNow;

    public static void Open(AppEngine e, Window? owner)
    {
        if (_open != null) { _open.Activate(); return; }
        var w = new VideoFullscreenWindow(e);
        if (owner != null) w.Owner = owner;      // fecha junto com a janela principal
        _open = w;
        w.Show();
        w.Activate();
        w.Focus();
    }

    private VideoFullscreenWindow(AppEngine e)
    {
        _e = e;
        Title = "PTZ Studio — Vídeo";
        Background = Brushes.Black;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        ShowInTaskbar = false;

        _status = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 20, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40)
        };
        _hint = new TextBlock
        {
            Text = "Esc, F11 ou duplo clique para sair da tela cheia",
            Foreground = Brushes.White, Opacity = 0.7, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18)
        };
        var grid = new Grid { Background = Brushes.Black };
        grid.Children.Add(new VideoSurface());
        grid.Children.Add(_status);
        grid.Children.Add(_hint);
        Content = grid;

        KeyDown += (_, k) => { if (k.Key == Key.Escape || k.Key == Key.F11) Close(); };
        MouseLeftButtonDown += (_, m) => { if (m.ClickCount == 2) Close(); };

        LiveVideo.Instance.FullscreenActive = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            LiveVideo.Instance.FullscreenActive = false;
            _open = null;
        };
    }

    private void Tick()
    {
        var v = LiveVideo.Instance;
        // Janela principal na bandeja ou minimizada: esta janela (filha dela) fica escondida e NÃO deve continuar pedindo vídeo
        // em tamanho grande. Sem pedidos, o vídeo para sozinho em ~2 s; ao voltar, a tela normal reassume o vídeo.
        bool shown = IsVisible && WindowState != WindowState.Minimized &&
                     (Owner == null || (Owner.IsVisible && Owner.WindowState != WindowState.Minimized));
        v.FullscreenActive = shown;
        if (!shown) return;
        v.Ensure(_e.Camera);
        _status.Text = v.IsLive ? "" : v.StatusText;
        _status.Visibility = v.IsLive ? Visibility.Collapsed : Visibility.Visible;
        if (_hint.Visibility == Visibility.Visible && (DateTime.UtcNow - _openedAt).TotalSeconds > 4) _hint.Visibility = Visibility.Collapsed;
    }
}
