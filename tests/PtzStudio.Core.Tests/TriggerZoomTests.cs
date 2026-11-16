using PtzStudio.Core.Control;
using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Etapa 3: RT/LT mapeados como Zoom aproximar/afastar usam a pressão do gatilho; soltar para na hora.</summary>
public class TriggerZoomTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static bool IsZoomStop(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && b[4] == 0x00;
    private static bool IsZoomTele(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && (b[4] & 0xF0) == 0x20;
    private static bool IsZoomWide(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && (b[4] & 0xF0) == 0x30;
    private static int Speed(byte[] b) => (b[4] & 0x0F) + 1;

    // ---------- leitura da pressão ----------
    [Fact]
    public void TriggerAmount_UsesTheAvailableTravel_ForSeparateAndCombinedAxes()
    {
        // eixo só do gatilho: repouso 0, curso 0..1
        Assert.Equal(0.5, InputInterpreter.TriggerAmount(0.5, true, 0.0), 3);
        Assert.Equal(1.0, InputInterpreter.TriggerAmount(1.0, true, 0.0), 3);
        // LT e RT juntos num eixo: repouso 0,5; LT vai a 1,0 e RT vai a 0,0 — cada um tem meio curso, mas a pressão final é 0..1
        Assert.Equal(1.0, InputInterpreter.TriggerAmount(0.5, true, 0.5), 3);
        Assert.Equal(1.0, InputInterpreter.TriggerAmount(-0.5, false, 0.5), 3);
        Assert.Equal(0.5, InputInterpreter.TriggerAmount(-0.25, false, 0.5), 3);
        // ruído de leitura e sentido contrário não contam
        Assert.Equal(0, InputInterpreter.TriggerAmount(0.01, true, 0.0));
        Assert.Equal(0, InputInterpreter.TriggerAmount(-0.4, true, 0.5));
    }

    [Fact]
    public void Interpret_ReportsThePressureOfWatchedAxes_EvenBelowTheOnOffThreshold()
    {
        var snap = new InputSnapshot { Axes = new double[8] };
        snap.Axes[2] = 0.25;                                    // gatilho apertado pela metade (eixo combinado, repouso 0,5)
        var rest = new double[8]; rest[2] = 0.5;
        var r = InputInterpreter.Interpret(snap, new AxisMap(), rest, new[] { VirtualButtons.AxisMinus(2), VirtualButtons.AxisPlus(2) });
        Assert.Equal(0.5, r.Analog[VirtualButtons.AxisMinus(2)], 3);
        Assert.DoesNotContain(VirtualButtons.AxisPlus(2), r.Analog.Keys);
        Assert.DoesNotContain(VirtualButtons.AxisMinus(2), r.Pressed);          // ainda abaixo do limite do "botão"
    }

    // ---------- roteador ----------
    private static Profile ProfileWith(params (int button, ButtonAction action)[] binds)
    {
        var p = new Profile();
        p.Bindings.Clear();
        foreach (var (b, a) in binds) p.Bindings.Add(new ButtonBinding { Button = b, Action = a });
        return p;
    }

    [Fact]
    public void Router_ZoomFollowsTheTriggerPressure_AndIsZeroWhenReleased()
    {
        int rt = VirtualButtons.AxisMinus(2), lt = VirtualButtons.AxisPlus(2);
        var p = ProfileWith((rt, ButtonAction.ZoomAproximar), (lt, ButtonAction.ZoomAfastar));
        var router = new ButtonRouter();

        router.Update(Array.Empty<int>(), p, T0, new Dictionary<int, double> { [rt] = 0.4 });
        Assert.Equal(0.4, router.HeldZoomAnalog, 3);
        Assert.Equal(0, router.HeldZoom);                                        // não é liga/desliga

        router.Update(Array.Empty<int>(), p, T0, new Dictionary<int, double> { [rt] = 0.9 });
        Assert.Equal(0.9, router.HeldZoomAnalog, 3);

        router.Update(Array.Empty<int>(), p, T0, new Dictionary<int, double> { [lt] = 0.6 });
        Assert.Equal(-0.6, router.HeldZoomAnalog, 3);                            // LT afasta

        router.Update(Array.Empty<int>(), p, T0, new Dictionary<int, double>());
        Assert.Equal(0, router.HeldZoomAnalog);                                  // soltou
    }

    [Fact]
    public void Router_WithoutAnalogReading_StillWorksAsAnOrdinaryButton()
    {
        int rt = VirtualButtons.AxisMinus(2);
        var p = ProfileWith((rt, ButtonAction.ZoomAproximar));
        var router = new ButtonRouter();
        router.Update(new[] { rt }, p, T0);
        Assert.Equal(1, router.HeldZoom);
        Assert.Equal(0, router.HeldZoomAnalog);
    }

    [Fact]
    public void Router_PhysicalZoomButtonsAreUnchanged()
    {
        var p = ProfileWith((4, ButtonAction.ZoomAproximar), (5, ButtonAction.ZoomAfastar));
        var router = new ButtonRouter();
        router.Update(new[] { 4 }, p, T0, new Dictionary<int, double>());
        Assert.Equal(1, router.HeldZoom);
        Assert.Equal(0, router.HeldZoomAnalog);
    }

    // ---------- controlador ----------
    private static (PtzController c, FakeFeedbackSender s, Profile p) Make(bool softStop = false)
    {
        var s = new FakeFeedbackSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = softStop ? 30 : 0; a.NoiseThreshold = 0; }
        p.Motion.Zoom.SoftStopOnRelease = softStop;
        p.Motion.Zoom.Curve = ResponseCurve.Linear;
        p.Motion.Zoom.MinSpeed = 1; p.Motion.Zoom.MaxSpeed = 7;
        var cam = new CameraConfig { CommandIntervalMs = 0, MaxZoomSpeed = 8 };
        var c = new PtzController(s, () => cam, () => p);
        c.AttachFeedback(s);
        return (c, s, p);
    }

    private static int ZoomSpeedFor(double pressure)
    {
        var (c, s, _) = Make();
        c.SetTriggerZoom(pressure);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, T0);
        c.Tick(0.016, T0);
        var z = s.Sent.LastOrDefault(IsZoomTele);
        return z == null ? 0 : Speed(z);
    }

    [Fact]
    public void ZoomSpeed_GrowsWithTheTriggerPressure()
    {
        int light = ZoomSpeedFor(0.25), medium = ZoomSpeedFor(0.6), full = ZoomSpeedFor(1.0);
        Assert.True(light >= 1, "um aperto leve já deve dar o zoom mais lento");
        Assert.True(medium > light, $"médio ({medium}) deve ser mais rápido que leve ({light})");
        Assert.True(full > medium, $"total ({full}) deve ser mais rápido que médio ({medium})");
        Assert.Equal(7, full);
    }

    [Fact]
    public void ABarelyTouchedTrigger_DoesNotMoveTheZoom()
    {
        Assert.Equal(0, ZoomSpeedFor(0.04));                                     // abaixo da zona morta do zoom
    }

    [Fact]
    public void LT_ZoomsOut()
    {
        var (c, s, _) = Make();
        c.SetTriggerZoom(-0.8);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, T0);
        c.Tick(0.016, T0);
        Assert.Contains(s.Sent, IsZoomWide);
        Assert.DoesNotContain(s.Sent, IsZoomTele);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingTheTrigger_SendsStopInTheSameCycle_EvenWithSoftStopOn(bool softStop)
    {
        var (c, s, _) = Make(softStop);
        var t = T0;
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, t);
        c.SetTriggerZoom(1.0);
        for (int i = 0; i < 10; i++) { c.SetAnalog(InputSource.Fisico, 0, 0, 0, t); c.Tick(0.016, t); t = t.AddMilliseconds(16); }
        Assert.Contains(s.Sent, IsZoomTele);

        s.Sent.Clear();
        c.SetTriggerZoom(0);                                                     // soltou
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, t);
        c.Tick(0.016, t);
        Assert.Contains(s.Sent, IsZoomStop);                                     // parada no mesmo ciclo
        Assert.DoesNotContain(s.Sent, IsZoomTele);
        Assert.Equal(0, c.SentZoom);
    }

    [Fact]
    public void TriggerZoom_KeepsAliveWhileHeld_AndDoesNotTripTheWatchdog()
    {
        var (c, s, _) = Make();
        var t = T0;
        c.SetTriggerZoom(0.7);
        for (int i = 0; i < 100; i++)                                            // 1,6 s segurando
        {
            c.SetAnalog(InputSource.Fisico, 0, 0, 0, t);
            c.Tick(0.016, t);
            t = t.AddMilliseconds(16);
        }
        Assert.False(c.SafetyLatched);
        Assert.NotEqual(0, c.SentZoom);
    }
}
