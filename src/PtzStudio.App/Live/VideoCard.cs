using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PtzStudio.App.Engine;
using PtzStudio.App.Ui;
using PtzStudio.Core.Onvif;
using PtzStudio.Core.Video;

namespace PtzStudio.App.Live;

/// <summary>Conteúdo do cartão "Vídeo ao vivo" da aba Controle PTZ: imagem, botões e ajustes do endereço RTSP.</summary>
internal sealed class VideoCard
{
    private readonly AppEngine _e;
    private readonly TextBlock _overlay = Ux.Text("", 13, false, Ux.Muted);
    private readonly Border _overlayBox = new() { Background = Brushes.Black };
    private readonly TextBlock _msg = Ux.Text("", 14, false, Ux.Muted);
    private readonly StackPanel _settings = new() { Visibility = Visibility.Collapsed };
    private Button _toggle = new();
    private Border _stage = new();
    private TextBox _tbMain = new(), _tbSub = new();
    private bool _busy;
    private double _maxStageHeight = double.PositiveInfinity;

    public VideoCard(AppEngine e) { _e = e; }

    /// <summary>Verdadeiro enquanto os campos de Ajustes (endereço RTSP etc.) estão abertos.</summary>
    public bool SettingsOpen => _settings.Visibility == Visibility.Visible;

    /// <summary>Altura atual da imagem (a aba usa para saber quanto sobra de tela).</summary>
    public double StageHeight => _stage.ActualHeight;

    /// <summary>Limite de altura da imagem, para o vídeo e os presets caberem na tela sem rolar. A imagem continua em 16:9.</summary>
    public void SetMaxStageHeight(double h)
    {
        h = Math.Max(150, h);
        if (Math.Abs(h - _maxStageHeight) < 1) return;
        _maxStageHeight = h;
        FitStage();
    }

    /// <summary>A imagem ocupa a largura do cartão em 16:9; se isso passar do limite de altura, fica mais baixa (com faixas pretas nos lados).</summary>
    private void FitStage()
    {
        double w = _stage.ActualWidth;
        if (w <= 0) return;
        double h = Math.Round(Math.Min(w * 9.0 / 16.0, _maxStageHeight));
        if (Math.Abs(_stage.Height - h) >= 1 || double.IsNaN(_stage.Height)) _stage.Height = h;

        // Imagem grande na tela: decodifica a prévia em tamanho maior, para não ficar borrada (troca com folga, para não reiniciar o vídeo a toda hora).
        double px = w * VisualTreeHelper.GetDpi(_stage).DpiScaleX;
        var v = LiveVideo.Instance;
        if (!v.PreviewLarge && px > 860) v.PreviewLarge = true;
        else if (v.PreviewLarge && px < 760) v.PreviewLarge = false;
    }

