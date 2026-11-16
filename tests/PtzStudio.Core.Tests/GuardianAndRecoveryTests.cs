using PtzStudio.Core.Control;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Remetente de teste que também entrega respostas da câmera (ACK, erro) ao controlador.</summary>
public sealed class FakeFeedbackSender : FakeSender, IViscaFeedback
{
    public event Action<ViscaResponse>? ResponseReceived;
    public void Raise(params byte[] bytes) => ResponseReceived?.Invoke(ViscaResponse.Parse(bytes, ViscaTransportMode.UdpPuro));
}

/// <summary>Guardião de parada, parada reafirmada, confirmação e reenvio de comandos únicos, e retomada pelo mouse.</summary>
public class GuardianAndRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private static readonly byte[] Ack = { 0x90, 0x41, 0xFF };

    private static (PtzController c, FakeFeedbackSender s) Make()
    {
        var s = new FakeFeedbackSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = 0; a.NoiseThreshold = 0; }
        p.Motion.PanTiltSpeedPercent = 100;
        var cam = new CameraConfig { CommandIntervalMs = 0 };
        var c = new PtzController(s, () => cam, () => p);
        c.AttachFeedback(s);
        return (c, s);
    }

    private static bool IsRecall(byte[] b) => b.Length == 7 && b[2] == 0x04 && b[3] == 0x3F && b[4] == 0x02;
    private static bool IsHome(byte[] b) => b.Length == 5 && b[2] == 0x06 && b[3] == 0x04;
    private static bool IsZoomStop(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x07 && b[4] == 0x00;
    private static bool IsFocusStop(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x08 && b[4] == 0x00;

    // ---------------- C1: guardião ----------------
    [Fact]
    public void Guardian_StopsAMovingCamera_WhenTheControlLoopFreezes()
    {
        var (c, s) = Make();
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); c.Tick(0.02, T0);
        Assert.Contains(s.Sent, b => s.IsPanTiltMove(b));
        s.Sent.Clear();

        Thread.Sleep(160);                                   // o ciclo de controle "congelou": nenhum Tick novo
        Assert.True(c.GuardianCheck(100));
        Assert.Contains(s.Sent, b => s.IsPanTiltStop(b));
        Assert.Contains(s.Sent, IsZoomStop);
        Assert.Contains(s.Sent, IsFocusStop);
        Assert.False(c.GuardianCheck(100));                  // não repete a cada 100 ms: reforça só a cada 1,5 s
    }

    [Fact]
    public void Guardian_StaysQuiet_WhenTheLoopIsAliveOrTheCameraIsIdle()
    {
        var (c, s) = Make();
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); c.Tick(0.02, T0);
        s.Sent.Clear();
        Assert.False(c.GuardianCheck(5000));                 // o ciclo acabou de rodar

        var (idle, si) = Make();
        idle.Tick(0.02, T0);                                 // nada em movimento
        Thread.Sleep(130);
        Assert.False(idle.GuardianCheck(100));
        Assert.Empty(si.Sent);
    }

    [Fact]
    public void AfterTheGuardianActs_TheCameraOnlyMovesAgainAfterTheInputsReturnToNeutral()
    {
        var (c, s) = Make();
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); c.Tick(0.02, T0);
        Thread.Sleep(160);
        Assert.True(c.GuardianCheck(100));
        s.Sent.Clear();

        var t = T0.AddMilliseconds(40);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t); c.Tick(0.02, t);            // o ciclo volta; o analógico ainda está inclinado
        Assert.True(c.SafetyLatched);
        Assert.DoesNotContain(s.Sent, b => s.IsPanTiltMove(b));

        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, t); c.Tick(0.02, t);            // neutro: libera
        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t); c.Tick(0.02, t);
        Assert.Contains(s.Sent, b => s.IsPanTiltMove(b));
    }

    [Fact]
    public void ForceStopOutOfBand_SendsEveryStopTwice_WithoutNeedingTheControlLoop()
    {
        var (c, s) = Make();
        c.ForceStopOutOfBand("teste");
        Assert.Equal(2, s.Sent.Count(b => s.IsPanTiltStop(b)));
        Assert.Equal(2, s.Sent.Count(IsZoomStop));
        Assert.Equal(2, s.Sent.Count(IsFocusStop));
    }

    // ---------------- C2: parada reafirmada ----------------
    [Fact]
    public void WhenTheCameraComesBack_AnIdleControllerReassertsTheStop()
    {
        var (c, s) = Make();
        c.ReassertStopIfIdle("câmera voltou");
        Assert.Contains(s.Sent, b => s.IsPanTiltStop(b));
        Assert.Contains(s.Sent, IsZoomStop);
        Assert.Contains(s.Sent, IsFocusStop);
    }

    [Fact]
    public void TheReassertedStop_NeverCancelsAMovementOrAPresetTravel()
    {
        var (moving, sm) = Make();
        moving.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); moving.Tick(0.02, T0);
        sm.Sent.Clear();
        moving.ReassertStopIfIdle("x");
        Assert.Empty(sm.Sent);                               // o operador está movendo a câmera

        var (travel, st) = Make();
        travel.RecallPreset(2);
        st.Sent.Clear();
        travel.ReassertStopIfIdle("x");
        Assert.Empty(st.Sent);                               // a câmera está indo para o preset
    }

    // ---------------- A1: confirmação e reenvio ----------------
    [Fact]
    public void PresetRecall_IsResent_WhenTheCameraConfirmsOtherCommandsButNotThisOne()
    {
        var (c, s) = Make();
        c.RecallPreset(3);                                   // 3 paradas + o recall: 4 confirmações esperadas
        Assert.Equal(1, s.Sent.Count(IsRecall));
        s.Raise(Ack);                                        // a câmera confirma comandos, mas só chegou uma confirmação
        Assert.True(c.CameraAcksCommands);

        c.ServicePending(DateTime.UtcNow.AddMilliseconds(400));
        Assert.Equal(2, s.Sent.Count(IsRecall));             // reenviado

        s.Raise(Ack);                                        // agora a confirmação do reenvio chegou
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(1200));
        Assert.Equal(2, s.Sent.Count(IsRecall));             // não reenvia mais
    }

    [Fact]
    public void PresetRecall_IsNotResent_WhenTheCameraNeverConfirmsAnyCommand()
    {
        var (c, s) = Make();
        c.RecallPreset(3);
        Assert.False(c.CameraAcksCommands);
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(400));
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(900));
        Assert.Equal(1, s.Sent.Count(IsRecall));             // câmera que não confirma: nenhum reenvio nem alarme falso
    }

    [Fact]
    public void ResendsStopAfterTwoRetries_AndTheOperatorIsTold()
    {
        var (c, s) = Make();
        var messages = new List<string>();
        c.Message += messages.Add;
        c.RecallPreset(3);
        s.Raise(Ack);
        var t = DateTime.UtcNow.AddMilliseconds(400);
        c.ServicePending(t);
        c.ServicePending(t = t.AddMilliseconds(400));
        c.ServicePending(t = t.AddMilliseconds(400));
        Assert.Equal(3, s.Sent.Count(IsRecall));             // original + 2 reenvios
        Assert.Contains(messages, m => m.Contains("Sem confirmação"));
        c.ServicePending(t.AddMilliseconds(400));
        Assert.Equal(3, s.Sent.Count(IsRecall));             // desistiu
    }

    [Fact]
    public void BufferFullError_ResendsTheCommandQuickly_EvenWithoutAckSupport()
    {
        var (c, s) = Make();
        c.RecallPreset(3);
        s.Raise(0x90, 0x60, 0x03, 0xFF);                     // "memória de comandos cheia"
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(150));
        Assert.Equal(2, s.Sent.Count(IsRecall));
    }

    [Fact]
    public void Home_IsConfirmedAndResentTheSameWay()
    {
        var (c, s) = Make();
        c.Home();
        Assert.Equal(1, s.Sent.Count(IsHome));
        s.Raise(Ack);
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(400));
        Assert.Equal(2, s.Sent.Count(IsHome));
    }

    [Fact]
    public void ManualMovement_CancelsTheResendOfAPreset()
    {
        var (c, s) = Make();
        c.RecallPreset(3);
        s.Raise(Ack);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, T0); c.Tick(0.02, T0);          // o analógico estava solto: a trava do preset sai
        var t1 = T0.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t1); c.Tick(0.02, t1);          // agora o operador assume o movimento
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(400));
        Assert.Equal(1, s.Sent.Count(IsRecall));
    }

    [Fact]
    public void ARefusedCommand_IsReportedOnce_AndCancelledCommandsAreIgnored()
    {
        var (c, s) = Make();
        var messages = new List<string>();
        c.Message += messages.Add;
        s.Raise(0x90, 0x60, 0x41, 0xFF);                     // não executável agora
        s.Raise(0x90, 0x60, 0x41, 0xFF);
        s.Raise(0x90, 0x60, 0x04, 0xFF);                     // cancelado por um comando mais novo: normal
        Assert.Single(messages.Where(m => m.Contains("recusou")));
    }

    // ---------------- A8: o mouse retoma o comando ----------------
    private static DateTime Step(PtzController c, DateTime t, (double x, double y, double z) phys, (double x, double y, double z) virt)
    {
        t = t.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, phys.x, phys.y, phys.z, t);
        c.SetAnalog(InputSource.Virtual, virt.x, virt.y, virt.z, t);
        c.Tick(0.02, t);
        return t;
    }

    [Fact]
    public void ADeliberateMouseClick_TakesOverFromAStuckController()
    {
        var (c, _) = Make();
        var t = Step(c, T0, (0.5, 0, 0), (0, 0, 0));
        Assert.Equal(InputSource.Fisico, c.InControl);

        c.RequestVirtualControl();                           // o operador clicou no joystick virtual
        t = Step(c, t, (0.5, 0, 0), (1, 0, 0));              // o controle físico continua "preso" em 0,5
        Assert.Equal(InputSource.Virtual, c.InControl);
        Assert.Equal(12, c.SentPan);

        t = Step(c, t, (0, 0, 0), (1, 0, 0));                // o físico volta ao centro: a trava é liberada
        t = Step(c, t, (0.5, 0, 0), (1, 0, 0));              // e, com o mouse ainda no comando, o físico não o toma de volta
        Assert.Equal(InputSource.Virtual, c.InControl);
        Assert.Equal(12, c.SentPan);
    }
}
