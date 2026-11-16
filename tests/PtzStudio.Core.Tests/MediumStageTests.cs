using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using PtzStudio.Core.Control;
using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using PtzStudio.Core.Motion;
using PtzStudio.Core.Storage;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Câmera simulada em loopback, com respostas configuráveis (inclusive respostas erradas ou atrasadas de propósito).</summary>
internal sealed class QuickCam : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte[]> _got = new();
    public volatile bool Mute;
    public Func<byte[], byte[][]> Reply = _ => Array.Empty<byte[]>();
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public QuickCam()
    {
        try { _udp.Client.IOControl(-1744830452, new byte[4], null); } catch { }
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                lock (_got) _got.Add(r.Buffer);
                if (Mute) continue;
                foreach (var rep in Reply(r.Buffer)) await _udp.SendAsync(rep, rep.Length, r.RemoteEndPoint);
            }
            catch { if (_cts.IsCancellationRequested) return; }
        }
    }

    public List<byte[]> Received { get { lock (_got) return _got.ToList(); } }

    public async Task<bool> WaitFor(Func<List<byte[]>, bool> cond, int ms = 2500)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond(Received)) return true; await Task.Delay(20); }
        return cond(Received);
    }

    public void Dispose() { _cts.Cancel(); _udp.Close(); }
}

