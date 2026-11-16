using PtzStudio.Core.Control;
using PtzStudio.Core.Models;
using PtzStudio.Core.Storage;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>
/// Controle físico e joystick virtual (mouse) ativos ao mesmo tempo, sem botão de alternância: quem sai do centro primeiro
/// mantém o comando até voltar ao centro, e as duas entradas nunca são somadas (o movimento continua proporcional).
/// </summary>
public class InputArbitrationTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptz-input-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static (PtzController c, FakeSender s) Make(int virtualZoomPercent = 40)
    {
        var s = new FakeSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = 0; a.NoiseThreshold = 0; }
        p.Motion.PanTiltSpeedPercent = 100;
        p.Motion.VirtualZoomPercent = virtualZoomPercent;
        var cam = new CameraConfig { CommandIntervalMs = 0 };
        return (new PtzController(s, () => cam, () => p), s);
    }

    /// <summary>Um ciclo do motor: as duas entradas são sempre informadas, como faz o AppEngine.</summary>
    private static DateTime Step(PtzController c, DateTime t, (double x, double y, double z) phys, (double x, double y, double z) virt)
    {
        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, phys.x, phys.y, phys.z, t);
        c.SetAnalog(InputSource.Virtual, virt.x, virt.y, virt.z, t);
        c.Tick(0.02, t);
        return t;
    }

    private static readonly (double, double, double) Zero = (0, 0, 0);

    [Fact]
    public void EachSourceAloneMovesTheCamera_WithNoSwitchNeeded()
    {
        var (a, sa) = Make();
        Step(a, T0, (1, 0, 0), Zero);
        Assert.Equal(12, a.SentPan);
        Assert.Equal(InputSource.Fisico, a.InControl);

        var (b, sb) = Make();
        Step(b, T0, Zero, (1, 0, 0));
        Assert.Equal(12, b.SentPan);
        Assert.Equal(InputSource.Virtual, b.InControl);
        Assert.Contains(sb.Sent, x => sb.IsPanTiltMove(x));
    }

    [Fact]
    public void PhysicalFirst_KeepsControl_AndTheVirtualJoystickIsIgnored()
    {
        var (c, _) = Make();
        var t = Step(c, T0, (0.5, 0, 0), Zero);
        int half = c.SentPan;
        Assert.InRange(half, 1, 11);
        t = Step(c, t, (0.5, 0, 0), (1, 0, 0));          // o mouse empurra tudo, mas o controle já estava no comando
        Assert.Equal(half, c.SentPan);
        Assert.Equal(InputSource.Fisico, c.InControl);
    }

    [Fact]
    public void VirtualFirst_KeepsControl_AndThePhysicalStickIsIgnored()
    {
        var (c, _) = Make();
        var t = Step(c, T0, Zero, (0.5, 0, 0));
        int half = c.SentPan;
        Assert.InRange(half, 1, 11);
        Step(c, t, (1, 0, 0), (0.5, 0, 0));
        Assert.Equal(half, c.SentPan);
        Assert.Equal(InputSource.Virtual, c.InControl);
    }

    [Fact]
    public void WhenTheOwnerReturnsToCenter_TheOtherInputTakesOver()
    {
        var (c, _) = Make();
        var t = Step(c, T0, (0.5, 0, 0), Zero);
        t = Step(c, t, (0.5, 0, 0), (1, 0, 0));
        Assert.Equal(InputSource.Fisico, c.InControl);
        Step(c, t, Zero, (1, 0, 0));                      // o controle voltou ao centro: o mouse assume
        Assert.Equal(12, c.SentPan);
        Assert.Equal(InputSource.Virtual, c.InControl);
    }

    [Fact]
    public void WhenBothLeaveTheCenterTogether_ThePhysicalControllerWins()
    {
        var (c, _) = Make();
        Step(c, T0, (0.5, 0, 0), (1, 0, 0));
        Assert.Equal(InputSource.Fisico, c.InControl);
        Assert.InRange(c.SentPan, 1, 11);
    }

    [Fact]
    public void StickDriftInsideTheDeadzone_DoesNotBlockTheVirtualJoystick()
    {
        var (c, _) = Make();
        Step(c, T0, (0.05, 0.03, 0), (1, 0, 0));          // folga do analógico em repouso (zona morta padrão 0,12)
        Assert.Equal(12, c.SentPan);
        Assert.Equal(InputSource.Virtual, c.InControl);
    }

    [Fact]
    public void TheTwoInputsAreNeverSummed()
    {
        var (c, s) = Make();
        Step(c, T0, (1, 0, 0), (-1, 0, 0));                // somando, as duas se cancelariam e a câmera ficaria parada
        var mv = s.Sent.First(b => s.IsPanTiltMove(b));
        Assert.Equal(0x02, mv[6]);                         // direita (a do controle, que estava no comando)
        Assert.Equal(12, mv[4]);
    }

    [Fact]
    public void ZoomIntensity_FollowsWhoIsInControl()
    {
        // o joystick virtual tem intensidade própria (40%); o controle físico não. Com os dois ativos, vale o de quem está no comando.
        byte? Zoom(FakeSender s) => s.Sent.FirstOrDefault(b => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && b[4] != 0x00)?[4];

        var (both, sBoth) = Make(40);
        Step(both, T0, (0, 0, 1), (0, 0, 1));
        var (physOnly, sPhys) = Make(40);
        Step(physOnly, T0, (0, 0, 1), Zero);
        var (virtOnly, sVirt) = Make(40);
        Step(virtOnly, T0, Zero, (0, 0, 1));

        Assert.NotNull(Zoom(sBoth));
        Assert.Equal(Zoom(sPhys), Zoom(sBoth));            // físico no comando: sem o fator do joystick virtual
        Assert.True(Zoom(sVirt) < Zoom(sPhys));            // só o mouse: zoom reduzido pela intensidade do perfil
    }

    [Fact]
    public void StopAndSafetyStop_ClearBothInputsAndTheOwner()
    {
        var (c, s) = Make();
        Step(c, T0, (1, 0, 0), (0, 1, 0));
        Assert.NotNull(c.InControl);
        c.StopAll("teste");
        Assert.Null(c.InControl);
        Assert.Equal(0, c.SentPan);

        Step(c, T0.AddSeconds(1), (1, 0, 0), Zero);
        c.SafetyStop("teste");
        Assert.Null(c.InControl);
        Assert.True(c.SafetyLatched);
    }

    [Fact]
    public void StopsWhenTheOwnerReturnsToCenter_EvenIfTheOtherInputIsStillInsideItsDeadzone()
    {
        var (c, s) = Make();
        var t = Step(c, T0, (1, 0, 0), Zero);
        s.Sent.Clear();
        Step(c, t, Zero, Zero);
        Assert.Contains(s.Sent, b => s.IsPanTiltStop(b));
        Assert.Null(c.InControl);
    }

    // ---------------- a alternância foi removida ----------------
    [Fact]
    public void TheToggleAction_IsNoLongerOfferedOrBoundByDefault()
    {
        Assert.DoesNotContain(ActionCatalog.Todas, t => t.Action == ButtonAction.AlternarEntrada);
        foreach (var p in AppSettings.CreateDefault().Profiles)
            Assert.DoesNotContain(p.Bindings, b => b.Action == ButtonAction.AlternarEntrada);
    }

    [Fact]
    public void OldProfiles_LoseOnlyTheRemovedToggleBinding()
    {
        var p = new Profile();
        p.Bindings.Add(new ButtonBinding { Button = 6, Action = ButtonAction.AlternarEntrada });
        p.Bindings.Add(new ButtonBinding { Button = 1, Action = ButtonAction.Home });
        SettingsStore.MigrateProfile(p);
        Assert.DoesNotContain(p.Bindings, b => b.Action == ButtonAction.AlternarEntrada);
        Assert.Contains(p.Bindings, b => b.Action == ButtonAction.Home);
    }

    [Fact]
    public void OldSettingsFile_WithInputSourceAndTheToggleBinding_StillLoads()
    {
        Directory.CreateDirectory(_dir);
        var st = new SettingsStore(_dir);
        File.WriteAllText(st.FilePath,
            "{\"InputSource\":\"Virtual\",\"ActiveProfile\":\"Igreja\",\"Profiles\":[{\"Name\":\"Igreja\",\"Bindings\":[" +
            "{\"Button\":6,\"Action\":\"AlternarEntrada\"},{\"Button\":1,\"Action\":\"Home\"}]}]}");
        var r = st.Load();
        var p = r.Profiles.First(x => x.Name == "Igreja");
        Assert.DoesNotContain(p.Bindings, b => b.Action == ButtonAction.AlternarEntrada);
        Assert.Contains(p.Bindings, b => b.Action == ButtonAction.Home);
        Assert.DoesNotContain("InputSource", System.Text.Json.JsonSerializer.Serialize(r));   // o campo antigo deixou de existir
    }
}
