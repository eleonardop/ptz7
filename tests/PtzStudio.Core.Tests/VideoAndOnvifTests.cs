using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PtzStudio.Core.Models;
using PtzStudio.Core.Onvif;
using PtzStudio.Core.Video;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Endereços RTSP, descoberta ONVIF (com câmera simulada, sem rede) e teste de caminhos RTSP em loopback.</summary>
public class VideoAndOnvifTests
{
    // ---------------- endereços RTSP ----------------
    [Fact]
    public void RtspAddress_ValidatesOnlyRtspUrls()
    {
        Assert.True(RtspAddress.IsValid("rtsp://192.168.1.100/1"));
        Assert.True(RtspAddress.IsValid("  rtsp://192.168.1.100:8554/live/ch0  "));
        Assert.False(RtspAddress.IsValid(""));
        Assert.False(RtspAddress.IsValid(null));
        Assert.False(RtspAddress.IsValid("http://192.168.1.100/1"));
        Assert.False(RtspAddress.IsValid("192.168.1.100"));
    }

    [Fact]
    public void RtspAddress_AddsCredentials_EscapingSpecialCharacters_AndNeverDuplicatesThem()
    {
        Assert.Equal("rtsp://admin:1234@192.168.1.100/1", RtspAddress.WithCredentials("rtsp://192.168.1.100/1", "admin", "1234"));
        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@10.0.0.5:554/x?a=1", RtspAddress.WithCredentials("rtsp://10.0.0.5:554/x?a=1", "admin", "p@ss:w/rd"));
        Assert.Equal("rtsp://u:p@10.0.0.5/1", RtspAddress.WithCredentials("rtsp://u:p@10.0.0.5/1", "admin", "zzz"));   // já tem usuário
        Assert.Equal("rtsp://10.0.0.5/1", RtspAddress.WithCredentials("rtsp://10.0.0.5/1", "", "x"));                 // sem usuário: não muda
    }

    [Fact]
    public void RtspAddress_MasksThePassword()
    {
        Assert.Equal("rtsp://admin:***@10.0.0.5/1", RtspAddress.Mask("rtsp://admin:segredo@10.0.0.5/1"));
        Assert.Equal("rtsp://10.0.0.5/1", RtspAddress.Mask("rtsp://10.0.0.5/1"));
    }

    // ---------------- configuração ----------------
    [Fact]
    public void OldCameraFiles_LoadWithVideoOff()
    {
        var c = JsonSerializer.Deserialize<CameraConfig>("{\"Name\":\"Antiga\",\"IpAddress\":\"10.0.0.9\"}")!;
        Assert.False(c.VideoEnabled);
        Assert.Equal("", c.RtspUrl);
        Assert.Equal("", c.RtspSubUrl);
        Assert.True(c.VideoUsePreviewStream);
        Assert.Equal("Antiga", c.Name);
    }

    [Fact]
    public void VideoSettings_SurviveClone()
    {
        var c = new CameraConfig { VideoEnabled = true, RtspUrl = "rtsp://a/1", RtspSubUrl = "rtsp://a/2", RtspUser = "u", RtspPassword = "p", VideoUsePreviewStream = false };
        var d = c.Clone();
        Assert.True(d.VideoEnabled);
        Assert.Equal("rtsp://a/1", d.RtspUrl);
        Assert.Equal("rtsp://a/2", d.RtspSubUrl);
        Assert.Equal("u", d.RtspUser);
        Assert.Equal("p", d.RtspPassword);
        Assert.False(d.VideoUsePreviewStream);
    }

    // ---------------- ONVIF ----------------
    private const string Ns =
        "xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\" " +
        "xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"";

    private static string Wrap(string body) => $"<s:Envelope {Ns}><s:Body>{body}</s:Body></s:Envelope>";

    private static readonly string DateXml = Wrap(
        "<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:UTCDateTime>" +
        "<tt:Time><tt:Hour>10</tt:Hour><tt:Minute>30</tt:Minute><tt:Second>5</tt:Second></tt:Time>" +
        "<tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>11</tt:Day></tt:Date>" +
        "</tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>");

