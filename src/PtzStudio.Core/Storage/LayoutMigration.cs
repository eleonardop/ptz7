using PtzStudio.Core.Models;

namespace PtzStudio.Core.Storage;

/// <summary>Resultado da conferência entre o controle conectado e o controle para o qual o mapeamento foi feito.</summary>
public enum LayoutDeviceCheck { NotRecorded, Match, Mismatch }

/// <summary>
/// Quando o assistente de mapeamento descobre o número real de um botão, as funções já atribuídas devem continuar
/// no MESMO botão do desenho (por exemplo, "B continua sendo Preset 3"). Os perfis guardam números, não nomes de botão,
/// então os números dos perfis são trocados junto, de uma só vez (trocas entre dois botões também funcionam).
/// </summary>
public static class LayoutMigration
{
    public static void Apply(AppSettings s, IReadOnlyDictionary<string, int> newLayout)
    {
        var map = new Dictionary<int, int>();
        foreach (var kv in newLayout)
        {
            int old = s.Layout.TryGetValue(kv.Key, out var o) ? o : -1;
            if (old >= 0 && kv.Value >= 0 && old != kv.Value) map[old] = kv.Value;
        }
        if (map.Count > 0)
        {
            foreach (var p in s.Profiles)
                foreach (var b in p.Bindings)
                {
                    if (map.TryGetValue(b.Button, out var nb)) b.Button = nb;
                    if (b.Modifier >= 0 && map.TryGetValue(b.Modifier, out var nm)) b.Modifier = nm;
                }
        }
        foreach (var kv in newLayout) s.Layout[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Os números dos botões mudam com o modo de conexão do controle (USB, Bluetooth...). Compara a identificação (VID:PID) do controle
    /// conectado com a do controle para o qual o mapeamento foi feito: se forem diferentes, as funções de botão não devem disparar.
    /// </summary>
    public static LayoutDeviceCheck CheckDevice(AppSettings s, string connectedDeviceKey)
    {
        if (string.IsNullOrEmpty(connectedDeviceKey)) return LayoutDeviceCheck.NotRecorded;
        if (string.IsNullOrEmpty(s.LayoutDevice)) return LayoutDeviceCheck.NotRecorded;
        return string.Equals(s.LayoutDevice, connectedDeviceKey, StringComparison.OrdinalIgnoreCase)
            ? LayoutDeviceCheck.Match : LayoutDeviceCheck.Mismatch;
    }
}
