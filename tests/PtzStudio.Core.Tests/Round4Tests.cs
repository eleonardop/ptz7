using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>Rodada 4: M2 (zoom por eixo de gatilho usa a mesma normalização) e B1 (alternar perfil exige segurar).</summary>
public class Round4Tests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void TriggerAxisZoom_UsesTheSameNormalizationAsTheButtonPath()
    {
        // eixo combinado (repouso 0,5): metade do curso útil (0,25) deve dar pressão 0,5, não 0,25
        var map = new AxisMap { ZoomAxis = -1, ZoomTeleTriggerAxis = 2 };
        var snap = new InputSnapshot { Axes = new double[8] };
        snap.Axes[2] = 0.75;
        var rest = new double[8]; rest[2] = 0.5;
        var r = InputInterpreter.Interpret(snap, map, rest, Array.Empty<int>());
        Assert.Equal(0.5, r.Z, 3);
    }

    [Fact]
    public void TriggerAxisZoom_IgnoresNoiseNearRest()
    {
        var map = new AxisMap { ZoomAxis = -1, ZoomTeleTriggerAxis = 2 };
        var snap = new InputSnapshot { Axes = new double[8] };
        snap.Axes[2] = 0.01;
        var r = InputInterpreter.Interpret(snap, map, new double[8], Array.Empty<int>());
        Assert.Equal(0, r.Z);
    }

    [Fact]
    public void SwitchProfile_FiresOnlyAfterHold()
    {
        Assert.Contains(ButtonAction.AlternarPerfil, ButtonRouter.HoldToConfirm);
        var p = new Profile();
        p.Bindings.Clear();
        p.Bindings.Add(new ButtonBinding { Button = 7, Action = ButtonAction.AlternarPerfil });
        var r = new ButtonRouter { HoldMs = 500 };
        var fired = new List<ButtonAction>();
        r.Triggered += (a, _) => fired.Add(a);
        r.Update(new[] { 7 }, p, T0);
        r.Update(new[] { 7 }, p, T0.AddMilliseconds(200));
        Assert.Empty(fired);
        r.Update(new[] { 7 }, p, T0.AddMilliseconds(600));
        Assert.Single(fired);
    }
}