    private static readonly string CapsXml = Wrap(
        "<tds:GetCapabilitiesResponse><tds:Capabilities><tt:Media><tt:XAddr>http://127.0.0.1/onvif/media_service</tt:XAddr></tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse>");

    private static readonly string ProfilesXml = Wrap(
        "<trt:GetProfilesResponse>" +
        "<trt:Profiles token=\"profile_hi\" fixed=\"true\"><tt:Name>Principal</tt:Name>" +
        "<tt:VideoEncoderConfiguration token=\"v1\"><tt:Name>enc1</tt:Name><tt:Encoding>H264</tt:Encoding>" +
        "<tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration></trt:Profiles>" +
        "<trt:Profiles token=\"profile_lo\" fixed=\"true\"><tt:Name>Secundario</tt:Name>" +
        "<tt:VideoEncoderConfiguration token=\"v2\"><tt:Name>enc2</tt:Name><tt:Encoding>H265</tt:Encoding>" +
        "<tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration></trt:Profiles>" +
        "</trt:GetProfilesResponse>");

    private static string UriXml(string uri) =>
        Wrap($"<trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>{uri}</tt:Uri></trt:MediaUri></trt:GetStreamUriResponse>");

    /// <summary>Câmera ONVIF simulada: responde às quatro chamadas e guarda o que recebeu.</summary>
    private sealed class FakeOnvif : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            string xml =
                body.Contains("GetSystemDateAndTime") ? DateXml :
                body.Contains("GetCapabilities") ? CapsXml :
                body.Contains("GetProfiles") ? ProfilesXml :
                body.Contains("GetStreamUri") ? (body.Contains("profile_hi") ? UriXml("rtsp://127.0.0.1:554/hi") : UriXml("rtsp://127.0.0.1:554/lo")) :
                "";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml") };
        }
    }

    [Fact]
    public async Task Onvif_ReadsProfilesAndStreams_AndPicksMainAndPreview()
    {
        var fake = new FakeOnvif();
        var client = new OnvifClient("admin", "123", 3000, fake);
        var r = await client.ResolveAsync("http://127.0.0.1/onvif/device_service");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Streams.Count);
        Assert.Equal("rtsp://127.0.0.1:554/hi", r.Main!.Uri);
        Assert.Equal(1920, r.Main.Width);
        Assert.Equal("H264", r.Main.Codec);
        Assert.Equal("rtsp://127.0.0.1:554/lo", r.Sub!.Uri);
        Assert.Equal(640, r.Sub.Width);
        Assert.Equal("H265", r.Sub.Codec);
    }

    [Fact]
    public async Task Onvif_SendsLogin_OnlyWhenCredentialsExist_AndNeverOnTheClockQuery()
    {
        var withLogin = new FakeOnvif();
        await new OnvifClient("admin", "123", 3000, withLogin).ResolveAsync("http://127.0.0.1/onvif/device_service");
        Assert.DoesNotContain("UsernameToken", withLogin.Bodies.First(b => b.Contains("GetSystemDateAndTime")));
        Assert.Contains("UsernameToken", withLogin.Bodies.First(b => b.Contains("GetProfiles")));

        var anonymous = new FakeOnvif();
        var r = await new OnvifClient("", "", 3000, anonymous).ResolveAsync("http://127.0.0.1/onvif/device_service");
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain(anonymous.Bodies, b => b.Contains("UsernameToken"));
    }

    [Fact]
    public void Onvif_SecurityHeader_DigestFollowsTheFormula()
    {
        var nonce = Encoding.ASCII.GetBytes("0123456789abcdef");
        var xml = XDocument.Parse(OnvifClient.Envelope("<x/>", OnvifClient.SecurityHeader("admin", "segredo", new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc), nonce)));
        string Get(string name) => xml.Descendants().First(e => e.Name.LocalName == name).Value;

        Assert.Equal("admin", Get("Username"));
        Assert.Equal(Convert.ToBase64String(nonce), Get("Nonce"));
        string created = Get("Created");
        Assert.Equal("2026-11-11T10:00:00.000Z", created);
        var raw = nonce.Concat(Encoding.UTF8.GetBytes(created)).Concat(Encoding.UTF8.GetBytes("segredo")).ToArray();
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(raw)), Get("Password"));
    }

    [Fact]
    public void Onvif_SingleProfile_HasNoSecondaryStream()
    {
        var r = new OnvifResult { Ok = true, Streams = { new OnvifStream("A", "H264", 1920, 1080, "rtsp://x/1") } };
        Assert.NotNull(r.Main);
        Assert.Null(r.Sub);
    }

    [Fact]
    public void Onvif_PreviewPrefersH264OrH265_AndIsNeverTheMainStream()
    {
        var r = new OnvifResult
        {
            Ok = true,
            Streams =
            {
                new OnvifStream("Main", "H265", 2560, 1440, "rtsp://x/main"),
                new OnvifStream("Foto", "JPEG", 320, 180, "rtsp://x/jpeg"),
                new OnvifStream("Sub", "H264", 640, 360, "rtsp://x/sub"),
            }
        };
        Assert.Equal("rtsp://x/main", r.Main!.Uri);
        Assert.Equal("rtsp://x/sub", r.Sub!.Uri);
    }

    [Fact]
    public void Onvif_ParsesFaultsAndDiscoveryAddresses()
    {
        string fault = Wrap("<s:Fault><s:Reason><s:Text xml:lang=\"en\">Sender not Authorized</s:Text></s:Reason></s:Fault>");
        Assert.Equal("Sender not Authorized", OnvifClient.FaultText(fault));
        Assert.Null(OnvifClient.FaultText(ProfilesXml));

        string hello = "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\"><e:Body>" +
                       "<d:ProbeMatches><d:ProbeMatch><d:XAddrs>http://192.168.1.100:8000/onvif/device_service http://[fe80::1]:8000/onvif/device_service</d:XAddrs></d:ProbeMatch></d:ProbeMatches></e:Body></e:Envelope>";
        var addrs = OnvifDiscovery.ParseXAddrs(hello);
        Assert.Equal(2, addrs.Count);
        Assert.Equal("http://192.168.1.100:8000/onvif/device_service", addrs[0]);
        Assert.Empty(OnvifDiscovery.ParseXAddrs("isto não é xml"));
    }

    [Fact]
    public async Task OnvifFinder_RejectsAnInvalidIp()
    {
        var r = await OnvifFinder.FindAsync("abc", null, null);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    // ---------------- caminhos RTSP comuns (servidor simulado em loopback) ----------------
    [Fact]
    public async Task RtspProbe_ReportsOnlyThePathsTheCameraAnswers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var c = await listener.AcceptTcpClientAsync(cts.Token);
                    var s = c.GetStream();
                    var buf = new byte[1024];
                    int n = await s.ReadAsync(buf, 0, buf.Length, cts.Token);
                    string req = Encoding.ASCII.GetString(buf, 0, n);
                    string reply = req.Contains("/stream1") ? "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n" : "RTSP/1.0 404 Not Found\r\nCSeq: 1\r\n\r\n";
                    var b = Encoding.ASCII.GetBytes(reply);
                    await s.WriteAsync(b, 0, b.Length, cts.Token);
                }
                catch { if (cts.IsCancellationRequested) return; }
            }
        });

        var res = await RtspProbe.ProbeAsync("127.0.0.1", port, 1000, new[] { "/nada", "/stream1" });
        cts.Cancel();
        listener.Stop();

        Assert.Single(res);
        Assert.EndsWith("/stream1", res[0].Url);
        Assert.Equal(200, res[0].Status);
    }

    [Fact]
    public async Task RtspProbe_StopsEarly_WhenThePortIsClosed()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int closedPort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();                                                    // ninguém escutando nesta porta
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await RtspProbe.ProbeAsync("127.0.0.1", closedPort, 500);
        Assert.Empty(res);
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public async Task RtspProbe_RejectsInvalidIp()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RtspProbe.ProbeAsync("abc"));
    }
}
