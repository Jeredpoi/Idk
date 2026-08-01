namespace Keyboop.Core.Audio;

/// <summary>Почему запись не пошла в распознавание.</summary>
public enum CaptureRejection
{
    /// <summary>Всё в порядке, можно распознавать.</summary>
    None,

    /// <summary>Слишком короткое нажатие — человек просто задел хоткей.</summary>
    TooShort,

    /// <summary>Микрофон молчал. Сюда же попадает «устройство занято другим приложением».</summary>
    Silence,
}

/// <summary>
/// Гейт перед вызовом whisper. Нужен не ради экономии: на тишине whisper галлюцинирует титрами
/// («Продолжение следует»), поэтому пустое аудио к нему лучше не подпускать вовсе.
/// Пороги перенесены из macOS-версии.
/// </summary>
public static class AudioAnalysis
{
    /// <summary>Короче этого — считаем случайным касанием хоткея, отменяем молча.</summary>
    public const double MinDurationSeconds = 0.3;

    /// <summary>Ниже этого RMS сигнала нет. Порог подобран на живых записях macOS-версии.</summary>
    public const float SilenceRmsThreshold = 0.001f;

    /// <summary>Среднеквадратичный уровень сигнала.</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        // Копим в double: на длинной диктовке (минуты по 16 кГц) float-аккумулятор заметно врёт.
        double sum = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Длительность записи по числу сэмплов.</summary>
    public static double DurationSeconds(int sampleCount, int sampleRate)
    {
        return sampleRate <= 0 ? 0 : (double)sampleCount / sampleRate;
    }

    /// <summary>
    /// Решение «стоит ли звать распознавание». Порядок проверок как в macOS-версии: сначала
    /// длительность (тихая отмена), потом тишина (о ней сообщаем человеку — микрофон мог быть занят).
    /// </summary>
    public static CaptureRejection Evaluate(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (DurationSeconds(samples.Length, sampleRate) < MinDurationSeconds)
        {
            return CaptureRejection.TooShort;
        }

        return Rms(samples) <= SilenceRmsThreshold
            ? CaptureRejection.Silence
            : CaptureRejection.None;
    }
}
