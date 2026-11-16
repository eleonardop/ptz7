using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Home e Salvar preset só disparam depois de segurar o botão; as demais ações disparam na hora.</summary>
public class HoldToConfirmTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static (ButtonRouter r, Profile p, List<(ButtonAction, int)> fired) Make()
    {
        var p = new Profile();
        p.Bindings.Clear();
        p.Bindings.Add(new ButtonBinding { Button = 2, Action = ButtonAction.Home });
        p.Bindings.Add(new ButtonBinding { Button = 3, Action = ButtonAction.SalvarPreset, Parameter = 5 });
        p.Bindings.Add(new ButtonBinding { Button = 4, Action = ButtonAction.AcionarPreset, Parameter = 1 });
        var r = new ButtonRouter { HoldMs = 500 };
        var fired = new List<(ButtonAction, int)>();
        r.Triggered += (a, prm) => fired.Add((a, prm));
        return (r, p, fired);
    }

    [Fact]
    public void Home_FiresOnlyAfterTheButtonIsHeld_AndOnlyOncePerPress()
    {
        var (r, p, fired) = Make();
        r.Update(new[] { 2 }, p, T0);
        r.Update(new[] { 2 }, p, T0.AddMilliseconds(300));
        Assert.Empty(fired);
        Assert.Equal(ButtonAction.Home, r.HoldingAction);
        Assert.InRange(r.HoldProgress, 0.55, 0.65);

        r.Update(new[] { 2 }, p, T0.AddMilliseconds(600));
        Assert.Single(fired);
        r.Update(new[] { 2 }, p, T0.AddMilliseconds(900));
        Assert.Single(fired);                                    // segurar mais não repete
        Assert.Null(r.HoldingAction);

        r.Update(Array.Empty<int>(), p, T0.AddMilliseconds(1000));
        r.Update(new[] { 2 }, p, T0.AddMilliseconds(1100));      // novo toque: precisa segurar de novo
        r.Update(new[] { 2 }, p, T0.AddMilliseconds(1300));
        Assert.Single(fired);
        r.Update(new[] { 2 }, p, T0.AddMilliseconds(1700));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void AQuickTap_NeverFiresHomeOrSavePreset()
    {
        var (r, p, fired) = Make();
        r.Update(new[] { 2, 3 }, p, T0);
        r.Update(new[] { 2, 3 }, p, T0.AddMilliseconds(200));
        r.Update(Array.Empty<int>(), p, T0.AddMilliseconds(250));
        r.Update(Array.Empty<int>(), p, T0.AddMilliseconds(900));
        Assert.Empty(fired);
    }

    [Fact]
    public void SavePreset_AlsoNeedsTheHold_AndKeepsItsParameter()
    {
        var (r, p, fired) = Make();
        r.Update(new[] { 3 }, p, T0);
        r.Update(new[] { 3 }, p, T0.AddMilliseconds(550));
        Assert.Equal((ButtonAction.SalvarPreset, 5), Assert.Single(fired));
    }

    [Fact]
    public void RecallingAPreset_StillFiresInstantly()
    {
        var (r, p, fired) = Make();
        r.Update(new[] { 4 }, p, T0);
        Assert.Equal((ButtonAction.AcionarPreset, 1), Assert.Single(fired));
        r.Update(new[] { 4 }, p, T0.AddMilliseconds(100));
        Assert.Single(fired);                                    // só na borda de subida
    }
}
