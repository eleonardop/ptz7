using System.Windows;
using System.Windows.Controls;
using PtzStudio.App.Engine;
using PtzStudio.App.Ui;
using PtzStudio.Core.Models;

namespace PtzStudio.App.Tabs;

/// <summary>
/// "Movimento": todos os ajustes numéricos de Pan, Tilt e Zoom (curva, zona morta, velocidades, suavidade).
/// A velocidade Pan/Tilt do perfil (1% a 100%) fica na aba Controle PTZ.
/// </summary>
internal sealed class MotionTab : TabBase
{
    public MotionTab(AppEngine e) : base(e) { Init(); }

    protected override void Build()
    {
        var m = E.Profile.Motion;
        var reset = Ux.Btn("Restaurar padrões deste perfil", () =>
        {
            var r = MessageBox.Show(
                $"Voltar os ajustes de movimento do perfil “{E.Profile.Name}” aos valores de fábrica?\n\n" +
                "Pan, Tilt, Zoom e a velocidade Pan/Tilt do perfil serão restaurados. Botões, eixos e presets não mudam.",
                "Restaurar padrões", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            E.RestoreMotionDefaults();
            Rebuild();
        }, 300);

        var how = Ux.Card(Ux.Text(
            "Como a velocidade é calculada: a inclinação do analógico é multiplicada pela sensibilidade, desconta-se a zona morta, aplica-se a curva " +
            "e o resultado vira uma velocidade entre a mínima e a máxima do eixo. Em Pan e Tilt entra ainda a velocidade do perfil (1% a 100%, na aba Controle PTZ) e, " +
            "por fim, o limite da câmera (aba Câmera). O Zoom não é afetado pela velocidade Pan/Tilt. Com a curva Progressiva, 10–20% de inclinação gera movimento " +
            "muito lento e 100% chega à velocidade máxima configurada.", 13, false, Ux.Muted),
            "COMO OS AJUSTES SE COMBINAM");

        Content = Ux.Scroll(Ux.V(
            Ux.Wrap(reset),
            Ux.Group("Pan (horizontal)", new AxisSettingsPanel(m.Pan, 24, E.Save)),
            Ux.Group("Tilt (vertical)", new AxisSettingsPanel(m.Tilt, 20, E.Save)),
            Ux.Group("Zoom progressivo", Ux.V(
                new AxisSettingsPanel(m.Zoom, 8, E.Save),
                Ux.Text("O zoom usa velocidade variável do VISCA (8 níveis). Soltar o controle envia parada imediata, a menos que “Parada suave” esteja marcada.", 13, false, Ux.Muted))),
            Ux.Group("Reduzir Pan/Tilt com o zoom", Ux.V(
                Ux.Check("Reduzir a velocidade de Pan/Tilt conforme o zoom fecha", m.ReducePanTiltWithZoom, v => { m.ReducePanTiltWithZoom = v; E.Save(); }),
                Ux.SliderRow("Velocidade que sobra no zoom máximo (%)", 5, 100, m.ZoomReductionMinPercent, v => { m.ZoomReductionMinPercent = (int)Math.Round(v); E.Save(); }, "0"),
                Ux.Text("A posição do zoom é lida da própria câmera. Com o zoom aberto a velocidade não muda; a redução cresce aos poucos e chega ao valor acima " +
                        "no zoom máximo. A mesma opção aparece na aba Controle PTZ. Se a câmera não informar a posição do zoom, nada é reduzido. " +
                        "O movimento nunca para por causa disso: a câmera tem uma velocidade mínima (1).", 13, false, Ux.Muted))),
            how));
        Ux.WithHeader((ScrollViewer)Content, "Movimento",
            "Curva, zona morta, velocidade e suavidade de Pan, Tilt e Zoom. Os valores valem para o perfil ativo.");
    }
}
