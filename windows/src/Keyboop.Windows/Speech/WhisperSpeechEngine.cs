using System.Text;
using Keyboop.Core.Speech;
using Keyboop.Windows.Diagnostics;
using Whisper.net;

namespace Keyboop.Windows.Speech;

/// <summary>
/// Адаптер Whisper.net: применяет профиль декодирования из ядра к настоящему движку.
///
/// Здесь нет ни одного «магического» параметра: каждый пришёл из <see cref="WhisperDecodeProfile"/>,
/// где к нему написано, почему он такой. Так набор настроек нельзя изменить случайно — правка
/// значения без правки объяснения сразу видна в ревью.
/// </summary>
internal sealed class WhisperSpeechEngine : IDisposable
{
    /// <summary>
    /// Распознавание строго по одному за раз. Процессор Whisper.net не реентерабелен, а вторая
    /// диктовка вполне может начаться, пока первая ещё считается.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    internal bool IsModelLoaded => _factory is not null;

    /// <summary>Модель прямо сейчас читается с диска.</summary>
    internal bool IsLoading { get; private set; }

    /// <summary>
    /// Загрузить модель. Операция дорогая (файл от 140 МБ до 1,6 ГБ), поэтому держим её загруженной
    /// между диктовками и перезагружаем только при смене файла.
    ///
    /// ⚠️ АСИНХРОННО, И ЭТО НЕ УКРАШЕНИЕ. Чтение полутора гигабайт занимает секунды, а вызывают
    /// нас с потока интерфейса — того самого, на котором висит перехватчик клавиатуры. Занятый
    /// поток не успевает обработать колбэк, Windows молча снимает перехватчик по таймауту, и для
    /// человека это выглядит как «после выбора модели перестали работать хоткеи».
    ///
    /// ⚠️ Через тот же семафор, что и распознавание. Без него смена модели могла бы освободить
    /// фабрику посреди чужой диктовки — то есть уронить процесс в неуправляемом коде.
    /// </summary>
    internal async Task LoadModelAsync(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Файл модели не найден.", modelPath);
        }

        if (_loadedModelPath == modelPath && _factory is not null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        IsLoading = true;

        try
        {
            await Task.Run(() =>
            {
                _factory?.Dispose();
                _factory = null;
                _loadedModelPath = null;

                var started = DateTime.UtcNow;
                var factory = WhisperFactory.FromPath(modelPath);

                _factory = factory;
                _loadedModelPath = modelPath;

                Log.Write($"whisper: модель загружена за {(DateTime.UtcNow - started).TotalMilliseconds:F0} мс "
                          + $"({new FileInfo(modelPath).Length / (1024 * 1024)} МБ)");
            }).ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Распознать аудио (16 кГц моно float32) и вернуть текст уже без эха затравки.
    /// Оформлением вывода занимается вызывающий через <see cref="TranscriptPostProcessor"/>.
    /// </summary>
    internal async Task<string> TranscribeAsync(
        float[] samples, WhisperDecodeProfile profile, CancellationToken cancellationToken = default)
    {
        if (_factory is null)
        {
            Log.Write("whisper: модель не загружена — распознавание пропущено");
            return string.Empty;
        }

        if (samples.Length == 0)
        {
            return string.Empty;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var started = DateTime.UtcNow;
            await using var processor = BuildProcessor(profile);

            var builder = new StringBuilder();
            await foreach (var segment in processor
                               .ProcessAsync(samples, cancellationToken)
                               .ConfigureAwait(false))
            {
                builder.Append(segment.Text);
            }

            var raw = builder.ToString();
            var text = PunctuationPrompt.StripEcho(raw, profile.Prompt);

            // В лог — только измеримое: длина, число знаков препинания, время. Содержимое не пишем.
            Log.Write($"whisper: {text.Length} симв., пунктуация={TranscriptPostProcessor.CountPunctuation(text)}, "
                      + $"язык={profile.Language}, {(DateTime.UtcNow - started).TotalMilliseconds:F0} мс");

            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperProcessor BuildProcessor(WhisperDecodeProfile profile)
    {
        var builder = _factory!.CreateBuilder()
            .WithLanguage(profile.Language)
            .WithThreads(profile.Threads)
            .WithTemperature(profile.Temperature)
            .WithTemperatureInc(profile.TemperatureIncrement)
            .WithEntropyThreshold(profile.EntropyThreshold)
            .WithNoSpeechThreshold(profile.NoSpeechThreshold)
            .WithPrintTimestamps(false);

        if (profile.NoContext)
        {
            builder = builder.WithNoContext();
        }

        if (profile.SingleSegment)
        {
            builder = builder.WithSingleSegment();
        }

        // ВОТ РАДИ ЭТИХ ДВУХ СТРОК ВСЁ И ЗАТЕВАЛОСЬ.
        // Затравка удерживает whisper в режиме с пунктуацией, а перенос затравки не даёт знакам
        // пропасть на втором и следующих окнах длинной диктовки. Без них примерно треть реплик
        // приходит сплошняком, без единого знака.
        if (!string.IsNullOrEmpty(profile.Prompt))
        {
            builder = builder
                .WithPrompt(profile.Prompt)
                .WithCarryInitialPrompt(profile.CarryInitialPrompt);
        }

        return builder.Build();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}
