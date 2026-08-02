using Keyboop.Core;
using Keyboop.Core.Audio;
using Keyboop.Core.Speech;
using Keyboop.Windows.Audio;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;
using Keyboop.Windows.Speech;

namespace Keyboop.Windows;

/// <summary>Что показывать в трее.</summary>
public enum VoiceState
{
    Idle,
    Recording,
    Processing,
}

/// <summary>
/// Оркестратор диктовки: хоткей → запись → whisper → вставка текста.
///
/// ⚠️ Все публичные методы вызываются ИЗ КОЛБЭКА ХУКА, то есть с потока, который Windows ждёт.
/// Поэтому здесь нет ни одной тяжёлой операции: старт записи и постановка распознавания в очередь
/// стоят микросекунды, а всё остальное уходит в Task. Если положить сюда что-то дорогое, Windows
/// снимет хук по таймауту и хоткей «перестанет работать» без единой ошибки.
/// </summary>
internal sealed class VoiceController : IDisposable
{
    private readonly MicrophoneRecorder _recorder = new();
    private readonly WhisperSpeechEngine _engine;
    private readonly AppSettings _settings;
    private readonly VoiceHistory _history;

    /// <summary>Диктовку отменили по Escape — распознавать не нужно.</summary>
    private volatile bool _cancelled;

    internal VoiceController(AppSettings settings, WhisperSpeechEngine engine, VoiceHistory history)
    {
        _settings = settings;
        _engine = engine;
        _history = history;
    }

    internal event Action<VoiceState>? StateChanged;

    /// <summary>Сообщение человеку, когда сделать ничего не удалось (тост в трее).</summary>
    internal event Action<string>? Notice;

    internal bool IsRecording => _recorder.IsRecording;

    internal void Begin()
    {
        if (_recorder.IsRecording)
        {
            return;
        }

        if (!_engine.IsModelLoaded)
        {
            // Разделяем два совершенно разных случая. «Модель не выбрана» — это задача человеку;
            // «модель ещё читается с диска» — это просьба подождать секунду. Одинаковое сообщение
            // на оба отправляло бы человека в настройки, где всё уже правильно.
            Notice?.Invoke(L10n.T(_engine.IsLoading ? "voice.modelLoading" : "voice.noModel"));
            return;
        }

        _cancelled = false;

        try
        {
            // Микрофон берём из настроек на КАЖДОЙ записи: человек мог поменять его в окне
            // настроек минуту назад, и требовать перезапуска ради этого не за что.
            _recorder.DeviceId = _settings.MicrophoneId;
            _recorder.Start();
            StateChanged?.Invoke(VoiceState.Recording);
        }
        catch (Exception ex)
        {
            // Микрофон занят эксклюзивно, отключён или запрещён политикой приватности Windows.
            Log.Write($"voice: запись не стартовала — {ex.GetType().Name}: {ex.Message}");
            Notice?.Invoke(L10n.T("voice.recordFailed"));
            StateChanged?.Invoke(VoiceState.Idle);
        }
    }

    internal void Cancel()
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        _cancelled = true;
        _recorder.Cancel();
        StateChanged?.Invoke(VoiceState.Idle);
        Log.Write("voice: диктовка отменена по Escape");
    }

    internal void End()
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        var samples = _recorder.Stop();

        if (_cancelled)
        {
            StateChanged?.Invoke(VoiceState.Idle);
            return;
        }

        var verdict = AudioAnalysis.Evaluate(samples, AudioResampler.TargetSampleRate);

        switch (verdict)
        {
            case CaptureRejection.TooShort:
                // Случайное касание хоткея. Молча — шуметь тут не о чем.
                Log.Write("voice: слишком коротко — отмена");
                StateChanged?.Invoke(VoiceState.Idle);
                return;

            case CaptureRejection.Silence:
                // А вот об этом сказать надо: чаще всего микрофон занят другим приложением, и
                // человеку важно понимать, что дело не в Keyboop.
                Log.Write("voice: тишина — распознавание пропущено");
                Notice?.Invoke(L10n.T("voice.silence"));
                StateChanged?.Invoke(VoiceState.Idle);
                return;
        }

        StateChanged?.Invoke(VoiceState.Processing);

        // Распознавание — в фон. Возврат из хука обязан быть мгновенным.
        _ = Task.Run(() => TranscribeAndDeliverAsync(samples));
    }

    private async Task TranscribeAndDeliverAsync(float[] samples)
    {
        try
        {
            var profile = WhisperDecodeProfile.ForLanguage(_settings.Language);
            var raw = await _engine.TranscribeAsync(samples, profile).ConfigureAwait(false);
            var text = TranscriptPostProcessor.Prepare(raw, _settings.OutputOptions);

            if (text.Length == 0)
            {
                Log.Write("voice: пустой результат — вставлять нечего");
                Notice?.Invoke(L10n.T("voice.empty"));
                return;
            }

            // ⚠️ В ИСТОРИЮ КЛАДЁМ ДО ПОПЫТКИ ВСТАВКИ. Вставка может не состояться по причинам, от
            // человека не зависящим: активно поле пароля, окно закрылось, программа не принимает
            // синтетический ввод. Записав только после успеха, мы теряли бы ровно те диктовки,
            // которые человеку важнее всего вернуть.
            _history.Add(text);

            if (!TextInjector.TypeText(text))
            {
                // Текст распознан, но система его не приняла. Молчать нельзя: для человека это
                // выглядит как потерянная диктовка.
                Notice?.Invoke(L10n.T("voice.insertFailed"));
                return;
            }

            if (_settings.AutoEnter)
            {
                const ushort VK_RETURN = 0x0D;
                TextInjector.PressKey(VK_RETURN);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"voice: распознавание упало — {ex.GetType().Name}: {ex.Message}");
            Notice?.Invoke(L10n.T("voice.failed"));
        }
        finally
        {
            StateChanged?.Invoke(VoiceState.Idle);
        }
    }

    public void Dispose()
    {
        _recorder.Dispose();
    }
}