    public UIElement Build()
    {
        var cam = _e.Camera;

        // ---- imagem (proporção 16:9 para a largura do cartão) ----
        var grid = new Grid();
        grid.Children.Add(new VideoSurface());
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _overlay.TextAlignment = TextAlignment.Center;
        _overlay.Margin = new Thickness(12);
        _overlayBox.Child = _overlay;
        grid.Children.Add(_overlayBox);
        _stage = new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(8), Height = 200, ClipToBounds = true,
            Child = grid, Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Duplo clique no vídeo = tela cheia. Os comandos de Pan, Tilt e Zoom continuam funcionando normalmente."
        };
        _stage.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2) OpenFullscreen(); };
        _stage.SizeChanged += (_, ev) => { if (ev.WidthChanged) FitStage(); };

        // ---- botões ----
        _toggle = Ux.Btn("Ligar", Toggle, 80);
        var fs = Ux.Btn("Tela cheia", OpenFullscreen, 80);
        var cfg = Ux.Btn("Ajustes", () =>
            _settings.Visibility = _settings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, 80);
        cfg.ToolTip = "Endereço RTSP, usuário, senha e qualidade do vídeo";
        foreach (var b in new[] { _toggle, fs, cfg }) { b.MinHeight = 36; b.FontSize = 14; b.Margin = new Thickness(0, 2, 8, 2); }
        _settings.Children.Clear();     // a tela pode ser reconstruída: os campos são recriados

        // ---- ajustes ----
        void Label(string t) => _settings.Children.Add(Ux.Text(t, 12, false, Ux.Muted));

        Label("Endereço RTSP principal (maior qualidade)");
        _tbMain = Ux.Input(cam.RtspUrl ?? "", v => { cam.RtspUrl = (v ?? "").Trim(); _e.Save(); }, 300);
        _settings.Children.Add(_tbMain);

        Label("Endereço RTSP da prévia (secundário, opcional)");
        _tbSub = Ux.Input(cam.RtspSubUrl ?? "", v => { cam.RtspSubUrl = (v ?? "").Trim(); _e.Save(); }, 300);
        _settings.Children.Add(_tbSub);

        Label("Usuário");
        _settings.Children.Add(Ux.Input(cam.RtspUser ?? "", v => { cam.RtspUser = (v ?? "").Trim(); _e.Save(); }, 300));

        Label("Senha");
        var pw = new PasswordBox
        {
            Password = cam.RtspPassword ?? "", Width = 300, FontSize = 14, MinHeight = 38, Margin = new Thickness(4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)), Foreground = Ux.TextBrush,
            BorderBrush = Ux.BorderStrong, CaretBrush = Ux.TextBrush, HorizontalAlignment = HorizontalAlignment.Left
        };
        pw.LostFocus += (_, _) => { cam.RtspPassword = pw.Password; _e.Save(); };
        _settings.Children.Add(pw);

        Label("Qualidade da prévia (a tela cheia usa o principal, se houver)");
        _settings.Children.Add(Ux.Combo(new[] { "Prévia (stream secundário, mais leve)", "Principal (maior resolução)" },
            cam.VideoUsePreviewStream ? 0 : 1, i => { cam.VideoUsePreviewStream = i == 0; _e.Save(); }, 300));

        _settings.Children.Add(Ux.Check("Usar decodificação pela placa de vídeo (menos CPU, porém menos estável)", cam.VideoHardwareDecode,
            v => { cam.VideoHardwareDecode = v; _e.Save(); }));
        _settings.Children.Add(Ux.Check("Baixa latência (imagem mais rápida para enquadrar à mão; pode engasgar em rede ruim)", cam.VideoLowLatency,
            v => { cam.VideoLowLatency = v; _e.Save(); }));

        _settings.Children.Add(Ux.Wrap(
            Ux.Btn("Descobrir (ONVIF)", Discover, 140),
            Ux.Btn("Testar endereços comuns", ProbeCommon, 140)));
        _settings.Children.Add(Ux.Text(
            "A senha fica salva no arquivo de configurações (também em backups). O vídeo roda por RTSP, sem som, e o programa só o decodifica enquanto esta tela está visível.",
            12, false, Ux.Muted));

        return Ux.V(
            _stage,
            Ux.Wrap(_toggle, fs, cfg),
            _msg,
            _settings);
    }

    private void Toggle()
    {
        var cam = _e.Camera;
        cam.VideoEnabled = !cam.VideoEnabled;
        _e.Save();
        Refresh();
    }

    private void OpenFullscreen()
    {
        var cam = _e.Camera;
        if (!cam.VideoEnabled) { cam.VideoEnabled = true; _e.Save(); }
        VideoFullscreenWindow.Open(_e, Window.GetWindow(_stage));
    }

    /// <summary>Chamado pela aba a cada atualização (só com a aba visível).</summary>
    public void Refresh()
    {
        var cam = _e.Camera;
        var v = LiveVideo.Instance;
        v.Ensure(cam);
        _toggle.Content = cam.VideoEnabled ? "Desligar" : "Ligar";
        bool live = v.IsLive;
        _overlayBox.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _overlay.Text = cam.VideoEnabled ? v.StatusText : "Vídeo desligado.\nClique em Ligar para ver a câmera aqui.";
        _msg.Visibility = _msg.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- descoberta ----------------
    private async void Discover()
    {
        if (_busy) return;
        _busy = true;
        var cam = _e.Camera;
        _msg.Foreground = Ux.Warn;
        _msg.Text = "Consultando a câmera (ONVIF)… pode levar alguns segundos.";
        try
        {
            string ip = cam.IpAddress, user = cam.RtspUser ?? "", pass = cam.RtspPassword ?? "";
            var r = await Task.Run(() => OnvifFinder.FindAsync(ip, user, pass));
            if (!r.Ok) { _msg.Foreground = Ux.Danger; _msg.Text = r.Message; return; }

            var main = r.Main; var sub = r.Sub;
            if (main != null) { cam.RtspUrl = main.Uri; _tbMain.Text = main.Uri; }
            if (sub != null) { cam.RtspSubUrl = sub.Uri; _tbSub.Text = sub.Uri; }
            _e.Save();
            string Desc(OnvifStream s) => $"{s.Width}×{s.Height} {(s.Codec.Length > 0 ? s.Codec : "")}".Trim();
            _msg.Foreground = Ux.Ok;
            _msg.Text = "Encontrado via ONVIF. Principal: " + (main != null ? Desc(main) : "—") +
                        (sub != null ? ". Prévia: " + Desc(sub) + "." : ". A câmera não informou um stream secundário.");
        }
        catch (Exception ex) { _msg.Foreground = Ux.Danger; _msg.Text = "Não foi possível consultar a câmera: " + ex.Message; }
        finally { _busy = false; }
    }

    private async void ProbeCommon()
    {
        if (_busy) return;
        _busy = true;
        var cam = _e.Camera;
        _msg.Foreground = Ux.Warn;
        _msg.Text = "Testando endereços RTSP comuns… pode levar até meio minuto.";
        try
        {
            string ip = cam.IpAddress;
            int rtspPort = 554;                                        // usa a porta que o usuário já digitou no endereço RTSP, se houver
            if (Uri.TryCreate((cam.RtspUrl ?? "").Trim(), UriKind.Absolute, out var typed) && typed.Scheme == "rtsp" && typed.Port > 0) rtspPort = typed.Port;
            var list = await Task.Run(() => RtspProbe.ProbeAsync(ip, rtspPort));
            if (list.Count == 0)
            {
                _msg.Foreground = Ux.Danger;
                _msg.Text = $"Nenhum endereço comum respondeu na porta {rtspPort}. Confira o IP, veja o endereço RTSP no manual da câmera ou tente Descobrir (ONVIF).";
                return;
            }
            var open = list.FirstOrDefault(x => x.Status == 200);
            if (open != null || list.Count <= 3)
            {
                var pick = open ?? list[0];
                cam.RtspUrl = pick.Url; _tbMain.Text = pick.Url; _e.Save();
                _msg.Foreground = Ux.Ok;
                _msg.Text = "Respondeu: " + string.Join(", ", list.Select(x => x.Url)) + ". Usei o primeiro como endereço principal" +
                            (open == null ? " (a câmera pede usuário e senha)." : ".");
            }
            else
            {
                _msg.Foreground = Ux.Warn;
                _msg.Text = "A câmera pede usuário e senha antes de revelar os caminhos, então este teste não consegue confirmar nenhum. Informe usuário e senha e use Descobrir (ONVIF), ou digite o endereço do manual.";
            }
        }
        catch (Exception ex) { _msg.Foreground = Ux.Danger; _msg.Text = "Não foi possível testar: " + ex.Message; }
        finally { _busy = false; }
    }
}
