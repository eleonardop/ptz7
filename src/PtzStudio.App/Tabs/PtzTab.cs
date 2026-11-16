using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PtzStudio.App.Engine;
using PtzStudio.App.Live;
using PtzStudio.App.Ui;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;

namespace PtzStudio.App.Tabs;

/// <summary>Controle PTZ: vídeo ao vivo, joystick virtual, velocidade Pan/Tilt, zoom, imagem (brilho/exposição), entradas e posição inicial.</summary>
internal sealed class PtzTab : TabBase
{
    private VirtualJoystick _joy = new();
    private VideoCard? _video;
    private readonly TextBlock _info = Ux.Text("", 14, false, Ux.Muted);
    private readonly TextBlock _sent = Ux.Text("", 14, true);
    private readonly TextBlock _ptEffect = Ux.Text("", 14, false, Ux.Muted);
    private readonly TextBlock _zoomEffect = Ux.Text("", 14, false, Ux.Muted);
    private readonly StackPanel _imageHost = new();
    private Button _afBtn = new();
    private readonly TextBlock _afHint = Ux.Text("", 14, false, Ux.Muted);
    private string _afShown = "\u0001";
    private readonly Dictionary<ImageParam, (Slider slider, TextBlock value)> _imgRows = new();
    private Window? _win;
    private string _imgSig = "";
    private bool _imgUpdating;

    public PtzTab(AppEngine e) : base(e)
    {
        Init();
        // Janela desativada (Alt+Tab, clique fora) ou aba fechada: o joystick e o zoom da tela voltam ao centro e a câmera para.
        Loaded += (_, _) => { _win = Window.GetWindow(this); if (_win != null) _win.Deactivated += OnDeactivated; };
        Unloaded += (_, _) => { if (_win != null) _win.Deactivated -= OnDeactivated; _win = null; ReleaseAll(); };
    }

    private void OnDeactivated(object? s, EventArgs a) => ReleaseAll();
    private void ReleaseAll() { _joy.Release(); E.VirtualX = E.VirtualY = E.VirtualZ = 0; E.Controller.SetVirtualFocus(0); }

