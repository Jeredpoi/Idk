using Keyboop.Core.Audio;
using Keyboop.Windows.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Keyboop.Windows.Audio;

/// <summary>
/// Запись с микрофона через WASAPI в разделяемом режиме.
///
/// ПОЧЕМУ РАЗДЕЛЯЕМЫЙ, А НЕ ЭКСКЛЮЗИВНЫЙ. Эксклюзивный режим позволил бы попросить у устройства
/// сразу 16 кГц моно, как это делает macOS-версия. Но он ОТБИРАЕТ микрофон у всех остальных: у
/// человека на созвоне диктовка выключила бы микрофон в Zoom. Поэтому берём разделяемый, принимаем
/// формат микшера какой есть и приводим его к 16 кГц сами (<see cref="AudioResampler"/>).
/// </summary>
internal sealed class MicrophoneRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly List<float> _samples = [];

    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private DateTime _startedAt;

    internal bool IsRecording { get; private set; }

    /// <summary>
    /// Идентификатор микрофона. Пусто — как решит Windows.
    /// Меняется из настроек и подхватывается со следующей записи, без перезапуска.
    /// </summary>
    internal string DeviceId { get; set; } = AudioDevices.SystemDefault;

    /// <summary>Уровень сигнала для индикатора (0…1). Считается по последнему буферу.</summary>
    internal event Action<float>? LevelChanged;

    internal void Start()
    {
        lock (_gate)
        {
            if (IsRecording)
            {
                return;
            }

            _samples.Clear();

            // Устройство разрешаем на КАЖДОЙ записи, а не один раз при запуске: микрофоны
            // подключают и отключают, и закэшированный дескриптор пережил бы своё устройство.
            //
            // ⚠️ Освобождать его здесь НЕЛЬЗЯ, хотя рука тянется: WasapiCapture держит устройство
            // всё время записи, и освобождённый дескриптор оборвал бы запись на первом же буфере.
            // Владельцем становится capture, он же его и закроет.
            var device = AudioDevices.Resolve(DeviceId);
            var capture = device is null ? new WasapiCapture() : new WasapiCapture(device);
            _format = capture.WaveFormat;
            capture.DataAvailable += OnDataAvailable;

            _capture = capture;
            IsRecording = true;
            _startedAt = DateTime.UtcNow;

            capture.StartRecording();

            Log.Write($"запись: старт, формат устройства {_format.SampleRate} Гц, "
                      + $"{_format.Channels} кан., {_format.BitsPerSample} бит");
        }
    }

    /// <summary>
    /// Остановить запись и отдать аудио в формате whisper: 16 кГц моно float32.
    /// Возвращает пустой массив, если запись не шла.
    /// </summary>
    internal float[] Stop()
    {
        WasapiCapture? capture;
        float[] raw;
        WaveFormat? format;

        lock (_gate)
        {
            if (!IsRecording)
            {
                return [];
            }

            IsRecording = false;
            capture = _capture;
            _capture = null;
            format = _format;
            raw = _samples.ToArray();
            _samples.Clear();
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
        }

        if (format is null || raw.Length == 0)
        {
            return [];
        }

        // Ресемплим ОДИН раз в конце, а не по буферам: интерполяция по кускам даёт щелчки на
        // границах, а на речи это слышно и мешает распознаванию.
        var result = AudioResampler.ToWhisperFormat(raw, format.Channels, format.SampleRate);

        Log.Write($"запись: стоп, {result.Length} сэмплов, "
                  + $"{AudioAnalysis.DurationSeconds(result.Length, AudioResampler.TargetSampleRate):F1} с");

        return result;
    }

    /// <summary>Отменить запись, ничего не возвращая.</summary>
    internal void Cancel()
    {
        _ = Stop();
    }

    internal double ElapsedSeconds =>
        IsRecording ? (DateTime.UtcNow - _startedAt).TotalSeconds : 0;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _format;
        if (format is null || e.BytesRecorded == 0)
        {
            return;
        }

        var block = Decode(e.Buffer, e.BytesRecorded, format);
        if (block.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!IsRecording)
            {
                return;
            }

            _samples.AddRange(block);
        }

        LevelChanged?.Invoke(AudioAnalysis.Rms(block));
    }

    /// <summary>
    /// Разбор сырого буфера устройства. Микшер Windows почти всегда отдаёт 32-битный float, но
    /// на части драйверов приходит 16-битный PCM — поддерживаем оба, иначе на таком железе
    /// диктовка молча писала бы тишину.
    /// </summary>
    private static float[] Decode(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32:
            {
                var count = bytesRecorded / sizeof(float);
                var result = new float[count];
                Buffer.BlockCopy(buffer, 0, result, 0, count * sizeof(float));
                return result;
            }

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 16:
            {
                var count = bytesRecorded / sizeof(short);
                var result = new float[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = BitConverter.ToInt16(buffer, i * sizeof(short)) / 32768f;
                }

                return result;
            }

            default:
                Log.Write($"запись: неподдержанный формат {format.Encoding} {format.BitsPerSample} бит");
                return [];
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
