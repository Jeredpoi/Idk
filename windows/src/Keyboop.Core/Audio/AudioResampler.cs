namespace Keyboop.Core.Audio;

/// <summary>
/// Приведение захваченного аудио к формату, которого требует whisper: 16 кГц, моно, float32.
///
/// ПОЧЕМУ РЕСЕМПЛИМ САМИ. На macOS формат просто ОБЪЯВЛЯЛСЯ системе, и она отдавала ровно
/// 16 кГц моно. В Windows так нельзя: WASAPI в разделяемом режиме отдаёт микрофон в том формате,
/// в котором устройство уже открыто микшером (обычно 44.1 или 48 кГц, часто стерео), и навязать
/// свой формат означало бы либо эксклюзивный режим (тогда микрофон отбирается у Zoom и браузера),
/// либо отказ на части устройств. Поэтому берём что дают и приводим сами — здесь, в коде без
/// зависимостей от ОС, который можно покрыть тестами.
/// </summary>
public static class AudioResampler
{
    /// <summary>Частота, которую ждёт whisper. Модель обучена на ней, менять нельзя.</summary>
    public const int TargetSampleRate = 16000;

    /// <summary>
    /// Свести чередующиеся каналы в моно простым усреднением.
    /// </summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            var baseIndex = i * channels;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[baseIndex + c];
            }

            mono[i] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Линейная интерполяция до целевой частоты.
    ///
    /// Линейной достаточно: речь в 16 кГц занимает полосу до 8 кГц, а понижение с 44.1/48 кГц
    /// такой полосе вредит незначительно — whisper устойчив к этому классу искажений. Полосовой
    /// фильтр с окном дал бы формально чище, но заметной разницы в распознавании не даёт, а цена
    /// — лишняя зависимость на горячем пути.
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> mono, int sourceRate, int targetRate = TargetSampleRate)
    {
        if (sourceRate <= 0 || targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate), "Частота дискретизации должна быть положительной.");
        }

        if (mono.Length == 0)
        {
            return [];
        }

        if (sourceRate == targetRate)
        {
            return mono.ToArray();
        }

        // Длину считаем в long: на многоминутной диктовке произведение переполняет int.
        var targetLength = (int)((long)mono.Length * targetRate / sourceRate);
        if (targetLength <= 0)
        {
            return [];
        }

        var result = new float[targetLength];
        var ratio = (double)(mono.Length - 1) / Math.Max(1, targetLength - 1);

        for (var i = 0; i < targetLength; i++)
        {
            var position = i * ratio;
            var left = (int)position;
            var right = Math.Min(left + 1, mono.Length - 1);
            var fraction = (float)(position - left);
            result[i] = mono[left] + ((mono[right] - mono[left]) * fraction);
        }

        return result;
    }

    /// <summary>
    /// Полный путь: чередующиеся каналы любой частоты → моно 16 кГц.
    /// </summary>
    public static float[] ToWhisperFormat(ReadOnlySpan<float> interleaved, int channels, int sourceRate)
    {
        var mono = DownmixToMono(interleaved, channels);
        return Resample(mono, sourceRate);
    }
}
