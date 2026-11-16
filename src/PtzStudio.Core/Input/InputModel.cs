using PtzStudio.Core.Models;

namespace PtzStudio.Core.Input;

/// <summary>Leitura bruta de um joystick (DirectInput). Eixos normalizados 0..1; POV em centésimos de grau (-1 = centro).</summary>
public sealed class InputSnapshot
{
    public bool[] Buttons { get; set; } = Array.Empty<bool>();
    public double[] Axes { get; set; } = new double[8];
    public int[] Pov { get; set; } = { -1, -1, -1, -1 };
    public static readonly string[] AxisNames = { "X", "Y", "Z", "Rot X", "Rot Y", "Rot Z", "Slider 1", "Slider 2" };
}

/// <summary>
/// Identificadores de "botão" usados nos mapeamentos:
/// 0..127 = botões físicos; 1000..1003 = direcional (cima, direita, baixo, esquerda);
/// 2000+eixo = eixo empurrado para o lado positivo; 3000+eixo = lado negativo (gatilhos analógicos).
/// </summary>
public static class VirtualButtons
{
    public const int DpadUp = 1000, DpadRight = 1001, DpadDown = 1002, DpadLeft = 1003;
    public static int AxisPlus(int axis) => 2000 + axis;
    public static int AxisMinus(int axis) => 3000 + axis;

    public static string Describe(int id)
    {
        if (id < 0) return "não detectado";
        if (id < 128) return $"Botão {id}";
        return id switch
        {
            DpadUp => "Direcional ↑", DpadRight => "Direcional →", DpadDown => "Direcional ↓", DpadLeft => "Direcional ←",
            >= 2000 and < 3000 => $"Eixo {InputSnapshot.AxisNames[Math.Clamp(id - 2000, 0, 7)]} (+)",
            >= 3000 and < 4000 => $"Eixo {InputSnapshot.AxisNames[Math.Clamp(id - 3000, 0, 7)]} (−)",
            _ => $"Entrada {id}"
        };
    }
}

public sealed class InterpretedInput
{
    public double X { get; set; }   // direita +
    public double Y { get; set; }   // cima +
    public double Z { get; set; }   // aproximar +
    public HashSet<int> Pressed { get; } = new();
    /// <summary>
    /// Pressão (0..1) das entradas de eixo observadas (gatilhos analógicos): 0 = solto, 1 = apertado até o fim. É a distância do
    /// repouso em relação ao curso disponível naquele sentido, então vale tanto para gatilhos em eixos separados (repouso 0)
    /// quanto para LT e RT juntos num só eixo (repouso no meio).
    /// </summary>
    public Dictionary<int, double> Analog { get; } = new();
}

public static class InputInterpreter
{
    public const double AxisPressThreshold = 0.35;

    /// <summary>Abaixo disto o gatilho conta como solto (ruído de leitura do potenciômetro), como fração do curso.</summary>
    public const double AnalogFloor = 0.03;

    /// <summary>Pressão 0..1 de uma entrada de eixo: deslocamento do repouso dividido pelo curso que existe naquele sentido.</summary>
    public static double TriggerAmount(double delta, bool plusSide, double rest)
    {
        double travel = plusSide ? 1.0 - rest : rest;            // curso disponível entre o repouso e o fim, no sentido do gatilho
        travel = Math.Clamp(travel, 0.2, 1.0);                   // evita dividir por quase nada se o repouso estiver no extremo
        double a = (plusSide ? delta : -delta) / travel;
        return a < AnalogFloor ? 0 : Math.Min(1.0, a);
    }

