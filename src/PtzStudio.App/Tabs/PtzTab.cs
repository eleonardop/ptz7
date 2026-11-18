using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PtzStudio.App.Engine;
using PtzStudio.App.Live;
using PtzStudio.App.Ui;
using PtzStudio.Core.Control;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;

namespace PtzStudio.App.Tabs;

/// <summary>
/// Controle PTZ. À esquerda, o vídeo ao vivo (grande) com os presets e o Home embaixo. À direita, a velocidade no topo,
/// o joystick virtual (arrastar ou segurar uma das 8 setas) e, ao lado dele, zoom, foco e brilho/exposição em botões compactos.
/// </summary>
internal sealed class PtzTab : TabBase
{
    private const double RightWidth = 590;          // coluna dos controles (joystick 300 + zoom/foco/imagem 290)
    private const double NarrowBelow = 980;         // abaixo desta largura de aba, os controles descem para baixo do vídeo

    private VirtualJoystick _joy = new();
    private VideoCard? _video;
    private readonly TextBlock _info = Ux.Text("", 12, false, Ux.Muted);
    private readonly TextBlock _sent = Ux.Text("", 12, true);
    private readonly TextBlock _ptEffect = Ux.Text("", 12, false, Ux.Muted);
    private readonly TextBlock _zoomEffect = Ux.Text("", 12, false, Ux.Muted);
    private readonly TextBlock _zoomStatus = Ux.Text("", 12, false, Ux.Muted);
    private string _zoomShown = "\u0001";
    private CheckBox _reduceCheck = new();
    private readonly StackPanel _imageHost = new();
    private Button _afBtn = new();
    private readonly TextBlock _afHint = Ux.Text("", 12, false, Ux.Muted);
    private string _afShown = "\u0001";
    private readonly Dictionary<ImageParam, (Slider slider, TextBlock value)> _imgRows = new();
    private Window? _win;
    private string _imgSig = "";
    private bool _imgUpdating;

    // layout
    private ScrollViewer _scroll = new();
    private FrameworkElement _header = new Border();
    private Grid _body = new();
    private StackPanel _leftCol = new();
    private Grid _rightCol = new();
    private bool? _narrow;

    // presets (dentro desta aba; a barra do topo da janela não existe mais)
    private WrapPanel _presetPanel = new();
    private TextBlock _presetEmpty = new();
    private Button _homeBtn = new();
    private string _presetSig = "\u0001";

    // brilho/exposição: aberto por padrão; recolhe sozinho se não couber na tela (o usuário pode abrir/fechar à mão)
    private Button _imgToggle = new();
    private bool _imgOpen = true, _imgUserSet;
    private double _imgBodyHeight = -1;

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

