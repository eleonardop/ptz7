using System.Diagnostics;

namespace PtzStudio.Core;

/// <summary>
/// Relógio que só avança: mede intervalos (watchdog, tempo de espera, repetições) sem ser enganado por ajuste do horário do
/// Windows (sincronização NTP, mudança de fuso, horário de verão). Devolve um DateTime em UTC para ser usado onde o código
/// já esperava um DateTime, mas só diferenças entre dois valores dele têm significado.
/// </summary>
public static class MonoClock
{
    private static readonly DateTime Origin = DateTime.UtcNow;
    private static readonly long Start = Stopwatch.GetTimestamp();

    public static DateTime UtcNow => Origin + Stopwatch.GetElapsedTime(Start);
}
