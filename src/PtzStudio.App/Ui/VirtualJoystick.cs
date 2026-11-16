using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace PtzStudio.App.Ui;

/// <summary>
/// Joystick virtual para o mouse: valores X (direita +) e Y (cima +) de -1 a 1.
/// Duas formas de usar: arrastar o botão azul do centro (proporcional ao deslocamento, em qualquer direção) ou segurar um dos
/// 8 botões de direção em volta (velocidade cheia do perfil naquela direção). Soltar, sair do botão com o clique preso ou
/// perder a captura do mouse sempre volta ao centro (a câmera para).
/// </summary>
internal sealed class VirtualJoystick : Canvas
{
    private const double Size = 260, KnobR = 34;
    private const double DragR = 66;                    // dentro deste raio o clique começa o arraste; fora dele ficam os botões de direção
    private const double WedgeIn = 72, WedgeOut = 123;  // anel dos botões de direção
    private static readonly Brush WedgeFill = Frozen(0x25, 0x2A, 0x35);
    private static readonly Brush WedgeHover = Frozen(0x30, 0x37, 0x45);
    private static readonly Brush WedgeEdge = Frozen(0x3B, 0x43, 0x52);
    private static readonly Brush Chevron = Frozen(0x9A, 0xA4, 0xB5);
    private static readonly Pen LeaveTolerance = MakePen();

    private readonly Ellipse _knob;
    private Path? _activeWedge;
    public double X { get; private set; }
    public double Y { get; private set; }
    public event Action<double, double>? ValueChanged;

    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    private static Pen MakePen() { var p = new Pen(Brushes.Black, 24); p.Freeze(); return p; }