    /// <summary>Botão que repete enquanto está apertado (primeiro passo na hora, depois em intervalos curtos).</summary>
    private static void Repeat(Button b, Action step)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        void Stop() { t.Stop(); }
        t.Tick += (_, _) => { t.Interval = TimeSpan.FromMilliseconds(140); step(); };
        b.PreviewMouseLeftButtonDown += (_, _) => { b.CaptureMouse(); step(); t.Interval = TimeSpan.FromMilliseconds(450); t.Start(); };
        b.PreviewMouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); Stop(); };
        b.LostMouseCapture += (_, _) => Stop();
        b.Unloaded += (_, _) => Stop();
    }

    private static Button Hold(string glyph, string text, Action down, Action up)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = glyph, FontSize = 30, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, MinHeight = 76, MinWidth = 220, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
        b.PreviewMouseLeftButtonDown += (_, _) => { b.CaptureMouse(); down(); };
        b.PreviewMouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); up(); };
        b.LostMouseCapture += (_, _) => up();
        return b;
    }

    /// <summary>Botão que só executa depois de ficar apertado por ms milissegundos (evita toque acidental em ações de efeito visível).</summary>
    private static Button HoldToRun(string text, string holdingText, Action run, int ms = 500)
    {
        var b = new Button { Content = text, MinHeight = 56, MinWidth = 220, Margin = new Thickness(4), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Stretch };
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        void Cancel() { t.Stop(); b.Content = text; }
        t.Tick += (_, _) => { t.Stop(); b.Content = text; run(); };
        b.PreviewMouseLeftButtonDown += (_, _) => { b.CaptureMouse(); b.Content = holdingText; t.Start(); };
        b.PreviewMouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); Cancel(); };
        b.LostMouseCapture += (_, _) => Cancel();
        b.Unloaded += (_, _) => Cancel();
        return b;
    }

    protected override void Build()
    {
        E.VirtualX = E.VirtualY = E.VirtualZ = 0;     // nada pode ficar "apertado" ao reconstruir a tela
        E.Controller.SetVirtualFocus(0);
        _imgRows.Clear(); _imgSig = ""; _afShown = "\u0001";
        var m = E.Profile.Motion;

        // ---------- Pan / Tilt: joystick virtual (volta sozinho ao centro ao soltar) ----------
        _joy = new VirtualJoystick { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
        _joy.ValueChanged += (x, y) =>
        {
            // ato deliberado do operador: o joystick virtual retoma o comando, mesmo que o controle físico esteja travado
            if ((x != 0 || y != 0) && E.VirtualX == 0 && E.VirtualY == 0) E.Controller.RequestVirtualControl();
            E.VirtualX = x; E.VirtualY = y;
        };
        var panTilt = Ux.Card(Ux.V(
            _joy,
            Ux.Text("Arraste para mover; ao soltar, o joystick volta ao centro e a câmera para. Usa a curva e a zona morta do perfil ativo.", 12, false, Ux.Muted)),
            "PAN / TILT  ·  JOYSTICK VIRTUAL");

        // ---------- Velocidade Pan/Tilt (por perfil) ----------
        var speed = Ux.Card(Ux.V(
            Ux.SliderRow("Velocidade Pan/Tilt", 1, 100, m.PanTiltSpeedPercent, v =>
            {
                m.PanTiltSpeedPercent = (int)Math.Round(v); E.Save(); UpdateEffects();
            }, "0\\%", true),
            _ptEffect,
            _sent),
            $"VELOCIDADE · PERFIL {E.Settings.ActiveProfile.ToUpperInvariant()}");

        // ---------- Zoom (independente da velocidade Pan/Tilt) ----------
        var zoomIn = Hold("+", "Aproximar", () => { E.Controller.RequestVirtualControl(); E.VirtualZ = 1; }, () => E.VirtualZ = 0);
        var zoomOut = Hold("−", "Afastar", () => { E.Controller.RequestVirtualControl(); E.VirtualZ = -1; }, () => E.VirtualZ = 0);
        var zoom = Ux.Card(Ux.V(
            zoomIn, zoomOut,
            Ux.SliderRow("Intensidade do zoom", 1, 100, m.VirtualZoomPercent, v =>
            {
                m.VirtualZoomPercent = (int)Math.Round(v); E.Save(); UpdateEffects();
            }, "0\\%", true),
            _zoomEffect,
            Ux.Text("Segure o botão: ao soltar, o zoom para na hora. Não depende da velocidade Pan/Tilt.", 12, false, Ux.Muted)),
            "ZOOM");

        // ---------- Foco: autofoco (estado lido da câmera), foco de um toque e foco manual ----------
        _afBtn = new Button { MinHeight = 64, MinWidth = 220, Margin = new Thickness(0, 0, 0, 8), FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch };
        _afBtn.Click += (_, _) => { _ = E.ToggleAutoFocusAsync(); };
        var onePush = new Button { Content = "Focar agora (1 toque)", MinHeight = 52, MinWidth = 220, Margin = new Thickness(0, 0, 0, 8), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Stretch,
                                   ToolTip = "A câmera foca uma vez e mantém o foco. O autofoco contínuo é desligado (foco manual)." };
        onePush.Click += (_, _) => { _ = E.Focus.OnePushAsync(); };
        var focusNear = Hold("◀", "Foco perto", () => E.Controller.SetVirtualFocus(-1), () => E.Controller.SetVirtualFocus(0));
        var focusFar = Hold("▶", "Foco longe", () => E.Controller.SetVirtualFocus(1), () => E.Controller.SetVirtualFocus(0));
        focusNear.MinHeight = 52; focusFar.MinHeight = 52;
        var focus = Ux.Card(Ux.V(_afBtn, onePush, focusNear, focusFar, _afHint), "FOCO");

        // ---------- Imagem: brilho e exposição (só o que a câmera confirmar) ----------
        BuildImageCard();
        var image = Ux.Card(_imageHost, "IMAGEM DA CÂMERA");

        // ---------- Entradas: o controle físico e o joystick virtual (mouse) funcionam ao mesmo tempo ----------
        var source = Ux.Card(Ux.V(
            Ux.Text("O controle físico (GameSir) e o joystick virtual (mouse) funcionam ao mesmo tempo; não há mais botão para alternar.", 12, false, Ux.Muted),
            Ux.Text("Se os dois forem usados juntos, quem saiu do centro primeiro mantém o comando até voltar ao centro. As duas entradas nunca se somam.", 12, false, Ux.Muted),
            _info), "ENTRADAS");

        var leftCol = new StackPanel();
        leftCol.Children.Add(panTilt); leftCol.Children.Add(speed);
        var midCol = new StackPanel();
        midCol.Children.Add(zoom); midCol.Children.Add(focus); midCol.Children.Add(image);

        // ---------- Posição inicial (Home): segurar meio segundo, para não mandar a câmera ao Home por engano.
        // Os presets rápidos ficam na barra fixa no topo da janela (sempre visíveis, sem rolar). ----------
        var presets = Ux.Card(Ux.V(
            HoldToRun("Posição inicial (Home)", "Segure para ir ao Home…", () => { E.Controller.Home(); }),
            Ux.Text("Segure o botão por meio segundo. Os presets ficam na barra do topo da janela.", 12, false, Ux.Muted)), "POSIÇÃO INICIAL");

        // ---------- Vídeo ao vivo (topo da coluna direita; o resto da tela não muda) ----------
        _video = new VideoCard(E);
        var videoCard = Ux.Card(_video.Build(), "VÍDEO AO VIVO");

        var rightCol = new StackPanel();
        rightCol.Children.Add(videoCard); rightCol.Children.Add(presets); rightCol.Children.Add(source);

        // Três colunas de largura fixa dentro de um WrapPanel: em janelas estreitas a última coluna desce para a linha de baixo.
        leftCol.Width = 340; midCol.Width = 290; rightCol.Width = 360;
        var g = new WrapPanel { Margin = new Thickness(22, 18, 10, 6) };
        g.Children.Add(leftCol); g.Children.Add(midCol); g.Children.Add(rightCol);

        var page = new StackPanel();
        page.Children.Add(Ux.Header("Controle PTZ", "Veja a câmera e opere-a com o joystick virtual, o zoom e os presets."));
        page.Children.Add(g);
        Content = Ux.Scroll(page);
        UpdateEffects();
        if (E.Image.Detected) E.Image.Refresh();      // lê os valores atuais da câmera ao abrir a tela
    }

    // ---------------- imagem ----------------
    private string ImageSignature()
    {
        var i = E.Image;
        return $"{i.Detecting}|{i.Detected}|{i.Brilho.Support}|{i.Exposicao.Support}|{i.Brilho.Note}|{i.Exposicao.Note}|{E.Client.State}";
    }

    private void BuildImageCard()
    {
        _imageHost.Children.Clear(); _imgRows.Clear();
        _imgSig = ImageSignature();
        var img = E.Image;

        if (img.Detecting) { _imageHost.Children.Add(Ux.Text("Consultando a câmera…", 13, false, Ux.Warn)); return; }

        bool any = false;
        foreach (var (param, title) in new[] { (ImageParam.Brilho, "Brilho"), (ImageParam.Exposicao, "Exposição") })
        {
            var st = img.Get(param);
            if (st.Support != ImageSupport.Suportado) continue;
            any = true;
            var val = Ux.Text("", 14, true); val.HorizontalAlignment = HorizontalAlignment.Right; val.Margin = new Thickness(0);
            var head = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = Ux.Text(title, 14); lbl.Margin = new Thickness(0);
            Grid.SetColumn(val, 1);
            head.Children.Add(lbl); head.Children.Add(val);

            var slider = new Slider { Minimum = st.Min, Maximum = Math.Max(st.Max, st.Min + 1), Value = Math.Clamp(st.Value, st.Min, Math.Max(st.Max, st.Min + 1)), Height = 28, VerticalAlignment = VerticalAlignment.Center };
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            debounce.Tick += (_, _) => { debounce.Stop(); E.Image.SetValue(param, (int)Math.Round(slider.Value)); };
            slider.ValueChanged += (_, _) => { if (_imgUpdating) return; debounce.Stop(); debounce.Start(); };

            var minus = new Button { Content = "−", Width = 40, MinHeight = 36, FontSize = 18, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0) };
            var plus = new Button { Content = "+", Width = 40, MinHeight = 36, FontSize = 18, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
            Repeat(minus, () => E.Image.Step(param, -1));
            Repeat(plus, () => E.Image.Step(param, +1));
            var reset = new Button { Content = "↺", Width = 36, MinHeight = 36, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Voltar ao valor de quando a câmera foi detectada" };
            reset.Click += (_, _) => E.Image.RestoreInitial(param);

            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
            DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(reset, Dock.Right); DockPanel.SetDock(plus, Dock.Right);
            row.Children.Add(minus); row.Children.Add(reset); row.Children.Add(plus); row.Children.Add(slider);
            _imageHost.Children.Add(head); _imageHost.Children.Add(row);
            _imgRows[param] = (slider, val);
        }

        if (!any)
        {
            string msg = img.Detected
                ? "A câmera não confirmou ajuste de brilho nem de exposição por este protocolo. Nada foi enviado."
                : E.Client.State == ConnectionState.Conectado ? "Ajustes de imagem ainda não detectados." : "Conecte a câmera para detectar os ajustes de imagem.";
            _imageHost.Children.Add(Ux.Text(msg, 13, false, Ux.Muted));
            foreach (var n in new[] { img.Brilho.Note, img.Exposicao.Note }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                _imageHost.Children.Add(Ux.Text(n, 12, false, Ux.Muted));
        }
        else _imageHost.Children.Add(Ux.Text("Os botões do controle (aba Mapeamento) ajustam os mesmos valores.", 12, false, Ux.Muted));

        var detect = Ux.Btn("Detectar ajustes de imagem", () => { _ = E.Image.DetectAsync(); }, 200);
        detect.Margin = new Thickness(0, 4, 0, 0);
        _imageHost.Children.Add(detect);
    }

    private void SyncImageValues()
    {
        _imgUpdating = true;
        try
        {
            foreach (var (param, row) in _imgRows)
            {
                var st = E.Image.Get(param);
                if (!row.slider.IsMouseCaptureWithin)
                {
                    row.slider.Maximum = Math.Max(st.Max, st.Min + 1);
                    row.slider.Value = Math.Clamp(st.Value, st.Min, row.slider.Maximum);
                }
                row.value.Text = param == ImageParam.Exposicao
                    ? (st.Value - 7 == 0 ? "0 EV" : $"{st.Value - 7:+0;-0} passos")      // 7 = sem compensação
                    : st.Value.ToString();
            }
        }
        finally { _imgUpdating = false; }
    }

    // ---------------- textos que mostram o efeito real dos percentuais ----------------
    private void UpdateEffects()
    {
        var m = E.Profile.Motion; var cam = E.Camera;
        int Eff(int axisMax, int pct, int camMax) => Math.Clamp((int)Math.Round(axisMax * pct / 100.0), 1, camMax);
        int pan = Eff(m.Pan.MaxSpeed, m.PanTiltSpeedPercent, cam.MaxPanSpeed);
        int tilt = Eff(m.Tilt.MaxSpeed, m.PanTiltSpeedPercent, cam.MaxTiltSpeed);
        int zoom = Eff(m.Zoom.MaxSpeed, m.VirtualZoomPercent, cam.MaxZoomSpeed);
        _ptEffect.Text = $"No máximo do analógico: Pan {pan} de {cam.MaxPanSpeed} · Tilt {tilt} de {cam.MaxTiltSpeed} (velocidade VISCA). Cada perfil guarda a sua.";
        _zoomEffect.Text = $"Zoom da tela: nível {zoom} de {cam.MaxZoomSpeed} (a câmera tem só {cam.MaxZoomSpeed} níveis de velocidade de zoom).";
    }

    /// <summary>Mostra o estado REAL do foco (lido da câmera) no botão de autofoco.</summary>
    private void RefreshFocus()
    {
        var f = E.Focus;
        string sig = f.Mode + "|" + f.Confirmed;
        if (sig == _afShown) return;
        _afShown = sig;
        string st = f.Mode switch { FocusMode.Automatico => "LIGADO", FocusMode.Manual => "DESLIGADO", _ => "—" };
        _afBtn.Content = "AUTOFOCO: " + st + (f.Mode != FocusMode.Desconhecido && !f.Confirmed ? "  (não confirmado)" : "");
        if (f.Mode == FocusMode.Automatico)
        {
            _afBtn.Background = Ux.Accent; _afBtn.BorderBrush = Ux.Accent; _afBtn.Foreground = Brushes.White;
        }
        else
        {
            _afBtn.ClearValue(Control.BackgroundProperty); _afBtn.ClearValue(Control.BorderBrushProperty); _afBtn.ClearValue(Control.ForegroundProperty);
        }
        _afHint.Text = f.Mode switch
        {
            FocusMode.Automatico => "A câmera está focando sozinha. Foco perto/longe só funcionam com o autofoco DESLIGADO.",
            FocusMode.Manual => "Foco manual. Use Foco perto/longe, ou \"Focar agora\" para focar uma vez.",
            _ => "Estado do foco ainda não lido da câmera. O botão consulta a câmera antes de trocar."
        };
    }

    protected override void Refresh()
    {
        var c = E.Controller;
        _sent.Text = $"Enviado agora →  Pan {c.SentPan:+0;-0;0}    Tilt {c.SentTilt:+0;-0;0}    Zoom {c.SentZoom:+0;-0;0}";
        string who = c.InControl switch
        {
            InputSource.Fisico => "No comando agora: controle físico.",
            InputSource.Virtual => "No comando agora: joystick virtual (mouse).",
            _ => "No comando agora: ninguém (câmera parada)."
        };
        _info.Text = (c.SafetyLatched ? "Parada de segurança: leve os controles ao neutro para continuar.\n" : "") +
                     (c.WaitingNeutralAfterPreset ? "Indo para " + (c.Traveling ?? "a posição") + ": o analógico fica ignorado até voltar ao centro.\n" : "") + who;
        RefreshFocus();
        if (ImageSignature() != _imgSig) BuildImageCard();
        SyncImageValues();
        _video?.Refresh();
    }
}
