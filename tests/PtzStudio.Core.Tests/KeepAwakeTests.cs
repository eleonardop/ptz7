using PtzStudio.Core.Control;
using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Sinal de vibração zero para manter o controle acordado.</summary>
public class KeepAwakeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private sealed class FakePorts : IXInputPorts
    {
        public bool[] Connected = new bool[4];
        public readonly List<int> Sent = new();
        public bool Throw;
        public bool IsConnected(int port) { if (Throw) throw new InvalidOperationException("falha"); return Connected[port]; }
        public bool SendZeroVibration(int port) { Sent.Add(port); return true; }
    }

    [Fact]
    public void SendsOnStart_ThenEveryThreeMinutes()
    {
        var p = new FakePorts(); p.Connected[0] = true;
        var k = new ControllerKeepAwake(p);
        Assert.True(k.Tick(T0, true));
        Assert.Single(p.Sent);
        Assert.False(k.Tick(T0.AddMinutes(2.9), true));
        Assert.Single(p.Sent);
        Assert.True(k.Tick(T0.AddMinutes(3), true));
        Assert.Equal(2, p.Sent.Count);
        // até 10 minutos parado: pelo menos 3 sinais (início, 3 e 6 min, 9 min)
        for (int s = 181; s <= 600; s++) k.Tick(T0.AddSeconds(s), true);
        Assert.True(p.Sent.Count >= 4);
    }

    [Fact]
    public void Disabled_SendsNothing_AndRestartsImmediatelyWhenEnabledAgain()
    {
        var p = new FakePorts(); p.Connected[0] = true;
        var k = new ControllerKeepAwake(p);
        for (int s = 0; s < 1000; s += 10) k.Tick(T0.AddSeconds(s), false);
        Assert.Empty(p.Sent);
        Assert.False(k.IsDue(T0, false));
        Assert.True(k.IsDue(T0.AddSeconds(1000), true));
        Assert.True(k.Tick(T0.AddSeconds(1000), true));
        Assert.Single(p.Sent);
    }

    [Fact]
    public void OnlyConnectedXInputPortsReceiveTheSignal()
    {
        var p = new FakePorts(); p.Connected[1] = true; p.Connected[3] = true;
        var k = new ControllerKeepAwake(p);
        k.Tick(T0, true);
        Assert.Equal(new[] { 1, 3 }, p.Sent);
        Assert.Equal(2, k.LastCount);
    }

    [Fact]
    public void NoXInputController_NothingIsSent_ButTheRoundIsRecorded()
    {
        var p = new FakePorts();
        var k = new ControllerKeepAwake(p);
        Assert.True(k.Tick(T0, true));
        Assert.Empty(p.Sent);
        Assert.Equal(0, k.LastCount);
        Assert.Equal(T0, k.LastSent);
    }

    [Fact]
    public void AWindowsFailure_NeverEscapes()
    {
        var p = new FakePorts { Throw = true };
        var k = new ControllerKeepAwake(p);
        Assert.True(k.Tick(T0, true));
        Assert.Equal(0, k.LastCount);
    }

    [Fact]
    public void IsDue_MatchesTheSchedule()
    {
        var k = new ControllerKeepAwake(new FakePorts());
        Assert.True(k.IsDue(T0, true));
        k.Tick(T0, true);
        Assert.False(k.IsDue(T0.AddMinutes(1), true));
        Assert.True(k.IsDue(T0.AddMinutes(3), true));
    }

    [Fact]
    public void TheSignalIsIndependentOfCameraMotion()
    {
        // O sinal não passa pelo controlador da câmera: com ele rodando, nada é enviado à câmera e Pan/Tilt/Zoom continuam em zero.
        var cam = new FakeSender();
        var c = new PtzController(cam, () => new CameraConfig { CommandIntervalMs = 0 }, () => new Profile());
        var p = new FakePorts(); p.Connected[0] = true;
        var k = new ControllerKeepAwake(p);
        var t = T0;
        for (int i = 0; i < 20; i++)
        {
            t = t.AddMinutes(1);
            k.Tick(t, true);
            c.SetAnalog(InputSource.Fisico, 0, 0, 0, t);
            c.Tick(0.02, t);
        }
        Assert.True(p.Sent.Count >= 6);
        Assert.Equal(0, c.SentPan); Assert.Equal(0, c.SentTilt); Assert.Equal(0, c.SentZoom);
        Assert.DoesNotContain(cam.Sent, b => cam.IsPanTiltMove(b));
    }

    [Fact]
    public void OnByDefault_AndOldSettingsFilesLoadWithItOn()
    {
        Assert.True(new AppSettings().KeepControllerAwake);
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ActiveProfile\":\"Igreja\"}")!;
        Assert.True(s.KeepControllerAwake);
    }
}