    public static InterpretedInput Interpret(InputSnapshot s, AxisMap map, double[] rest, IEnumerable<int> watchedVirtual)
    {
        var r = new InterpretedInput();
        for (int i = 0; i < s.Buttons.Length; i++) if (s.Buttons[i]) r.Pressed.Add(i);

        if (s.Pov.Length > 0 && s.Pov[0] >= 0)
        {
            int a = s.Pov[0];
            if (a >= 31500 || a <= 4500) r.Pressed.Add(VirtualButtons.DpadUp);
            if (a >= 4500 && a <= 13500) r.Pressed.Add(VirtualButtons.DpadRight);
            if (a >= 13500 && a <= 22500) r.Pressed.Add(VirtualButtons.DpadDown);
            if (a >= 22500 && a <= 31500) r.Pressed.Add(VirtualButtons.DpadLeft);
        }

        foreach (int id in watchedVirtual)
        {
            if (id < 2000) continue;
            int axis = id % 1000;
            if (axis >= s.Axes.Length) continue;
            double rv = axis < rest.Length ? rest[axis] : 0;
            double delta = s.Axes[axis] - rv;
            if (id < 3000 ? delta > AxisPressThreshold : delta < -AxisPressThreshold) r.Pressed.Add(id);
            double amount = TriggerAmount(delta, id < 3000, rv);
            if (amount > 0) r.Analog[id] = amount;
        }

        double Norm(int i) => i < 0 || i >= s.Axes.Length ? 0 : (s.Axes[i] - 0.5) * 2.0;
        double Trig(int i)
        {
            if (i < 0 || i >= s.Axes.Length) return 0;
            double rv = i < rest.Length ? rest[i] : 0;
            return Math.Clamp(s.Axes[i] - rv, 0, 1);
        }

        r.X = Norm(map.PanAxis) * (map.PanAxisReversed ? -1 : 1);
        r.Y = -Norm(map.TiltAxis) * (map.TiltAxisReversed ? -1 : 1);                    // em DirectInput, empurrar para cima dá valor menor
        if (map.FullPanTilt && map.LimitDiagonal)
        {
            // um só analógico: o vetor (X, Y) nunca passa de raio 1, então a diagonal respeita os limites de velocidade
            double mag = Math.Sqrt(r.X * r.X + r.Y * r.Y);
            if (mag > 1) { r.X /= mag; r.Y /= mag; }
        }
        r.Z = -Norm(map.ZoomAxis) + Trig(map.ZoomTeleTriggerAxis) - Trig(map.ZoomWideTriggerAxis);
        r.Z = Math.Clamp(r.Z, -1, 1);
        return r;
    }
}

public sealed class ButtonRouter
{
    /// <summary>
    /// Ações de efeito visível que só disparam depois de o botão ficar apertado por HoldMs: um toque acidental não manda a
    /// câmera ao Home nem sobrescreve um preset. As demais ações disparam na hora.
    /// </summary>
    public static readonly HashSet<ButtonAction> HoldToConfirm = new() { ButtonAction.Home, ButtonAction.SalvarPreset };

    private HashSet<(int, int, ButtonAction)> _prevActive = new();
    private readonly Dictionary<(int, int, ButtonAction), DateTime> _holdSince = new();
    private readonly HashSet<(int, int, ButtonAction)> _holdFired = new();

    public event Action<ButtonAction, int>? Triggered;
    public int HeldPan { get; private set; }
    public int HeldTilt { get; private set; }
    public int HeldZoom { get; private set; }
    public int HeldFocus { get; private set; }
    /// <summary>
    /// Zoom por gatilho analógico (RT/LT mapeados em Aproximar/Afastar): −1..+1 proporcional à pressão. Zero ao soltar.
    /// Os botões comuns continuam em <see cref="HeldZoom"/> (liga/desliga).
    /// </summary>
    public double HeldZoomAnalog { get; private set; }
    /// <summary>Brilho / exposição da imagem: -1, 0 ou +1 enquanto o botão estiver segurado.</summary>
    public int HeldBright { get; private set; }
    public int HeldExposure { get; private set; }
    /// <summary>Tempo que o botão de uma ação "segurar para confirmar" precisa ficar apertado.</summary>
    public int HoldMs { get; set; } = 500;
    /// <summary>Ação "segurar para confirmar" que está sendo segurada agora (nulo se nenhuma) e o quanto já foi (0..1).</summary>
    public ButtonAction? HoldingAction { get; private set; }
    public double HoldProgress { get; private set; }

