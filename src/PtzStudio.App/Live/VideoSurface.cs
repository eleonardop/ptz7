using System.Windows.Controls;
using System.Windows.Media;

namespace PtzStudio.App.Live;

/// <summary>
/// Mostra os quadros do vídeo. É uma imagem WPF comum (não uma janela nativa), então rola e é recortada junto com a página
/// e aceita elementos por cima.
/// </summary>
internal sealed class VideoSurface : Image
{
    public VideoSurface()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        Loaded += (_, _) =>
        {
            LiveVideo.Instance.FrameChanged += OnFrame;
            Source = LiveVideo.Instance.Frame;
        };
        Unloaded += (_, _) => LiveVideo.Instance.FrameChanged -= OnFrame;
    }

    private void OnFrame() => Source = LiveVideo.Instance.Frame;
}
