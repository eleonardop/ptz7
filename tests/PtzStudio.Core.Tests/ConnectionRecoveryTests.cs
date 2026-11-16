using System.Net;
using System.Net.Sockets;
using PtzStudio.Core.Models;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Cliente VISCA que se recupera sozinho: socket recriado, laço de recepção resistente a assinantes com defeito.</summary>
public class ConnectionRecoveryTests
{
    /// <summary>Câmera simulada em loopback: responde às consultas (a menos que esteja "muda") e guarda o que recebeu.</summary>
    private sealed class LoopCamera : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly object _l = new();
        private readonly List<byte[]> _got = new();
        public volatile bool Mute;
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public LoopCamera()
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
                    lock (_l) _got.Add(r.Buffer);
                    if (!Mute && r.Buffer.Length >= 5 && r.Buffer[1] == 0x09)
                    {
                        byte[] reply = { 0x90, 0x50, 0x00, 0x20, 0xFF };
                        await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                    }
                }
                catch { if (_cts.IsCancellationRequested) return; }
            }
        }

        public List<byte[]> Received { get { lock (_l) return _got.ToList(); } }

        public async Task<bool> WaitFor(Func<List<byte[]>, bool> cond, int ms = 2500)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (cond(Received)) return true; await Task.Delay(20); }
            return cond(Received);
        }

        public void Dispose() { _cts.Cancel(); _udp.Close(); }
    }

    private static CameraConfig Cfg(LoopCamera cam, bool health = false) => new()
    {
        IpAddress = "127.0.0.1", Port = cam.Port, HealthCheck = health, TimeoutMs = 300, ReconnectAttempts = 5
    };

    private static bool IsMove(byte[] b) => b.Length == 9 && b[2] == 0x06 && b[3] == 0x01 && !(b[6] == 0x03 && b[7] == 0x03);

    [Fact]
    public async Task TheSocketCanBeRecreated_AndTheClientKeepsWorking()
    {
        using var cam = new LoopCamera();
        using var cl = new ViscaClient();
        cl.Connect(Cfg(cam));
        Assert.True(await cl.TestConnectionAsync());

        cl.ForceReconnect();
        Assert.True(cl.RebuildCount >= 1);
        Assert.True(await cl.TestConnectionAsync());               // a câmera responde para a nova porta local
        cl.Send(ViscaCommands.PanTiltDrive(1, 5, 5, 24, 20));
        Assert.True(await cam.WaitFor(r => r.Any(IsMove)));
    }

    [Fact]
    public async Task AFaultySubscriber_NeverKillsTheReceiveLoop()
    {
        using var cam = new LoopCamera();
        using var cl = new ViscaClient();
        int received = 0;
        cl.ResponseReceived += _ => Interlocked.Increment(ref received);
        cl.ResponseReceived += _ => throw new InvalidOperationException("assinante com defeito");
        cl.StateChanged += (_, _) => throw new InvalidOperationException("assinante de estado com defeito");
        cl.Connect(Cfg(cam));

        Assert.True(await cl.TestConnectionAsync());
        Assert.True(await cl.TestConnectionAsync());               // o laço continua vivo depois da exceção
        Assert.True(Volatile.Read(ref received) >= 2);
        for (int i = 0; i < 50 && cl.State != ConnectionState.Conectado; i++) await Task.Delay(20);
        Assert.Equal(ConnectionState.Conectado, cl.State);
    }

    [Fact]
    public async Task WhenTheCameraStopsAnswering_TheClientRecreatesTheConnection_AndRecoversOnItsOwn()
    {
        using var cam = new LoopCamera();
        using var cl = new ViscaClient();
        cl.Connect(Cfg(cam, health: true));
        Assert.True(await cl.TestConnectionAsync());

        cam.Mute = true;                                           // a câmera "some": nenhuma resposta
        for (int i = 0; i < 450 && cl.RebuildCount < 1; i++) await Task.Delay(20);       // até ~9 s
        Assert.True(cl.RebuildCount >= 1, "o socket deveria ser recriado depois de falhas seguidas");

        cam.Mute = false;                                          // a câmera volta
        for (int i = 0; i < 400 && cl.State != ConnectionState.Conectado; i++) await Task.Delay(20);   // até ~8 s
        Assert.Equal(ConnectionState.Conectado, cl.State);
    }

    [Fact]
    public async Task AnOldHealthLoop_DoesNotTouchTheNewConnection()
    {
        using var cam = new LoopCamera();
        using var cl = new ViscaClient();
        cl.Connect(Cfg(cam, health: true));
        cl.Connect(Cfg(cam, health: true));                        // reconectar: o laço anterior deve se encerrar sozinho
        Assert.True(await cl.TestConnectionAsync());
        await Task.Delay(1800);
        Assert.NotEqual(ConnectionState.Desconectado, cl.State);
    }
}
