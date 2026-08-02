using System.Media;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Audio;

/// <summary>
/// Звуковые метки: короткие тоны, которые Keyboop издаёт сам.
///
/// ⚠️ ЕДИНСТВЕННАЯ ДВЕРЬ, ЧЕРЕЗ КОТОРУЮ ПРОГРАММА ЗВУЧИТ. В macOS-версии этот класс появился
/// после отчёта, где человек всё сделал правильно — выключил звук в настройках, увёл громкость
/// в ноль — и всё равно его слышал: помимо настраиваемых звуков движок в четырёх местах звал
/// системный сигнал напрямую, а тот не смотрит ни на какие наши тумблеры. Настройка, у которой
/// нет власти над всем звуком, хуже отсутствующей. Поэтому звучать можно только отсюда.
///
/// Звук синтезируется в памяти, а не лежит файлом: ни ресурсов в сборке, ни обращений куда бы
/// то ни было. Системные сигналы Windows тоже не годятся — «Asterisk» означает для человека
/// ошибку, а исправленная раскладка ошибкой не является.
/// </summary>
internal static class Cues
{
    private const int SampleRate = 44100;

    /// <summary>Общий выключатель. Пока он выключен, не звучит ничего вообще.</summary>
    internal static bool Enabled { get; set; }

    // Плееры держим полями, а не создаём на каждый звук. Причина не в экономии: Play()
    // асинхронный, и плеер из локальной переменной успевал бы освободиться раньше, чем звук
    // доиграет. В macOS-версии это выглядело как «при быстром наборе второй звук иногда не
    // срабатывает» и разбиралось отдельным отчётом.
    private static readonly SoundPlayer ToCyrillic = Make(392, 523);   // соль → до, вверх
    private static readonly SoundPlayer ToLatin = Make(523, 392);      // до → соль, вниз
    private static readonly SoundPlayer Start = Make(330, 440);        // вверх: «пишу»
    private static readonly SoundPlayer Stop = Make(440, 587);         // ещё выше: «записал»

    /// <summary>Раскладка исправлена. Направление слышно: вверх — в русский, вниз — в латиницу.</summary>
    internal static void Converted(bool toCyrillic) => Play(toCyrillic ? ToCyrillic : ToLatin);

    internal static void RecordingStarted() => Play(Start);

    internal static void RecordingStopped() => Play(Stop);

    private static void Play(SoundPlayer player)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            // ⚠️ Именно Play, а не PlaySync. Зовут нас в том числе с потока перехватчика, а он
            // обязан вернуться за миллисекунды: подождать здесь даже сотню миллисекунд означает
            // потерять перехватчик по таймауту.
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Нет звуковой карты, устройство занято, сеанс без звука. Молчание — приемлемый
            // исход для звуковой метки; ронять из-за неё программу — нет.
            Log.Write($"звук: не проигрался — {ex.GetType().Name}");
        }
    }

    /// <summary>Два коротких тона подряд: «тук-тук» заданными нотами.</summary>
    private static SoundPlayer Make(double firstHz, double secondHz)
    {
        var samples = new List<short>();
        AppendTone(samples, firstHz);
        AppendSilence(samples, 0.025);
        AppendTone(samples, secondHz);

        return new SoundPlayer(new MemoryStream(Wav(samples)));
    }

    /// <summary>
    /// Один тон: синус с мягкой атакой и быстрым спадом.
    ///
    /// Атака нужна обязательно — тон, начатый с полной амплитуды, даёт щелчок вместо ноты.
    /// Спад делает звук перкуссивным «туком», а не гудком: метка должна отмечать событие,
    /// а не привлекать к себе внимание.
    /// </summary>
    private static void AppendTone(List<short> output, double hertz)
    {
        const double Duration = 0.045;
        const double Attack = 0.006;
        const double Amplitude = 0.35;

        var count = (int)(SampleRate * Duration);

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var attack = Math.Min(1.0, t / Attack);
            var decay = Math.Exp(-t / (Duration * 0.4));
            var value = Math.Sin(2 * Math.PI * hertz * t) * attack * decay * Amplitude;

            output.Add((short)(Math.Clamp(value, -1, 1) * short.MaxValue));
        }
    }

    private static void AppendSilence(List<short> output, double seconds)
    {
        for (var i = 0; i < (int)(SampleRate * seconds); i++)
        {
            output.Add(0);
        }
    }

    /// <summary>Обернуть отсчёты в WAV: 16 бит, моно. Самый простой формат, который принимает SoundPlayer.</summary>
    private static byte[] Wav(IReadOnlyList<short> samples)
    {
        var dataBytes = samples.Count * sizeof(short);

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                             // размер блока fmt
        writer.Write((short)1);                       // PCM без сжатия
        writer.Write((short)1);                       // моно
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));     // байт в секунду
        writer.Write((short)sizeof(short));           // выравнивание блока
        writer.Write((short)16);                      // бит на отсчёт

        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