    public void Update(IReadOnlyCollection<int> pressed, Profile profile, DateTime? nowUtc = null, IReadOnlyDictionary<int, double>? analog = null)
    {
        var clock = nowUtc ?? MonoClock.UtcNow;
        var now = new HashSet<int>(pressed);
        var active = new List<ButtonBinding>();
        double zoomAnalog = 0;
        foreach (var b in profile.Bindings)
        {
            if (b.Action == ButtonAction.Nenhuma) continue;
            // Gatilho analógico mapeado como zoom: a velocidade acompanha a pressão (não é liga/desliga). Sem leitura analógica
            // disponível (analog == null) cai no comportamento de botão comum.
            if (analog != null && b.Button >= 2000 && (b.Action == ButtonAction.ZoomAproximar || b.Action == ButtonAction.ZoomAfastar))
            {
                if (b.Modifier >= 0 && !now.Contains(b.Modifier)) continue;
                if (b.Modifier < 0 && profile.Bindings.Any(o => o.Modifier >= 0 && o.Button == b.Button &&
                        o.Action != ButtonAction.Nenhuma && now.Contains(o.Modifier))) continue;
                if (analog.TryGetValue(b.Button, out var amt))
                    zoomAnalog += (b.Action == ButtonAction.ZoomAproximar ? 1 : -1) * amt;
                continue;
            }
            if (!now.Contains(b.Button)) continue;
            if (b.Modifier >= 0 && !now.Contains(b.Modifier)) continue;
            // Uma combinação ativa tem prioridade sobre o botão isolado.
            if (b.Modifier < 0 && profile.Bindings.Any(o => o.Modifier >= 0 && o.Button == b.Button &&
                    o.Action != ButtonAction.Nenhuma && now.Contains(o.Modifier))) continue;
            active.Add(b);
        }

        int pan = 0, tilt = 0, zoom = 0, focus = 0, bright = 0, expo = 0;
        double holdProg = 0; ButtonAction? holdAct = null;
        var keys = new HashSet<(int, int, ButtonAction)>();
        foreach (var b in active)
        {
            var key = (b.Button, b.Modifier, b.Action);
            keys.Add(key);
            switch (b.Action)
            {
                case ButtonAction.PanEsquerda: pan -= 1; break;
                case ButtonAction.PanDireita: pan += 1; break;
                case ButtonAction.TiltCima: tilt += 1; break;
                case ButtonAction.TiltBaixo: tilt -= 1; break;
                case ButtonAction.ZoomAproximar: zoom += 1; break;
                case ButtonAction.ZoomAfastar: zoom -= 1; break;
                case ButtonAction.FocoDistante: focus += 1; break;
                case ButtonAction.FocoProximo: focus -= 1; break;
                case ButtonAction.BrilhoMais: bright += 1; break;
                case ButtonAction.BrilhoMenos: bright -= 1; break;
                case ButtonAction.ExposicaoMais: expo += 1; break;
                case ButtonAction.ExposicaoMenos: expo -= 1; break;
                default:
                    if (HoldToConfirm.Contains(b.Action))
                    {
                        if (!_holdSince.TryGetValue(key, out var t0)) { t0 = clock; _holdSince[key] = t0; }
                        double prog = (clock - t0).TotalMilliseconds / Math.Max(1, HoldMs);
                        if (prog >= 1) { if (_holdFired.Add(key)) Triggered?.Invoke(b.Action, b.Parameter); }
                        else if (prog > holdProg) { holdProg = prog; holdAct = b.Action; }
                    }
                    else if (!_prevActive.Contains(key)) Triggered?.Invoke(b.Action, b.Parameter);
                    break;
            }
        }
        foreach (var k in _holdSince.Keys.Where(k => !keys.Contains(k)).ToList()) { _holdSince.Remove(k); _holdFired.Remove(k); }
        _prevActive = keys;
        HoldProgress = holdProg; HoldingAction = holdAct;
        HeldPan = Math.Clamp(pan, -1, 1); HeldTilt = Math.Clamp(tilt, -1, 1);
        HeldZoomAnalog = Math.Clamp(zoomAnalog, -1, 1);
        HeldZoom = Math.Clamp(zoom, -1, 1); HeldFocus = Math.Clamp(focus, -1, 1);
        HeldBright = Math.Clamp(bright, -1, 1); HeldExposure = Math.Clamp(expo, -1, 1);
    }
}

