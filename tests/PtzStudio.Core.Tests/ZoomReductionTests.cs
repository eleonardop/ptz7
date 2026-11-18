using PtzStudio.Core.Control;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Redução da velocidade de Pan/Tilt conforme o zoom fecha.</summary>
public class ZoomReductionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static (PtzController c, FakeSender s, Profile p) Make(bool reduce, int minPercent = 25)
    {
        var s = new FakeSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = 0; a.NoiseThreshold = 0; }
        p.Motion.PanTiltSpeedPercent = 100;
        p.Motion.ReducePanTiltWithZoom = reduce;
        p.Motion.ZoomReductionMinPercent = minPercent;
        var cam = new CameraConfig { CommandIntervalMs = 0 };
        return (new PtzController(s, () => cam, () => p), s, p);
    }

    private static DateTime _clock = T0;     // relógio sempre para a frente entre as chamadas

    private static int PanAtFullStick(PtzController c, double zoomFraction)
    {
        c.ZoomFraction = zoomFraction;
        var t = _clock;
        for (int i = 0; i < 5; i++) { t = t.AddMilliseconds(20); c.SetAnalog(InputSource.Fisico, 1, 0, 0, t); c.Tick(0.02, t); }
        int pan = c.SentPan;
        for (int i = 0; i < 3; i++) { t = t.AddMilliseconds(20); c.SetAnalog(InputSource.Fisico, 0, 0, 0, t); c.Tick(0.02, t); }
        _clock = t.AddSeconds(1);
        return pan;
    }

    // ---------- curva ----------
    [Fact]
    public void Factor_IsOneWithZoomOpen_AndTheMinimumAtFullZoom()
    {
        Assert.Equal(1.0, PtzController.ZoomSpeedFactor(0, 25), 6);
        Assert.Equal(0.25, PtzController.ZoomSpeedFactor(1, 25), 6);
        Assert.Equal(0.40, PtzController.ZoomSpeedFactor(1, 40), 6);
    }

    [Fact]
    public void Factor_FallsGraduallyAndNeverRises_AsZoomCloses()
    {
        double prev = 1.0;
        for (int i = 0; i <= 20; i++)
        {
            double f = PtzController.ZoomSpeedFactor(i / 20.0, 25);
            Assert.True(f <= prev + 1e-9);
            Assert.InRange(f, 0.25, 1.0);
            prev = f;
        }
        Assert.True(PtzController.ZoomSpeedFactor(0.25, 25) > 0.85);     // pouco zoom: quase nada muda
    }

    [Fact]
    public void Factor_UnknownZoomPosition_MeansNoReduction()
    {
        Assert.Equal(1.0, PtzController.ZoomSpeedFactor(-1, 25));
        Assert.Equal(1.0, PtzController.ZoomSpeedFactor(double.NaN, 25));
    }

    [Fact]
    public void Factor_MinimumIsClampedBetween5And100Percent()
    {
        Assert.Equal(0.05, PtzController.ZoomSpeedFactor(1, 0), 6);
        Assert.Equal(1.0, PtzController.ZoomSpeedFactor(1, 150), 6);
    }

    // ---------- controlador ----------
    [Fact]
    public void Enabled_PanIsSlowerWithZoomClosed()
    {
        var (c, _, _) = Make(true);
        int wide = PanAtFullStick(c, 0);
        int tele = PanAtFullStick(c, 1);
        Assert.Equal(12, wide);                                        // velocidade máxima do perfil, sem redução
        Assert.Equal(3, tele);                                         // 25% de 12
        Assert.Equal(0.25, c.PanTiltZoomFactor, 6);
    }

    [Fact]
    public void Disabled_ZoomPositionChangesNothing()
    {
        var (c, _, _) = Make(false);
        Assert.Equal(12, PanAtFullStick(c, 1));
        Assert.Equal(1.0, c.PanTiltZoomFactor, 6);
    }

    [Fact]
    public void Enabled_UnknownZoomPosition_KeepsFullSpeed()
    {
        var (c, _, _) = Make(true);
        Assert.Equal(12, PanAtFullStick(c, -1));
    }

    [Fact]
    public void Enabled_MovementNeverStops_EvenAtTheLowestReduction()
    {
        var (c, s, p) = Make(true, 5);
        p.Motion.PanTiltSpeedPercent = 10;
        Assert.Equal(1, PanAtFullStick(c, 1));                         // nunca 0: a câmera continua andando devagar
        Assert.Contains(s.Sent, b => s.IsPanTiltMove(b));
    }

    [Fact]
    public void Enabled_ZoomSpeedItselfIsNotReduced()
    {
        var (c, _, _) = Make(true);
        c.ZoomFraction = 1;
        var t = T0;
        for (int i = 0; i < 5; i++) { t = t.AddMilliseconds(20); c.SetAnalog(InputSource.Fisico, 0, 0, 1, t); c.Tick(0.02, t); }
        Assert.Equal(5, c.SentZoom);                                   // Zoom.MaxSpeed padrão
    }

    [Fact]
    public void Enabled_ReleasingStillStopsImmediately()
    {
        var (c, s, _) = Make(true);
        c.ZoomFraction = 1;
        var t = T0.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t); c.Tick(0.02, t);
        s.Sent.Clear();
        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, t); c.Tick(0.02, t);
        Assert.Contains(s.Sent, b => s.IsPanTiltStop(b));
        Assert.Equal(0, c.SentPan);
    }

    [Fact]
    public void OldSettingsFiles_LoadWithTheReductionOff()
    {
        var m = System.Text.Json.JsonSerializer.Deserialize<MotionSettings>("{\"PanTiltSpeedPercent\":65}")!;
        Assert.False(m.ReducePanTiltWithZoom);
        Assert.Equal(25, m.ZoomReductionMinPercent);
    }

    // ---------- leitura da posição do zoom ----------
    [Fact]
    public void ZoomPositionInquiry_IsBuiltAsInTheVISCASpecification()
    {
        Assert.Equal(new byte[] { 0x81, 0x09, 0x04, 0x47, 0xFF }, ViscaCommands.ZoomPositionInquiry(1));
    }

    [Fact]
    public void ZoomPositionReply_IsDecodedFromNibbles()
    {
        Assert.True(ViscaCommands.TryReadZoomPosition(new byte[] { 0x90, 0x50, 0x04, 0x00, 0x00, 0x00, 0xFF }, out int p));
        Assert.Equal(0x4000, p);
        Assert.True(ViscaCommands.TryReadZoomPosition(new byte[] { 0x90, 0x50, 0x01, 0x02, 0x03, 0x04, 0xFF }, out p));
        Assert.Equal(0x1234, p);
        Assert.False(ViscaCommands.TryReadZoomPosition(new byte[] { 0x90, 0x50, 0x14, 0x00, 0x00, 0x00, 0xFF }, out _));   // nibble inválido
        Assert.False(ViscaCommands.TryReadZoomPosition(new byte[] { 0x90, 0x50, 0x02, 0xFF }, out _));                     // outro formato
        Assert.False(ViscaCommands.TryReadZoomPosition(null, out _));
    }

    private sealed class FakeZoomCamera : IViscaInquirer
    {
        public volatile bool Answers = true;
        public int Position;
        public ConnectionState State => ConnectionState.Conectado;
        public void Send(byte[] payload) { }
        public Task<ViscaResponse?> InquireAsync(byte[] inquiry, int timeoutMs = 0, Func<byte[], bool>? accept = null)
        {
            if (!Answers) return Task.FromResult<ViscaResponse?>(null);
            int v = Position;
            return Task.FromResult<ViscaResponse?>(new ViscaResponse(ViscaResponseKind.Inquerito,
                new byte[] { 0x90, 0x50, (byte)((v >> 12) & 0xF), (byte)((v >> 8) & 0xF), (byte)((v >> 4) & 0xF), (byte)(v & 0xF), 0xFF }));
        }
    }

    [Fact]
    public async Task CameraZoom_ReadsPosition_AndForgetsItAfterThreeFailures()
    {
        var cam = new FakeZoomCamera { Position = 0x2000 };
        var z = new CameraZoom(cam, () => new CameraConfig());
        Assert.Equal(-1, z.Fraction);
        Assert.True(await z.RefreshAsync());
        Assert.Equal(0.5, z.Fraction, 6);

        cam.Answers = false;
        Assert.False(await z.RefreshAsync());
        Assert.False(await z.RefreshAsync());
        Assert.Equal(0.5, z.Fraction, 6);                              // duas falhas: mantém a última posição
        Assert.False(await z.RefreshAsync());
        Assert.Equal(-1, z.Fraction);                                  // três: posição desconhecida (sem redução)

        cam.Answers = true;
        Assert.True(await z.RefreshAsync());
        Assert.Equal(0, z.Failures);
        Assert.Equal(0.5, z.Fraction, 6);
    }

    [Fact]
    public async Task CameraZoom_DigitalZoomBeyondTheUsualMaximum_BecomesTheNewMaximum()
    {
        var cam = new FakeZoomCamera { Position = 0x6000 };
        var z = new CameraZoom(cam, () => new CameraConfig());
        await z.RefreshAsync();
        Assert.Equal(1.0, z.Fraction, 6);
        cam.Position = 0x3000;
        await z.RefreshAsync();
        Assert.Equal(0.5, z.Fraction, 6);
        z.Reset();
        Assert.Equal(-1, z.Fraction);
    }
}