    /// <summary>Botão de segurar: age enquanto está apertado e para ao soltar (ou ao perder o mouse).</summary>
    private static Button Hold(string glyph, string text, Action down, Action up, double minHeight = 48, double glyphSize = 22, double textSize = 15)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = glyph, FontSize = glyphSize, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, glyphSize > 16 ? 10 : 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = textSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, MinHeight = minHeight, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        b.PreviewMouseLeftButtonDown += (_, _) => { b.CaptureMouse(); down(); };
        b.PreviewMouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); up(); };
        b.LostMouseCapture += (_, _) => up();
        return b;
    }

    /// <summary>Botão que só executa depois de ficar apertado por ms milissegundos (evita toque acidental em ações de efeito visível).</summary>
    private static Button HoldToRun(string text, string holdingText, Action run, int ms = 500)
    {
        var b = new Button { Content = text, MinHeight = 50, Margin = new Thickness(3), Padding = new Thickness(6, 3, 6, 3), FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch };
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        void Cancel() { t.Stop(); b.Content = text; }
        t.Tick += (_, _) => { t.Stop(); b.Content = text; run(); };
        b.PreviewMouseLeftButtonDown += (_, _) => { b.CaptureMouse(); b.Content = holdingText; t.Start(); };
        b.PreviewMouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); Cancel(); };
        b.LostMouseCapture += (_, _) => Cancel();
        b.Unloaded += (_, _) => Cancel();
        return b;
    }

    /// <summary>Cartão igual ao das outras abas (mesmas cores e cantos), só com margens menores: nesta tela cada linha de altura conta.</summary>
    private static Border Card(UIElement content, string? title = null)
    {
        var inner = new StackPanel();
        if (title != null)
            inner.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ux.AccentLight, Margin = new Thickness(0, 0, 0, 6) });
        inner.Children.Add(content);
        return new Border
        {
            Background = Ux.CardBg, BorderBrush = Ux.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), Child = inner
        };
    }

    /// <summary>Controle deslizante de 1% a 100% com o rótulo e o valor em cima e o efeito real (texto pequeno) embaixo.</summary>
    private static StackPanel SpeedRow(string label, double value, Action<double> set, TextBlock effect)
    {
        var l = new TextBlock { Text = label, FontSize = 14, Foreground = Ux.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value.ToString("0\\%"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ux.TextBrush, HorizontalAlignment = HorizontalAlignment.Right };
        var s = new Slider { Minimum = 1, Maximum = 100, Value = Math.Clamp(value, 1, 100), VerticalAlignment = VerticalAlignment.Center, Height = 26, Focusable = false };
        s.ValueChanged += (_, e) => { set(e.NewValue); val.Text = e.NewValue.ToString("0\\%"); };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(val, 1);
        top.Children.Add(l); top.Children.Add(val);
        effect.Margin = new Thickness(0);
        var sp = new StackPanel();
        sp.Children.Add(top); sp.Children.Add(s); sp.Children.Add(effect);
        return sp;
    }

    private static Grid Columns(params (UIElement el, double star)[] cells)
    {
        var g = new Grid();
        for (int i = 0; i < cells.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cells[i].star, GridUnitType.Star) });
            Grid.SetColumn(cells[i].el, i);
            g.Children.Add(cells[i].el);
        }
        return g;
    }

    protected override void Build()
    {
        E.VirtualX = E.VirtualY = E.VirtualZ = 0;     // nada pode ficar "apertado" ao reconstruir a tela
        E.Controller.SetVirtualFocus(0);
        _imgRows.Clear(); _imgSig = ""; _afShown = "\u0001"; _presetSig = "\u0001"; _narrow = null;
        var m = E.Profile.Motion;

        // ================= ESQUERDA: vídeo ao vivo (grande) e presets =================
        _video = new VideoCard(E);
        var videoCard = Card(_video.Build(), "VÍDEO AO VIVO");

        _presetPanel = new WrapPanel();
        _presetPanel.SizeChanged += (_, ev) => { if (ev.WidthChanged) FitPresets(); };
        _presetEmpty = Ux.Text("Nenhum preset gravado para esta câmera. Grave as posições na aba Presets; elas aparecem aqui.", 12, false, Ux.Muted);
        // Home: segurar meio segundo, para não mandar a câmera à posição inicial por engano.
        _homeBtn = HoldToRun("Home", "Segure…", () => { E.Controller.Home(); });
        _homeBtn.ToolTip = "Posição inicial (Home): segure o botão por meio segundo.";
        var presetsCard = Card(Ux.V(_presetEmpty, _presetPanel), "PRESETS");

        _leftCol = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        _leftCol.Children.Add(videoCard); _leftCol.Children.Add(presetsCard);

        // ================= DIREITA, topo: velocidade Pan/Tilt e intensidade do zoom (por perfil) =================
        var ptSpeed = SpeedRow("Velocidade Pan/Tilt", m.PanTiltSpeedPercent, v =>
        {
            m.PanTiltSpeedPercent = (int)Math.Round(v); E.Save(); UpdateEffects();
        }, _ptEffect);
        ptSpeed.Margin = new Thickness(0, 0, 10, 0);
        ptSpeed.ToolTip = "Vale para o controle físico, para o joystick virtual e para as setas. Cada perfil guarda a sua.";
        var zoomSpeed = SpeedRow("Intensidade do zoom", m.VirtualZoomPercent, v =>
        {
            m.VirtualZoomPercent = (int)Math.Round(v); E.Save(); UpdateEffects();
        }, _zoomEffect);
        zoomSpeed.Margin = new Thickness(10, 0, 0, 0);
        zoomSpeed.ToolTip = "Velocidade dos botões Aproximar/Afastar da tela. Não depende da velocidade Pan/Tilt. A câmera tem poucos níveis de velocidade de zoom.";
        // Redução de Pan/Tilt com o zoom (por perfil). O quanto reduz fica na aba Movimento.
        var reduce = _reduceCheck = new CheckBox
        {
            Content = "Reduzir Pan/Tilt com o zoom", IsChecked = m.ReducePanTiltWithZoom, FontSize = 13, Focusable = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 0),
            ToolTip = "Ligado: quanto mais fechado o zoom, mais lento o Pan/Tilt (a posição do zoom é lida da câmera). " +
                      "Com o zoom aberto a velocidade não muda. Vale para o controle, o joystick virtual e as setas. Cada perfil guarda a sua.\n" +
                      "O quanto reduz no zoom máximo se ajusta na aba Movimento."
        };
        reduce.Click += (_, _) => { m.ReducePanTiltWithZoom = reduce.IsChecked == true; E.Save(); _zoomShown = "\u0001"; };
        _zoomStatus.Margin = new Thickness(0, 4, 0, 0); _zoomStatus.VerticalAlignment = VerticalAlignment.Center;
        var reduceRow = new WrapPanel();
        reduceRow.Children.Add(reduce); reduceRow.Children.Add(_zoomStatus);
        var speed = Card(Ux.V(Columns((ptSpeed, 1), (zoomSpeed, 1)), reduceRow), $"VELOCIDADE · PERFIL {E.Settings.ActiveProfile.ToUpperInvariant()}");

        // ================= DIREITA: joystick virtual (arrastar o centro ou segurar uma das 8 setas) =================
        _joy = new VirtualJoystick { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        _joy.ValueChanged += (x, y) =>
        {
            // ato deliberado do operador: o joystick virtual retoma o comando, mesmo que o controle físico esteja travado
            if ((x != 0 || y != 0) && E.VirtualX == 0 && E.VirtualY == 0) E.Controller.RequestVirtualControl();
            E.VirtualX = x; E.VirtualY = y;
        };
        var joyHint = Ux.Text("Arraste o centro ou segure uma seta.", 12, false, Ux.Muted);
        joyHint.Margin = _sent.Margin = _info.Margin = new Thickness(0, 1, 0, 1);
        _afHint.Margin = new Thickness(0, 3, 0, 0);
        joyHint.ToolTip = "Ao soltar, a câmera para. Arrastar o botão azul move em qualquer direção, com velocidade proporcional ao deslocamento. Segurar uma seta move naquela direção na " +
                          "velocidade Pan/Tilt escolhida. Os dois usam a curva e a zona morta do perfil ativo.";
        _info.ToolTip = "O controle físico (GameSir) e o joystick virtual (mouse) funcionam ao mesmo tempo. Se os dois forem usados juntos, quem saiu do centro " +
                        "primeiro mantém o comando até voltar ao centro. As duas entradas nunca se somam.";
        var panTilt = Card(Ux.V(_joy, joyHint, _sent, _info), "PAN / TILT  ·  JOYSTICK VIRTUAL");

        // ================= DIREITA: zoom (independente da velocidade Pan/Tilt) =================
        var zoomIn = Hold("+", "Aproximar", () => { E.Controller.RequestVirtualControl(); E.VirtualZ = 1; }, () => E.VirtualZ = 0);
        var zoomOut = Hold("−", "Afastar", () => { E.Controller.RequestVirtualControl(); E.VirtualZ = -1; }, () => E.VirtualZ = 0);
        zoomOut.Margin = new Thickness(0);
        zoomIn.ToolTip = zoomOut.ToolTip = "Segure o botão: ao soltar, o zoom para na hora.";
        var zoom = Card(Ux.V(zoomIn, zoomOut), "ZOOM");

        // ================= DIREITA: foco (autofoco com o estado lido da câmera, foco de um toque e foco manual) =================
        _afBtn = new Button { Focusable = false, MinHeight = 34, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 3, 6), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch };
        _afBtn.Click += (_, _) => { _ = E.ToggleAutoFocusAsync(); };
        var onePush = new Button { Content = "Focar agora", Focusable = false, MinHeight = 34, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(3, 0, 0, 6), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch,
                                   ToolTip = "Foco de um toque: a câmera foca uma vez e mantém o foco. O autofoco contínuo é desligado (foco manual)." };
        onePush.Click += (_, _) => { _ = E.Focus.OnePushAsync(); };
        var focusNear = Hold("◀", "Perto", () => E.Controller.SetVirtualFocus(-1), () => E.Controller.SetVirtualFocus(0), 34, 11, 13);
        var focusFar = Hold("▶", "Longe", () => E.Controller.SetVirtualFocus(1), () => E.Controller.SetVirtualFocus(0), 34, 11, 13);
        focusNear.Margin = new Thickness(0, 0, 3, 2); focusFar.Margin = new Thickness(3, 0, 0, 2);
        focusNear.ToolTip = "Foco perto: segure para ajustar (só com o autofoco desligado)";
        focusFar.ToolTip = "Foco longe: segure para ajustar (só com o autofoco desligado)";
        var focus = Card(Ux.V(
            Columns((_afBtn, 1.7), (onePush, 1)),
            Columns((focusNear, 1), (focusFar, 1)),
            _afHint), "FOCO");

        // ================= DIREITA: brilho e exposição (só o que a câmera confirmar) =================
        BuildImageCard();
        var imgTitle = new TextBlock { Text = "BRILHO E EXPOSIÇÃO", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ux.AccentLight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = System.Windows.Input.Cursors.Hand };
        _imgToggle = new Button { Width = 30, MinHeight = 26, FontSize = 10, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        _imgToggle.Click += (_, _) => { _imgUserSet = true; SetImageOpen(!_imgOpen); };
        imgTitle.MouseLeftButtonDown += (_, _) => { _imgUserSet = true; SetImageOpen(!_imgOpen); };
        var detect = new Button { Content = "Detectar", MinHeight = 26, FontSize = 12, Padding = new Thickness(8, 1, 8, 1), ToolTip = "Perguntar à câmera quais ajustes de imagem ela aceita" };
        detect.Click += (_, _) => { _ = E.Image.DetectAsync(); };
        var imgHead = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_imgToggle, Dock.Right); DockPanel.SetDock(detect, Dock.Right);
        imgHead.Children.Add(_imgToggle); imgHead.Children.Add(detect); imgHead.Children.Add(imgTitle);
        _imageHost.Margin = new Thickness(0, 8, 0, 0);
        var image = Card(Ux.V(imgHead, _imageHost));
        SetImageOpen(_imgOpen);

        var joyCol = new StackPanel { Width = 300, VerticalAlignment = VerticalAlignment.Top };
        joyCol.Children.Add(panTilt);
        var zoomCol = new StackPanel { Width = RightWidth - 300, VerticalAlignment = VerticalAlignment.Top };
        zoomCol.Children.Add(zoom); zoomCol.Children.Add(focus); zoomCol.Children.Add(image);

        _rightCol = new Grid { Width = RightWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _rightCol.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rightCol.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(speed, 2);
        Grid.SetRow(joyCol, 1); Grid.SetRow(zoomCol, 1); Grid.SetColumn(zoomCol, 1);
        _rightCol.Children.Add(speed); _rightCol.Children.Add(joyCol); _rightCol.Children.Add(zoomCol);

        // ================= página: vídeo à esquerda (ocupa a largura que sobrar), controles à direita =================
        _body = new Grid();
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.Children.Add(_leftCol); _body.Children.Add(_rightCol);
        ApplyArrangement(ActualWidth > 0 && ActualWidth < NarrowBelow);

        _header = (FrameworkElement)Ux.Header("Controle PTZ");
        var page = new StackPanel();
        page.Children.Add(_header);
        page.Children.Add(_body);
        _scroll = new ScrollViewer
        {
            Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 12, 4, 6)
        };
        Content = _scroll;
        UpdateEffects();
        RebuildPresets();
        if (E.Image.Detected) E.Image.Refresh();      // lê os valores atuais da câmera ao abrir a tela
    }

    // ---------------- arranjo da página ----------------
    /// <summary>Janela larga: vídeo à esquerda e controles à direita. Janela estreita: os controles descem para baixo do vídeo.</summary>
    private void ApplyArrangement(bool narrow)
    {
        if (_narrow == narrow) return;
        _narrow = narrow;
        Grid.SetRow(_leftCol, 0); Grid.SetColumn(_leftCol, 0); Grid.SetColumnSpan(_leftCol, narrow ? 2 : 1);
        Grid.SetRow(_rightCol, narrow ? 1 : 0); Grid.SetColumn(_rightCol, narrow ? 0 : 1); Grid.SetColumnSpan(_rightCol, narrow ? 2 : 1);
    }

    /// <summary>
    /// Ajusta a tela ao espaço disponível, para o operador não precisar rolar durante a transmissão: limita a altura do vídeo
    /// (os presets ficam visíveis embaixo dele) e recolhe o cartão de brilho/exposição quando a coluna dos controles não cabe.
    /// </summary>
    private void AutoFit()
    {
        if (ActualWidth <= 0 || _video == null) return;
        ApplyArrangement(ActualWidth < NarrowBelow);
        double view = _scroll.ViewportHeight;
        if (view <= 0) return;
        double top = _header.ActualHeight + _header.Margin.Bottom;

        if (_narrow == true)
        {
            _video.SetMaxStageHeight(view * 0.5);      // empilhado: a tela rola; o vídeo não passa de metade da altura visível
            return;
        }

        if (!_video.SettingsOpen)
        {
            double others = _leftCol.ActualHeight - _video.StageHeight;      // botões do vídeo + cartão de presets
            if (others > 0) _video.SetMaxStageHeight(view - top - others - 2);
        }

        if (_imgUserSet) return;
        double need = top + _rightCol.ActualHeight;
        if (_imgOpen)
        {
            if (need > view + 1 && _imageHost.ActualHeight > 0) { _imgBodyHeight = _imageHost.ActualHeight + _imageHost.Margin.Top; SetImageOpen(false); }
        }
        else if (_imgBodyHeight >= 0 && need + _imgBodyHeight + 4 <= view) SetImageOpen(true);
    }

    private void SetImageOpen(bool open)
    {
        _imgOpen = open;
        _imageHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _imgToggle.Content = open ? "▼" : "▶";
        _imgToggle.ToolTip = open ? "Recolher" : "Mostrar os ajustes de brilho e exposição";
    }

    // ---------------- presets ----------------
    private void RebuildPresets()
    {
        var list = E.Settings.Presets.Where(p => p.CameraId == E.Camera.Id).ToList();
        string sig = E.Camera.Id + "|" + string.Join("|", list.Select(p => p.Slot + ":" + p.Name + ":" + p.ConfirmedOnCamera));
        if (sig == _presetSig) return;
        _presetSig = sig;
        _presetPanel.Children.Clear();
        foreach (var p in list)
        {
            int slot = p.Slot;
            var face = new StackPanel();
            face.Children.Add(new TextBlock { Text = slot.ToString(), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Ux.AccentLight, HorizontalAlignment = HorizontalAlignment.Center });
            face.Children.Add(new TextBlock { Text = p.Name, FontSize = 11, Foreground = Ux.Muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
            var btn = new Button { Content = face, MinHeight = 50, Margin = new Thickness(3), Padding = new Thickness(6, 3, 6, 3), Focusable = false };
            btn.Click += (_, _) => E.Execute(ButtonAction.AcionarPreset, slot);
            btn.ToolTip = p.Name + " — " + (p.ConfirmedOnCamera ? "gravado na memória da câmera" : "gravação ainda sem confirmação da câmera");
            _presetPanel.Children.Add(btn);
        }
        _presetPanel.Children.Add(_homeBtn);
        _presetEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FitPresets();
    }

    /// <summary>Todos os botões de preset com a mesma largura, em uma linha quando cabem (senão, em linhas equilibradas).</summary>
    private void FitPresets()
    {
        double w = _presetPanel.ActualWidth; int n = _presetPanel.Children.Count;
        if (w <= 0 || n == 0) return;
        int cols = Math.Clamp((int)(w / 70), 1, n);
        int rows = (int)Math.Ceiling(n / (double)cols);
        cols = (int)Math.Ceiling(n / (double)rows);
        double item = Math.Min(140, Math.Floor(w / cols));
        if (double.IsNaN(_presetPanel.ItemWidth) || Math.Abs(_presetPanel.ItemWidth - item) >= 1) _presetPanel.ItemWidth = item;
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
            var val = Ux.Text("", 13, true); val.HorizontalAlignment = HorizontalAlignment.Right; val.Margin = new Thickness(0);
            var head = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = Ux.Text(title, 13); lbl.Margin = new Thickness(0);
            Grid.SetColumn(val, 1);
            head.Children.Add(lbl); head.Children.Add(val);

            var slider = new Slider { Minimum = st.Min, Maximum = Math.Max(st.Max, st.Min + 1), Value = Math.Clamp(st.Value, st.Min, Math.Max(st.Max, st.Min + 1)), Height = 26, VerticalAlignment = VerticalAlignment.Center };
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            debounce.Tick += (_, _) => { debounce.Stop(); E.Image.SetValue(param, (int)Math.Round(slider.Value)); };
            slider.ValueChanged += (_, _) => { if (_imgUpdating) return; debounce.Stop(); debounce.Start(); };

            var minus = new Button { Content = "−", Width = 32, MinHeight = 30, FontSize = 16, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 0), ToolTip = "Diminuir (segure para repetir)" };
            var plus = new Button { Content = "+", Width = 32, MinHeight = 30, FontSize = 16, Padding = new Thickness(0), Margin = new Thickness(5, 0, 0, 0), ToolTip = "Aumentar (segure para repetir)" };
            Repeat(minus, () => E.Image.Step(param, -1));
            Repeat(plus, () => E.Image.Step(param, +1));
            var reset = new Button { Content = "↺", Width = 30, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(5, 0, 0, 0), ToolTip = "Voltar ao valor de quando a câmera foi detectada" };
            reset.Click += (_, _) => E.Image.RestoreInitial(param);

            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 6) };
            DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(reset, Dock.Right); DockPanel.SetDock(plus, Dock.Right);
            row.Children.Add(minus); row.Children.Add(reset); row.Children.Add(plus); row.Children.Add(slider);
            _imageHost.Children.Add(head); _imageHost.Children.Add(row);
            _imgRows[param] = (slider, val);
        }

        if (!any)
        {
            string msg = img.Detected
                ? "A câmera não confirmou ajuste de brilho nem de exposição por este protocolo. Nada foi enviado."
                : E.Client.State == ConnectionState.Conectado ? "Ajustes de imagem ainda não detectados. Clique em Detectar." : "Conecte a câmera para detectar os ajustes de imagem.";
            _imageHost.Children.Add(Ux.Text(msg, 12, false, Ux.Muted));
            foreach (var n in new[] { img.Brilho.Note, img.Exposicao.Note }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                _imageHost.Children.Add(Ux.Text(n, 12, false, Ux.Muted));
            _imageHost.ToolTip = null;
        }
        else _imageHost.ToolTip = "Os botões do controle (aba Mapeamento) ajustam os mesmos valores.";
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
        _ptEffect.Text = $"No máximo: Pan {pan} de {cam.MaxPanSpeed} · Tilt {tilt} de {cam.MaxTiltSpeed}";
        _ptEffect.ToolTip = $"Com o analógico (ou a seta) no máximo, a câmera recebe Pan {pan} de {cam.MaxPanSpeed} e Tilt {tilt} de {cam.MaxTiltSpeed} (velocidade VISCA). Cada perfil guarda a sua.";
        _zoomEffect.Text = $"Zoom da tela: nível {zoom} de {cam.MaxZoomSpeed}";
        _zoomEffect.ToolTip = $"A câmera tem só {cam.MaxZoomSpeed} níveis de velocidade de zoom.";
    }

    /// <summary>Mostra, ao lado da caixa "Reduzir Pan/Tilt com o zoom", a posição do zoom lida da câmera e quanto da velocidade sobra.</summary>
    private void RefreshZoomReduction()
    {
        bool on = E.Profile.Motion.ReducePanTiltWithZoom;
        if (_reduceCheck.IsChecked != on) _reduceCheck.IsChecked = on;     // a mesma opção existe na aba Movimento
        string text;
        if (!on) text = "";
        else if (E.Zoom.Fraction < 0) text = E.Client.State == ConnectionState.Conectado ? "zoom ainda não lido da câmera" : "sem câmera";
        else
        {
            double f = PtzController.ZoomSpeedFactor(E.Zoom.Fraction, E.Profile.Motion.ZoomReductionMinPercent);
            text = $"zoom {E.Zoom.Fraction * 100:0}% → velocidade {f * 100:0}%";
        }
        if (text == _zoomShown) return;
        _zoomShown = text;
        _zoomStatus.Text = text;
    }

    /// <summary>Mostra o estado REAL do foco (lido da câmera) no botão de autofoco.</summary>
    private void RefreshFocus()
    {
        var f = E.Focus;
        string sig = f.Mode + "|" + f.Confirmed;
        if (sig == _afShown) return;
        _afShown = sig;
        string st = f.Mode switch { FocusMode.Automatico => "ligado", FocusMode.Manual => "desligado", _ => "—" };
        bool unconfirmed = f.Mode != FocusMode.Desconhecido && !f.Confirmed;
        _afBtn.Content = "Autofoco: " + st + (unconfirmed ? " *" : "");
        if (f.Mode == FocusMode.Automatico)
        {
            _afBtn.Background = Ux.Accent; _afBtn.BorderBrush = Ux.Accent; _afBtn.Foreground = Brushes.White;
        }
        else
        {
            _afBtn.ClearValue(Control.BackgroundProperty); _afBtn.ClearValue(Control.BorderBrushProperty); _afBtn.ClearValue(Control.ForegroundProperty);
        }
        string full = f.Mode switch
        {
            FocusMode.Automatico => "A câmera está focando sozinha. Foco perto/longe só funcionam com o autofoco DESLIGADO.",
            FocusMode.Manual => "Foco manual. Use Perto/Longe, ou \"Focar agora\" para focar uma vez.",
            _ => "Estado do foco ainda não lido da câmera. O botão consulta a câmera antes de trocar."
        };
        _afHint.Text = (f.Mode switch
        {
            FocusMode.Automatico => "Perto/Longe só com o autofoco desligado.",
            FocusMode.Manual => "Foco manual.",
            _ => "Estado do foco ainda não lido da câmera."
        }) + (unconfirmed ? " * não confirmado pela câmera." : "");
        _afHint.ToolTip = full;
        _afBtn.ToolTip = "Liga ou desliga o autofoco contínuo. " + full + (unconfirmed ? " (* a câmera não confirmou este estado.)" : "");
    }

    protected override void Refresh()
    {
        var c = E.Controller;
        _sent.Text = $"Enviado agora →  Pan {c.SentPan:+0;-0;0}   Tilt {c.SentTilt:+0;-0;0}   Zoom {c.SentZoom:+0;-0;0}";
        string who = c.InControl switch
        {
            InputSource.Fisico => "No comando agora: controle físico.",
            InputSource.Virtual => "No comando agora: joystick virtual (mouse).",
            _ => "No comando agora: ninguém (câmera parada)."
        };
        _info.Text = (c.SafetyLatched ? "Parada de segurança: leve os controles ao neutro para continuar.\n" : "") +
                     (c.WaitingNeutralAfterPreset ? "Indo para " + (c.Traveling ?? "a posição") + ": o analógico fica ignorado até voltar ao centro.\n" : "") + who;
        RefreshFocus();
        RefreshZoomReduction();
        if (ImageSignature() != _imgSig) BuildImageCard();
        SyncImageValues();
        RebuildPresets();
        _video?.Refresh();
        AutoFit();
    }
}