public static class BindingValidator
{
    /// <summary>Retorna uma atribuição já existente para o mesmo botão (e mesmo modificador), se houver.</summary>
    public static ButtonBinding? FindConflict(Profile p, int button, int modifier) =>
        p.Bindings.FirstOrDefault(b => b.Button == button && b.Modifier == modifier && b.Action != ButtonAction.Nenhuma);
}

/// <summary>
/// Posição de repouso dos eixos (usada pelos gatilhos analógicos e pelos "botões de eixo"). Só é gravada depois que todos os
/// eixos ficam parados por um instante, e uma leitura que salta muito em relação à anterior do MESMO controle (gatilho apertado
/// na hora de ligar ou reconectar) é descartada: vale a anterior, e o operador é avisado.
/// </summary>
public sealed class RestTracker
{
    /// <summary>Quanto tempo os eixos precisam ficar parados para o repouso ser gravado.</summary>
    public double StableSeconds { get; set; } = 0.3;
    /// <summary>Variação máxima entre duas leituras seguidas para os eixos contarem como "parados".</summary>
    public double Tolerance { get; set; } = 0.01;
    /// <summary>Se o repouso novo difere do anterior (mesmo controle) por mais que isto, ele é descartado.</summary>
    public double JumpLimit { get; set; } = 0.25;

    private double[]? _cand;
    private DateTime _candSince;
    private double[]? _lastGood;

    public double[] Rest { get; private set; } = new double[8];
    /// <summary>Verdadeiro quando o repouso já foi gravado (até lá, nenhum "botão de eixo" deve disparar).</summary>
    public bool Ready { get; private set; }
    /// <summary>Aviso para o operador quando o repouso lido foi descartado (nulo se tudo normal).</summary>
    public string? Warning { get; private set; }

    /// <summary>Pede um novo repouso (controle reconectado, "Recalibrar"). O repouso anterior continua guardado para comparação.</summary>
    public void Reset() { Ready = false; _cand = null; Warning = null; }

    /// <summary>Esquece também o repouso anterior (outro controle foi conectado: os valores não são comparáveis).</summary>
    public void ForgetHistory() { Reset(); _lastGood = null; }

    private static double MaxDiff(double[] a, double[] b)
    {
        double m = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    /// <summary>Oferece uma leitura dos eixos. Devolve verdadeiro quando o repouso está pronto.</summary>
    public bool Offer(double[] axes, DateTime now)
    {
        if (Ready) return true;
        if (_cand == null || MaxDiff(_cand, axes) > Tolerance)
        {
            _cand = (double[])axes.Clone(); _candSince = now;
            return false;
        }
        if ((now - _candSince).TotalSeconds < StableSeconds) return false;

        if (_lastGood != null && MaxDiff(_lastGood, _cand) > JumpLimit)
        {
            Rest = (double[])_lastGood.Clone();
            Warning = "Um gatilho ou eixo parecia apertado quando o controle foi conectado; o repouso anterior foi mantido. Se os gatilhos não responderem, use Diagnóstico > Recalibrar repouso dos eixos com tudo solto.";
        }
        else
        {
            Rest = (double[])_cand.Clone();
            Warning = null;
        }
        _lastGood = (double[])Rest.Clone();
        Ready = true;
        return true;
    }
}
