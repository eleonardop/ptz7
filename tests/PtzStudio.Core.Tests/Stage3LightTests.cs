using PtzStudio.Core.Models;
using PtzStudio.Core.Storage;
using PtzStudio.Core.Visca;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Etapa 3 (Leve): campos de vídeo nulos, porta extra de sondagem.</summary>
public class Stage3LightTests
{
    [Fact]
    public void Sanitizer_NormalizesNullVideoFields_FromAHandEditedFile()
    {
        var c = new CameraConfig { RtspUrl = null!, RtspSubUrl = null!, RtspUser = null!, RtspPassword = null! };
        SettingsSanitizer.Camera(c);
        Assert.Equal("", c.RtspUrl);
        Assert.Equal("", c.RtspSubUrl);
        Assert.Equal("", c.RtspUser);
        Assert.Equal("", c.RtspPassword);

        var d = new CameraConfig { RtspUrl = "  rtsp://10.0.0.5/1  ", RtspPassword = " com espaço " };
        SettingsSanitizer.Camera(d);
        Assert.Equal("rtsp://10.0.0.5/1", d.RtspUrl);
        Assert.Equal(" com espaço ", d.RtspPassword);                          // a senha nunca é alterada
    }

    [Fact]
    public void ProbePorts_IncludeTheUsersOwnPort_WithoutDuplicates()
    {
        Assert.Equal(new[] { 1259, 52381 }, CameraProbe.PortsWith(1259));
        Assert.Equal(new[] { 1259, 52381, 5678 }, CameraProbe.PortsWith(5678));
        Assert.Equal(new[] { 1259, 52381 }, CameraProbe.PortsWith(0));
    }
}