    public VirtualJoystick()
    {
        Width = Size; Height = Size; Background = Brushes.Transparent; Cursor = Cursors.Hand;
        var ring = new Ellipse
        {
            Width = Size - 6, Height = Size - 6, StrokeThickness = 3,
            Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x43, 0x52)), Fill = new SolidColorBrush(Color.FromRgb(0x0F, 0x12, 0x17)),
            IsHitTestVisible = false
        };
        SetLeft(ring, 3); SetTop(ring, 3);
        Children.Add(ring);

        // 8 botões de direção: ângulo 0 = direita, sentido anti-horário (90 = cima)
        (double angle, double x, double y, string tip)[] dirs =
        {
            (90, 0, 1, "Cima"), (45, 1, 1, "Cima e direita"), (0, 1, 0, "Direita"), (315, 1, -1, "Baixo e direita"),
            (270, 0, -1, "Baixo"), (225, -1, -1, "Baixo e esquerda"), (180, -1, 0, "Esquerda"), (135, -1, 1, "Cima e esquerda")
        };
        foreach (var d in dirs) AddWedge(d.angle, d.x, d.y, d.tip);

        var inner = new Ellipse
        {
            Width = DragR * 2, Height = DragR * 2, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 },
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3B)), IsHitTestVisible = false
        };
        SetLeft(inner, Size / 2 - DragR); SetTop(inner, Size / 2 - DragR);
        Children.Add(inner);

        _knob = new Ellipse
        {
            Width = KnobR * 2, Height = KnobR * 2, Fill = Ux.Accent, Stroke = Ux.AccentLight, StrokeThickness = 4, IsHitTestVisible = false
        };
        Children.Add(_knob);
        Place(0, 0);

        // Arraste: só começa perto do centro (o anel de fora é dos botões de direção); depois de começar, vai até a borda como antes.
        MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(this);
            double dx = p.X - Size / 2, dy = p.Y - Size / 2;
            if (dx * dx + dy * dy > DragR * DragR) return;
            CaptureMouse(); Move(p);
        };
        MouseMove += (_, e) => { if (IsMouseCaptured) Move(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, _) => { if (IsMouseCaptured) Release(); };
        LostMouseCapture += (_, _) => Release();
        Unloaded += (_, _) => Release();            // saiu da tela: nunca deixar o joystick "preso" fora do centro
    }

    private static Point Polar(double r, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(Size / 2 + r * Math.Cos(a), Size / 2 - r * Math.Sin(a));
    }

    private void AddWedge(double angle, double x, double y, string tip)
    {
        const double half = 19.5;                    // cada botão ocupa 39 dos 45 graus (sobra uma folga entre eles)
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(Polar(WedgeIn, angle + half), true, true);
            c.LineTo(Polar(WedgeOut, angle + half), true, true);
            c.ArcTo(Polar(WedgeOut, angle - half), new System.Windows.Size(WedgeOut, WedgeOut), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(Polar(WedgeIn, angle - half), true, true);
            c.ArcTo(Polar(WedgeIn, angle + half), new System.Windows.Size(WedgeIn, WedgeIn), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geo.Freeze();
        var wedge = new Path { Data = geo, Fill = WedgeFill, Stroke = WedgeEdge, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, ToolTip = tip + " (segure para mover)" };

        // seta apontando para fora
        double mid = (WedgeIn + WedgeOut) / 2;
        var tipPt = Polar(mid + 7, angle);
        var chev = new Polyline
        {
            Points = new PointCollection { Polar(mid - 3, angle + 8), tipPt, Polar(mid - 3, angle - 8) },
            Stroke = Chevron, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
        };

        void Paint() => wedge.Fill = ReferenceEquals(_activeWedge, wedge) ? Ux.Accent : wedge.IsMouseOver ? WedgeHover : WedgeFill;
        wedge.MouseEnter += (_, _) => Paint();
        wedge.MouseLeave += (_, _) => Paint();
        wedge.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;                         // não começa o arraste do centro
            if (IsMouseCaptured) ReleaseMouseCapture();
            _activeWedge = wedge;
            if (!wedge.CaptureMouse()) { _activeWedge = null; return; }
            chev.Stroke = Brushes.White; Paint();
            Set(x, y, false);
        };
        wedge.MouseMove += (_, e) =>
        {
            // clique preso e o mouse saiu do botão (com uma pequena tolerância): para, em vez de continuar andando "às cegas"
            if (!wedge.IsMouseCaptured) return;
            var p = e.GetPosition(this);
            if (!geo.FillContains(p) && !geo.StrokeContains(LeaveTolerance, p)) wedge.ReleaseMouseCapture();
        };
        wedge.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (wedge.IsMouseCaptured) wedge.ReleaseMouseCapture(); };
        wedge.LostMouseCapture += (_, _) =>
        {
            if (ReferenceEquals(_activeWedge, wedge)) _activeWedge = null;
            chev.Stroke = Chevron; Paint();
            Set(0, 0, true);
        };
        Children.Add(wedge);
        Children.Add(chev);
    }

    private void Move(Point p)
    {
        double c = Size / 2, max = c - KnobR - 4;
        double dx = p.X - c, dy = p.Y - c;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len > max) { dx = dx / len * max; dy = dy / len * max; }
        Set(dx / max, -dy / max, true);
    }

    /// <summary>Volta ao centro (arraste ou botão de direção): a câmera para.</summary>
    public void Release()
    {
        var w = _activeWedge;
        if (w != null && w.IsMouseCaptured) w.ReleaseMouseCapture();     // o próprio botão zera o valor ao perder a captura
        if (IsMouseCaptured) ReleaseMouseCapture();
        Set(0, 0, true);
    }

    private void Set(double x, double y, bool moveKnob)
    {
        if (moveKnob || (x == 0 && y == 0)) Place(x, y);
        if (x == X && y == Y) return;
        X = x; Y = y;
        ValueChanged?.Invoke(x, y);
    }

    private void Place(double x, double y)
    {
        double c = Size / 2, max = c - KnobR - 4;
        SetLeft(_knob, c + x * max - KnobR);
        SetTop(_knob, c - y * max - KnobR);
    }
}
