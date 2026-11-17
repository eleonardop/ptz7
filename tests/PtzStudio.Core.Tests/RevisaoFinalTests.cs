using PtzStudio.Core.Control;
using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Revisão final: correções 1 a 4 (cada teste falha sem a sua correção).</summary>
public class RevisaoFinalTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static (PtzController c, FakeSender s) Make()
    {
        var s = new FakeSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = 0; a.NoiseThreshold = 0; }
        p.Motion.PanTiltSpeedPercent = 100;
        var cam = new CameraConfig { CommandIntervalMs = 0 };
        return (new PtzController(s, () => cam, () => p), s);
    }

    private static DateTime Step(PtzController c, DateTime t, (double x, double y, double z) phys, (double x, double y, double z) virt)
    {
        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, phys.x, phys.y, phys.z, t);
        c.SetAnalog(InputSource.Virtual, virt.x, virt.y, virt.z, t);
        c.Tick(0.02, t);
        return t;
    }

    private static bool IsZoomMove(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && b[4] != 0x00;

    // ---------- 1: soltar o mouse não devolve o comando a um controle físico que está sendo ignorado ----------
    [Fact]
    public void Correcao1_ReleasingTheMouse_DoesNotHandTheCameraBackToAnIgnoredStuckController()
    {
        var (c, s) = Make();
        var t = Step(c, T0, (0.5, 0, 0), (0, 0, 0));                 // analógico físico preso em 0,5
        Assert.Equal(InputSource.Fisico, c.InControl);

        c.RequestVirtualControl();                                     // o operador assume com o mouse
        t = Step(c, t, (0.5, 0, 0), (0, 1, 0));
        Assert.Equal(InputSource.Virtual, c.InControl);
        Assert.Equal(0, c.SentPan);

        s.Sent.Clear();
        for (int i = 0; i < 10; i++) t = Step(c, t, (0.5, 0, 0), (0, 0, 0));   // solta o mouse; o físico continua preso
        Assert.Null(c.InControl);
        Assert.Equal(0, c.SentPan);                                    // antes da correção: Pan voltava a ser enviado
        Assert.Equal(0, c.SentTilt);
        Assert.DoesNotContain(s.Sent, b => s.IsPanTiltMove(b));
    }

    // ---------- 2: controle conectado com um eixo fora do centro não move a câmera ----------
    [Fact]
    public void Correcao2_AControllerConnectedWithAnAxisOffCenter_DoesNotMoveTheCamera()
    {
        // O problema: sem a parada de segurança ao conectar, um eixo de zoom fora do centro (gatilho no lugar do eixo) move a câmera.
        var (before, sb) = Make();
        var t = T0;
        for (int i = 0; i < 5; i++) t = Step(before, t, (0, 0, 1), (0, 0, 0));
        Assert.Contains(sb.Sent, IsZoomMove);

        // A correção: o programa chama SafetyStop("controle conectado") ao conectar o controle.
        var (c, s) = Make();
        c.SafetyStop("controle conectado");
        s.Sent.Clear();
        t = T0;
        for (int i = 0; i < 10; i++) t = Step(c, t, (0, 0, 1), (0, 0, 0));
        Assert.Equal(0, c.SentZoom);
        Assert.DoesNotContain(s.Sent, IsZoomMove);
        Assert.True(c.SafetyLatched);

        // O mouse continua podendo tirar a câmera da trava: clica, solta e passa a comandar.
        c.RequestVirtualControl();
        t = Step(c, t, (0, 0, 1), (1, 0, 0));
        t = Step(c, t, (0, 0, 1), (0, 0, 0));
        Assert.False(c.SafetyLatched);
        Assert.Equal(0, c.SentZoom);
        c.RequestVirtualControl();
        t = Step(c, t, (0, 0, 1), (1, 0, 0));
        Assert.Equal(InputSource.Virtual, c.InControl);
        Assert.True(c.SentPan > 0);
        Assert.Equal(0, c.SentZoom);
    }

    [Fact]
    public void Correcao2_AControllerConnectedWithAxesCentered_IsUsableOnTheNextCycle()
    {
        var (c, _) = Make();
        c.SafetyStop("controle conectado");
        var t = Step(c, T0, (0, 0, 0), (0, 0, 0));
        Assert.False(c.SafetyLatched);
        t = Step(c, t, (1, 0, 0), (0, 0, 0));
        Assert.True(c.SentPan > 0);
    }

    // ---------- 3: repouso guardado de uma abertura anterior protege já a primeira conexão ----------
    private static double[] Axes(double z) { var a = new double[8]; for (int i = 0; i < 8; i++) a[i] = 0.5; a[2] = z; return a; }

    [Fact]
    public void Correcao3_ATriggerHeldOnTheFirstConnection_IsRejectedWhenARestWasSaved()
    {
        // O problema: sem histórico, um RT apertado ao conectar vira o "repouso"; soltar o RT passa a ser lido como LT pela metade.
        var bad = new RestTracker();
        bad.Offer(Axes(0.0), T0); bad.Offer(Axes(0.0), T0.AddMilliseconds(400));
        var r = InputInterpreter.Interpret(new InputSnapshot { Axes = Axes(0.5) }, new AxisMap(), bad.Rest,
                                           new[] { VirtualButtons.AxisPlus(2), VirtualButtons.AxisMinus(2) });
        Assert.NotEmpty(r.Analog);                                     // gatilho "apertado" com tudo solto

        // A correção: o repouso salvo deste controle é usado como comparação na primeira conexão.
        var t = new RestTracker();
        t.SetHistory(Axes(0.5));
        t.Offer(Axes(0.0), T0); t.Offer(Axes(0.0), T0.AddMilliseconds(400));
        Assert.True(t.Ready);
        Assert.Equal(0.5, t.Rest[2]);
        Assert.NotNull(t.Warning);
        var ok = InputInterpreter.Interpret(new InputSnapshot { Axes = Axes(0.5) }, new AxisMap(), t.Rest,
                                            new[] { VirtualButtons.AxisPlus(2), VirtualButtons.AxisMinus(2) });
        Assert.Empty(ok.Analog);
    }

    [Fact]
    public void Correcao3_InvalidSavedRest_IsIgnored_AndRestCanStillBeRecorded()
    {
        var t = new RestTracker();
        t.SetHistory(new double[] { 0.5, 0.5 });                       // tamanho errado
        t.SetHistory(null);
        t.Offer(Axes(0.3), T0); t.Offer(Axes(0.3), T0.AddMilliseconds(400));
        Assert.Equal(0.3, t.Rest[2]);
        Assert.Null(t.Warning);
    }

    [Fact]
    public void Correcao3_OldSettingsWithoutSavedRest_LoadWithAnEmptyList()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ActiveProfile\":\"Igreja\"}")!;
        Assert.NotNull(s.RestByDevice);
        Assert.Empty(s.RestByDevice);
    }

    // ---------- 4: botões e controles da tela Controle PTZ não ficam com o foco do teclado ----------
    // Verificação do código-fonte (a parte de tela não roda nos testes automáticos): Espaço/Enter/setas não podem
    // repetir o último preset clicado, trocar o autofoco nem mudar as velocidades.
    [Fact]
    public void Correcao4_PtzScreenButtonsAndSpeedSliders_AreNotKeyboardFocusable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PtzStudio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "PtzStudio.App", "Tabs", "PtzTab.cs"));
        string[] lines = src.Split('\n');
        string Line(string marker) => Assert.Single(lines, l => l.Contains(marker));
        Assert.Contains("Focusable = false", Line("var btn = new Button { Content = face"));             // presets
        Assert.Contains("Focusable = false", Line("_afBtn = new Button {"));                            // autofoco
        Assert.Contains("Focusable = false", Line("var onePush = new Button {"));                       // focar agora
        Assert.Contains("Focusable = false", Line("var s = new Slider { Minimum = 1, Maximum = 100"));  // velocidades
    }
}