/// <summary>Etapa 2 (Médio): consultas casadas com a resposta certa, detecção de queda mais rápida, sequência VISCA over IP,
/// autofoco com estado real, preset que ganha do analógico, relógio estável, repouso dos gatilhos, exportação sem senha.</summary>
public class MediumStageTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private static readonly byte[] Ack = { 0x90, 0x41, 0xFF };

    // ======================= M2: cada consulta recebe a resposta dela =======================
    [Fact]
    public void AcceptsForInquiry_RejectsOtherShapesAndQueueErrors()
    {
        var value = new ViscaResponse(ViscaResponseKind.Inquerito, new byte[] { 0x90, 0x50, 0x00, 0x00, 0x03, 0x05, 0xFF });
        var onOff = new ViscaResponse(ViscaResponseKind.Inquerito, new byte[] { 0x90, 0x50, 0x02, 0xFF });
        Assert.True(ViscaClient.AcceptsForInquiry(value, -1, ViscaCommands.IsValueReply));
        Assert.False(ViscaClient.AcceptsForInquiry(onOff, -1, ViscaCommands.IsValueReply));     // resposta de outro tipo de consulta
        Assert.True(ViscaClient.AcceptsForInquiry(onOff, -1, ViscaCommands.IsOnOffReply));
        Assert.True(ViscaClient.AcceptsForInquiry(value, -1, null));

        var syntax = new ViscaResponse(ViscaResponseKind.Erro, new byte[] { 0x90, 0x60, 0x02, 0xFF });
        var notExec = new ViscaResponse(ViscaResponseKind.Erro, new byte[] { 0x90, 0x60, 0x41, 0xFF });
        var bufferFull = new ViscaResponse(ViscaResponseKind.Erro, new byte[] { 0x90, 0x60, 0x03, 0xFF });
        var cancelled = new ViscaResponse(ViscaResponseKind.Erro, new byte[] { 0x90, 0x60, 0x04, 0xFF });
        Assert.True(ViscaClient.AcceptsForInquiry(syntax, -1, ViscaCommands.IsValueReply));      // "consulta não suportada"
        Assert.True(ViscaClient.AcceptsForInquiry(notExec, -1, ViscaCommands.IsValueReply));
        Assert.False(ViscaClient.AcceptsForInquiry(bufferFull, -1, ViscaCommands.IsValueReply)); // erro de OUTRO comando
        Assert.False(ViscaClient.AcceptsForInquiry(cancelled, -1, ViscaCommands.IsValueReply));

        var ack = new ViscaResponse(ViscaResponseKind.Ack, Ack);
        Assert.False(ViscaClient.AcceptsForInquiry(ack, -1, null));
    }

    [Fact]
    public void AcceptsForInquiry_OverIp_RequiresTheSameSequenceNumber()
    {
        var payload = new byte[] { 0x90, 0x50, 0x00, 0x00, 0x03, 0x05, 0xFF };
        var mine = new ViscaResponse(ViscaResponseKind.Inquerito, payload, 41u);
        var other = new ViscaResponse(ViscaResponseKind.Inquerito, payload, 40u);
        Assert.True(ViscaClient.AcceptsForInquiry(mine, 41, ViscaCommands.IsValueReply));
        Assert.False(ViscaClient.AcceptsForInquiry(other, 41, ViscaCommands.IsValueReply));
    }

    [Fact]
    public async Task Inquire_IgnoresAReplyOfAnotherShape_AndWaitsForTheRightOne()
    {
        using var cam = new QuickCam();
        cam.Reply = req => req.Length >= 5 && req[1] == 0x09 && req[3] == 0xA1
            ? new[] { new byte[] { 0x90, 0x50, 0x02, 0xFF },                                   // sobra de outra consulta (liga/desliga)
                      new byte[] { 0x90, 0x50, 0x00, 0x00, 0x03, 0x05, 0xFF } }                 // a resposta de brilho (0x35 = 53)
            : Array.Empty<byte[]>();
        using var cl = new ViscaClient();
        cl.Connect(new CameraConfig { IpAddress = "127.0.0.1", Port = cam.Port, HealthCheck = false, TimeoutMs = 1500 });
        var r = await cl.InquireAsync(ViscaCommands.BrightnessInquiry(1), 1500, ViscaCommands.IsValueReply);
        Assert.NotNull(r);
        Assert.True(ViscaCommands.TryReadValue(r!.Payload, out int v));
        Assert.Equal(53, v);
    }

    [Fact]
    public async Task Inquire_OverIp_IgnoresALateReplyWithAnotherSequenceNumber()
    {
        using var cam = new QuickCam();
        cam.Reply = req =>
        {
            if (req.Length < 13 || req[0] != 0x01 || req[1] != 0x10) return Array.Empty<byte[]>();      // só consultas (a mensagem de controle é ignorada)
            uint seq = ViscaFraming.ReadSequence(req)!.Value;
            return new[]
            {
                ViscaFraming.Wrap(new byte[] { 0x90, 0x50, 0x00, 0x00, 0x07, 0x07, 0xFF }, seq + 100, ViscaPayloadType.Reply),   // atrasada, de outra consulta
                ViscaFraming.Wrap(new byte[] { 0x90, 0x50, 0x00, 0x00, 0x02, 0x00, 0xFF }, seq, ViscaPayloadType.Reply)          // a certa (0x20 = 32)
            };
        };
        using var cl = new ViscaClient();
        cl.Connect(new CameraConfig { IpAddress = "127.0.0.1", Port = cam.Port, Transport = ViscaTransportMode.ViscaOverIp, HealthCheck = false, TimeoutMs = 1500 });
        var r = await cl.InquireAsync(ViscaCommands.BrightnessInquiry(1), 1500, ViscaCommands.IsValueReply);
        Assert.NotNull(r);
        Assert.True(ViscaCommands.TryReadValue(r!.Payload, out int v));
        Assert.Equal(32, v);
    }

    // ======================= M14: sequência do VISCA over IP =======================
    [Fact]
    public void ViscaOverIp_ControlMessage_And_SequenceHelpers()
    {
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01 }, ViscaFraming.ResetSequence());
        var data = ViscaFraming.Wrap(new byte[] { 0x90, 0x41, 0xFF }, 77, ViscaPayloadType.Reply);
        Assert.Equal(77u, ViscaFraming.ReadSequence(data));
        var r = ViscaResponse.Parse(data, ViscaTransportMode.ViscaOverIp);
        Assert.Equal(ViscaResponseKind.Ack, r.Kind);
        Assert.Equal(77u, r.Seq);
        Assert.Null(ViscaResponse.Parse(new byte[] { 0x90, 0x41, 0xFF }, ViscaTransportMode.UdpPuro).Seq);
    }

    [Fact]
    public async Task ViscaOverIp_ConnectingSendsTheSequenceReset_UdpPuroDoesNot()
    {
        using var ip = new QuickCam();
        using var cl = new ViscaClient();
        cl.Connect(new CameraConfig { IpAddress = "127.0.0.1", Port = ip.Port, Transport = ViscaTransportMode.ViscaOverIp, HealthCheck = false });
        Assert.True(await ip.WaitFor(r => r.Any(b => b.SequenceEqual(ViscaFraming.ResetSequence()))));
        cl.Send(ViscaCommands.PanTiltStop(1));
        Assert.True(await ip.WaitFor(r => r.Any(b => b.Length == 17 && b[0] == 0x01 && b[1] == 0x00)));      // o comando seguinte é um pacote de comando normal

        using var plain = new QuickCam();
        using var cl2 = new ViscaClient();
        cl2.Connect(new CameraConfig { IpAddress = "127.0.0.1", Port = plain.Port, Transport = ViscaTransportMode.UdpPuro, HealthCheck = false });
        cl2.Send(ViscaCommands.PanTiltStop(1));
        Assert.True(await plain.WaitFor(r => r.Count >= 1));
        Assert.DoesNotContain(plain.Received, b => b.Length > 0 && b[0] == 0x02);
    }

    // ======================= M3: a queda aparece depressa =======================
    [Fact]
    public void EffectiveAttempts_NeverPassesThree()
    {
        Assert.Equal(3, ViscaClient.EffectiveAttempts(new CameraConfig { ReconnectAttempts = 20 }));
        Assert.Equal(3, ViscaClient.EffectiveAttempts(new CameraConfig { ReconnectAttempts = 5 }));
        Assert.Equal(1, ViscaClient.EffectiveAttempts(new CameraConfig { ReconnectAttempts = 0 }));
        Assert.Equal(2, ViscaClient.EffectiveAttempts(new CameraConfig { ReconnectAttempts = 2 }));
        var c = new CameraConfig { ReconnectAttempts = 99, PresetSlotMax = 999 };
        SettingsSanitizer.Camera(c);
        Assert.Equal(3, c.ReconnectAttempts);
        Assert.Equal(254, c.PresetSlotMax);
    }

    [Fact]
    public async Task ADeadCamera_IsReportedAsDisconnectedInSeconds_NotTwentySeconds()
    {
        using var cam = new QuickCam();
        cam.Reply = req => req.Length >= 5 && req[1] == 0x09 ? new[] { new byte[] { 0x90, 0x50, 0x00, 0x20, 0xFF } } : Array.Empty<byte[]>();
        using var cl = new ViscaClient();
        cl.Connect(new CameraConfig { IpAddress = "127.0.0.1", Port = cam.Port, HealthCheck = true, TimeoutMs = 300, ReconnectAttempts = 5 });
        Assert.True(await cl.TestConnectionAsync());

        cam.Mute = true;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 14000 && cl.State != ConnectionState.Desconectado) await Task.Delay(50);
        Assert.Equal(ConnectionState.Desconectado, cl.State);
        Assert.True(sw.ElapsedMilliseconds < 11000, $"levou {sw.ElapsedMilliseconds} ms para marcar a câmera como desconectada");
    }

    // ======================= M4: inversão passa por zero =======================
    [Fact]
    public void ReversingTheStick_RampsThroughZero_InsteadOfJumpingFromPlusToMinus()
    {
        var ch = new AxisChannel();
        var s = new AxisSettings { AccelPerSecond = 0, DecelPerSecond = 100 };
        Assert.Equal(10, ch.Step(10, 0.016, s));
        double prev = 10; bool crossedZero = false;
        for (int i = 0; i < 40; i++)
        {
            double v = ch.Step(-10, 0.016, s);
            Assert.False(prev > 0 && v < 0, "a velocidade pulou de positiva para negativa sem passar por zero");
            if (v == 0) crossedZero = true;
            prev = v;
        }
        Assert.True(crossedZero);
        Assert.Equal(-10, ch.Current);
    }

    [Fact]
    public void ReversingTheStick_WithoutRamp_StaysInstant()
    {
        var ch = new AxisChannel();
        var s = new AxisSettings { AccelPerSecond = 0, DecelPerSecond = 0 };
        ch.Step(10, 0.016, s);
        Assert.Equal(-10, ch.Step(-10, 0.016, s));       // o usuário escolheu "sem rampa"
    }

    // ======================= M5: preset ganha do analógico =======================
    private static (PtzController c, FakeFeedbackSender s) MakeCtl(Action<CameraConfig>? tweak = null)
    {
        var s = new FakeFeedbackSender();
        var p = new Profile();
        foreach (var a in new[] { p.Motion.Pan, p.Motion.Tilt, p.Motion.Zoom }) { a.AccelPerSecond = 0; a.DecelPerSecond = 0; a.NoiseThreshold = 0; }
        p.Motion.PanTiltSpeedPercent = 100;
        var cam = new CameraConfig { CommandIntervalMs = 0 };
        tweak?.Invoke(cam);
        var c = new PtzController(s, () => cam, () => p);
        c.AttachFeedback(s);
        return (c, s);
    }

    [Fact]
    public void AHeldStick_DoesNotCutThePresetTravel_UntilItReturnsToCenter()
    {
        var (c, s) = MakeCtl();
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); c.Tick(0.02, T0);
        Assert.Contains(s.Sent, b => s.IsPanTiltMove(b));
        c.RecallPreset(3);
        s.Sent.Clear();

        var t1 = T0.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t1); c.Tick(0.02, t1);          // o analógico continua inclinado
        Assert.DoesNotContain(s.Sent, b => s.IsPanTiltMove(b));
        Assert.True(c.WaitingNeutralAfterPreset);
        Assert.False(c.SafetyLatched);                                           // não é parada de segurança
        Assert.Equal("preset 3", c.Traveling);

        var t2 = t1.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 0, 0, 0, t2); c.Tick(0.02, t2);          // soltou
        Assert.False(c.WaitingNeutralAfterPreset);
        var t3 = t2.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t3); c.Tick(0.02, t3);          // de novo: agora o operador assume
        Assert.Contains(s.Sent, b => s.IsPanTiltMove(b));
        Assert.Null(c.Traveling);
    }

    [Fact]
    public void Home_AlsoWinsOverAHeldStick()
    {
        var (c, s) = MakeCtl();
        c.Home();
        s.Sent.Clear();
        var t = T0.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, t);                              // o analógico está inclinado quando o Home está em curso
        c.Tick(0.02, t);
        Assert.DoesNotContain(s.Sent, b => s.IsPanTiltMove(b));
        Assert.Equal("Home", c.Traveling);
    }

    [Fact]
    public void ANormalStickDrift_DoesNotKeepTheSafetyStopLatched()
    {
        var (c, _) = MakeCtl();
        c.SetAnalog(InputSource.Fisico, 1, 0, 0, T0); c.Tick(0.02, T0);
        c.SafetyStop("teste");
        Assert.True(c.SafetyLatched);
        var t = T0.AddMilliseconds(20);
        c.SetAnalog(InputSource.Fisico, 0.05, 0.03, 0, t); c.Tick(0.02, t);      // folga normal, dentro da zona morta
        Assert.False(c.SafetyLatched);
    }

    // ======================= M8: número de preset da câmera =======================
    [Fact]
    public void PresetNumbersAboveTheCameraLimit_AreRefusedWithAMessage_AndNothingIsSent()
    {
        var (c, s) = MakeCtl(cam => cam.PresetSlotMax = 15);
        var msgs = new List<string>(); c.Message += msgs.Add;
        Assert.False(c.RecallPreset(20));
        Assert.False(c.SavePreset(16));
        Assert.Empty(s.Sent);
        Assert.Contains(msgs, m => m.Contains("fora da faixa"));
        Assert.True(c.SavePreset(15));
        Assert.Contains(s.Sent, b => b.Length == 7 && b[3] == 0x3F && b[4] == 0x01 && b[5] == 15);
        Assert.True(c.RecallPreset(15));
    }

    [Fact]
    public void WhenTheCameraRefusesAPreset_TheOperatorIsToldAndItIsNotResent()
    {
        var (c, s) = MakeCtl();
        var msgs = new List<string>(); c.Message += msgs.Add;
        c.RecallPreset(30);
        s.Raise(0x90, 0x60, 0x02, 0xFF);                                          // erro de sintaxe: número que a câmera não tem
        Assert.Contains(msgs, m => m.Contains("RECUSOU o preset 30"));
        c.ServicePending(DateTime.UtcNow.AddMilliseconds(600));
        Assert.Equal(1, s.Sent.Count(b => b.Length == 7 && b[4] == 0x02));        // um só recall: sem reenvio

        c.SavePreset(31);
        s.Raise(0x90, 0x60, 0x41, 0xFF);
        Assert.Contains(msgs, m => m.Contains("RECUSOU gravar o preset 31"));
    }

    // ======================= foco pelos botões da tela =======================
    [Fact]
    public void OnScreenFocusButtons_DriveTheFocus_AndStopWhenReleased()
    {
        var (c, s) = MakeCtl();
        c.SetVirtualFocus(1); c.Tick(0.02, T0);
        Assert.Contains(s.Sent, b => b.Length == 6 && b[3] == 0x08 && b[4] == 0x02);
        s.Sent.Clear();
        c.SetVirtualFocus(0); c.Tick(0.02, T0.AddMilliseconds(20));
        Assert.Contains(s.Sent, b => b.Length == 6 && b[3] == 0x08 && b[4] == 0x00);
    }

    // ======================= M7 e autofoco =======================
    private sealed class FakeFocusCamera : IViscaInquirer
    {
        public readonly List<byte[]> Sent = new();
        public volatile bool Answers = true;
        public volatile bool Ignore;
        public volatile bool Auto;
        public ConnectionState State => ConnectionState.Conectado;
        public void Send(byte[] payload)
        {
            lock (Sent) Sent.Add(payload);
            if (!Ignore && payload.Length == 6 && payload[2] == 0x04 && payload[3] == 0x38) Auto = payload[4] == 0x02;
        }
        public Task<ViscaResponse?> InquireAsync(byte[] inquiry, int timeoutMs = 0, Func<byte[], bool>? accept = null)
        {
            if (!Answers) return Task.FromResult<ViscaResponse?>(null);
            return Task.FromResult<ViscaResponse?>(new ViscaResponse(ViscaResponseKind.Inquerito, new byte[] { 0x90, 0x50, (byte)(Auto ? 0x02 : 0x03), 0xFF }));
        }
        public List<byte[]> Snapshot() { lock (Sent) return Sent.ToList(); }
    }

    private static bool IsAutoFocus(byte[] b, bool on) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x38 && b[4] == (on ? 0x02 : 0x03);
    private static bool IsOnePush(byte[] b) => b.Length == 6 && b[2] == 0x04 && b[3] == 0x18 && b[4] == 0x01;

    [Fact]
    public void FocusCommands_AreBuiltAsInTheVISCASpecification()
    {
        Assert.Equal(new byte[] { 0x81, 0x01, 0x04, 0x18, 0x01, 0xFF }, ViscaCommands.FocusOnePush(1));
        Assert.Equal(new byte[] { 0x81, 0x09, 0x04, 0x38, 0xFF }, ViscaCommands.FocusModeInquiry(1));
        Assert.Equal(new byte[] { 0x81, 0x01, 0x04, 0x38, 0x02, 0xFF }, ViscaCommands.AutoFocus(1, true));
        Assert.Equal(new byte[] { 0x81, 0x01, 0x04, 0x38, 0x03, 0xFF }, ViscaCommands.AutoFocus(1, false));
        Assert.True(ViscaCommands.IsOnOffReply(new byte[] { 0x90, 0x50, 0x02, 0xFF }));
        Assert.False(ViscaCommands.IsOnOffReply(new byte[] { 0x90, 0x50, 0x00, 0x00, 0x03, 0x05, 0xFF }));
        Assert.True(ViscaCommands.IsValueReply(new byte[] { 0x90, 0x50, 0x00, 0x00, 0x03, 0x05, 0xFF }));
    }

    [Fact]
    public async Task Toggle_UsesTheRealStateOfTheCamera_NotAPresumedOne()
    {
        // a câmera JÁ está em autofoco (alguém usou o controle remoto dela): o primeiro toque deve DESLIGAR, não "ligar de novo"
        var cam = new FakeFocusCamera { Auto = true };
        var f = new CameraFocus(cam, () => new CameraConfig());
        var mode = await f.ToggleAsync();
        Assert.Equal(FocusMode.Manual, mode);
        Assert.Contains(cam.Snapshot(), b => IsAutoFocus(b, false));
        Assert.DoesNotContain(cam.Snapshot(), b => IsAutoFocus(b, true));

        var cam2 = new FakeFocusCamera { Auto = false };
        var f2 = new CameraFocus(cam2, () => new CameraConfig());
        Assert.Equal(FocusMode.Automatico, await f2.ToggleAsync());
        Assert.Contains(cam2.Snapshot(), b => IsAutoFocus(b, true));
    }

    [Fact]
    public async Task RefreshReadsTheCameraMode_AndMarksItAsConfirmed()
    {
        var cam = new FakeFocusCamera { Auto = true };
        var f = new CameraFocus(cam, () => new CameraConfig());
        Assert.Equal(FocusMode.Desconhecido, f.Mode);
        Assert.True(await f.RefreshAsync());
        Assert.Equal(FocusMode.Automatico, f.Mode);
        Assert.True(f.Confirmed);
        f.Reset();
        Assert.Equal(FocusMode.Desconhecido, f.Mode);
        Assert.False(f.Confirmed);
    }

    [Fact]
    public async Task WhenTheCameraDoesNotAnswerTheInquiry_TheToggleStillWorks_ButIsMarkedUnconfirmed()
    {
        var cam = new FakeFocusCamera { Answers = false };
        var f = new CameraFocus(cam, () => new CameraConfig());
        var mode = await f.ToggleAsync();
        Assert.Equal(FocusMode.Automatico, mode);
        Assert.False(f.Confirmed);
        Assert.Contains(cam.Snapshot(), b => IsAutoFocus(b, true));
    }

    [Fact]
    public async Task OnePush_SwitchesToManualFirst_ThenTriggersTheFocus()
    {
        var cam = new FakeFocusCamera { Auto = true };
        var f = new CameraFocus(cam, () => new CameraConfig());
        var notes = new List<string>(); f.Notice += notes.Add;
        await f.OnePushAsync();
        var sent = cam.Snapshot();
        int iManual = sent.FindIndex(b => IsAutoFocus(b, false));
        int iPush = sent.FindIndex(IsOnePush);
        Assert.True(iManual >= 0 && iPush > iManual, "o foco manual deve vir antes do foco de um toque");
        Assert.Equal(FocusMode.Manual, f.Mode);
        Assert.Contains(notes, n => n.Contains("um toque"));
    }

    [Fact]
    public async Task IfTheCameraIgnoresTheChange_TheOperatorIsTold()
    {
        var cam = new FakeFocusCamera { Auto = false, Ignore = true };
        var f = new CameraFocus(cam, () => new CameraConfig());
        var told = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Notice += m => told.TrySetResult(m);
        f.SetAuto(true);                                                           // pede autofoco; a câmera não muda
        var done = await Task.WhenAny(told.Task, Task.Delay(3000));
        Assert.Same(told.Task, done);
        Assert.Contains("não aceitou", told.Task.Result);
    }

    [Fact]
    public void TheOneTouchFocusButton_FiresOncePerPress()
    {
        var router = new ButtonRouter();
        var p = new Profile();
        p.Bindings.Add(new ButtonBinding { Button = 5, Action = ButtonAction.FocoUmToque });
        var fired = new List<ButtonAction>();
        router.Triggered += (a, _) => fired.Add(a);
        router.Update(new[] { 5 }, p); router.Update(new[] { 5 }, p);
        Assert.Single(fired);
        router.Update(Array.Empty<int>(), p); router.Update(new[] { 5 }, p);
        Assert.Equal(2, fired.Count);
        Assert.Contains(ActionCatalog.Todas, t => t.Action == ButtonAction.FocoUmToque);
    }

    // ======================= M10: relógio que só avança =======================
    [Fact]
    public void MonoClock_OnlyMovesForward()
    {
        var a = MonoClock.UtcNow;
        Thread.Sleep(30);
        var b = MonoClock.UtcNow;
        Assert.True(b > a);
        Assert.True((b - a).TotalMilliseconds < 2000);
        Assert.True(Math.Abs((MonoClock.UtcNow - DateTime.UtcNow).TotalSeconds) < 5);
    }

    // ======================= M17: repouso dos eixos =======================
    private static double[] Axes(double z = 0.5) { var a = new double[8]; a[0] = a[1] = 0.5; a[2] = z; return a; }

    [Fact]
    public void Rest_IsOnlyRecordedAfterTheAxesStayStill()
    {
        var t = new RestTracker();
        Assert.False(t.Offer(Axes(), T0));
        Assert.False(t.Offer(Axes(), T0.AddMilliseconds(100)));
        Assert.False(t.Ready);
        Assert.False(t.Offer(Axes(0.9), T0.AddMilliseconds(200)));                // mexeu: recomeça a contar
        Assert.False(t.Offer(Axes(0.9), T0.AddMilliseconds(400)));
        Assert.True(t.Offer(Axes(0.9), T0.AddMilliseconds(520)));
        Assert.True(t.Ready);
        Assert.Equal(0.9, t.Rest[2]);
    }

    [Fact]
    public void ATriggerHeldWhenReconnecting_DoesNotBecomeTheRest()
    {
        var t = new RestTracker();
        t.Offer(Axes(0.5), T0); t.Offer(Axes(0.5), T0.AddMilliseconds(400));
        Assert.Equal(0.5, t.Rest[2]);

        t.Reset();                                                                 // controle reconectado (o mesmo)
        t.Offer(Axes(0.95), T0.AddSeconds(5)); t.Offer(Axes(0.95), T0.AddSeconds(5.5));
        Assert.True(t.Ready);
        Assert.Equal(0.5, t.Rest[2]);                                              // vale o repouso anterior
        Assert.NotNull(t.Warning);

        t.ForgetHistory();                                                         // o operador pediu para recalibrar
        t.Offer(Axes(0.95), T0.AddSeconds(8)); t.Offer(Axes(0.95), T0.AddSeconds(8.5));
        Assert.Equal(0.95, t.Rest[2]);
        Assert.Null(t.Warning);
    }

    // ======================= M12: exportação sem a senha do vídeo =======================
    [Fact]
    public void ExportAndBackup_CanLeaveTheVideoPasswordOut()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ptz-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = AppSettings.CreateDefault();
            s.Cameras[0].RtspPassword = "segredo123";
            store.Save(s);
            Assert.True(SettingsStore.HasPasswords(s));

            string without = Path.Combine(dir, "sem.json"), with = Path.Combine(dir, "com.json");
            store.ExportAll(s, without, includePasswords: false);
            store.ExportAll(s, with);
            Assert.DoesNotContain("segredo123", File.ReadAllText(without));
            Assert.Contains("segredo123", File.ReadAllText(with));
            Assert.Equal("segredo123", s.Cameras[0].RtspPassword);                  // as configurações em uso não mudam

            string zipPath = store.CreateBackup(Path.Combine(dir, "sem.zip"), includePasswords: false);
            using (var z = ZipFile.OpenRead(zipPath))
            using (var rd = new StreamReader(z.GetEntry("settings.json")!.Open()))
                Assert.DoesNotContain("segredo123", rd.ReadToEnd());

            string zipWith = store.CreateBackup(Path.Combine(dir, "com.zip"));
            using (var z = ZipFile.OpenRead(zipWith))
            using (var rd = new StreamReader(z.GetEntry("settings.json")!.Open()))
                Assert.Contains("segredo123", rd.ReadToEnd());

            Assert.False(SettingsStore.HasPasswords(SettingsStore.WithoutPasswords(s)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
