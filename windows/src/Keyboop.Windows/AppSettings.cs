using System.Text.Json;
using System.Text.Json.Serialization;
using Keyboop.Core.Speech;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;

namespace Keyboop.Windows;

/// <summary>
/// Настройки в обычном JSON рядом с логом. Не реестр: файл можно посмотреть, приложить к отчёту
/// об ошибке и починить руками, когда интерфейс недоступен.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Виртуальный код клавиши диктовки. По умолчанию F9.</summary>
    public int HotkeyVirtualKey { get; set; } = 0x78;

    /// <summary>
    /// Модификаторы хоткея. Пусто = голая клавиша.
    ///
    /// ⚠️ Голую клавишу можно назначать ТОЛЬКО такую, которая ничего не печатает. Повесив
    /// диктовку, скажем, на пробел, человек останется без пробела во всех программах, пока не
    /// выйдет из Keyboop: мы глотаем нажатие целиком. В macOS-версии это стоило трёх отчётов.
    /// </summary>
    public List<int> HotkeyModifiers { get; set; } = [];

    /// <summary>«hold» — удерживать, «toggle» — переключать.</summary>
    public string HotkeyModeName { get; set; } = "hold";

    /// <summary>Путь к файлу модели ggml-*.bin.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>«ru», «en» или «auto».</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Снимать одиночную точку в конце реплики.</summary>
    public bool DropFinalPeriod { get; set; }

    /// <summary>Опускать регистр первой буквы.</summary>
    public bool DropLeadingCapital { get; set; }

    /// <summary>Добавлять пробел в конце, чтобы фразы не слипались.</summary>
    public bool TrailingSpace { get; set; } = true;

    /// <summary>Отправлять Enter сразу после вставки (для чатов).</summary>
    public bool AutoEnter { get; set; }

    /// <summary>Исправлять раскладку набранного автоматически на границе слова.</summary>
    public bool LayoutAutoFix { get; set; } = true;

    /// <summary>
    /// Клавиша ручного переключения последнего слова. По умолчанию Pause — так же, как в
    /// Punto Switcher, к которому привыкло большинство.
    ///
    /// ⚠️ Голая клавиша здесь допустима ровно потому, что Pause ничего не печатает и её штатное
    /// действие давно ничего не значит. Вешать сюда печатающую клавишу нельзя: мы глотаем нажатие
    /// целиком, и человек останется без этого символа во всех программах.
    /// На ноутбуках без Pause клавишу надо переназначить — например, на F8.
    /// </summary>
    public int LayoutHotkeyVirtualKey { get; set; } = 0x13;

    /// <summary>Модификаторы для хоткея переключения. Пусто — голая клавиша.</summary>
    public List<int> LayoutHotkeyModifiers { get; set; } = [];

    [JsonIgnore]
    public HotkeyMode Mode =>
        string.Equals(HotkeyModeName, "toggle", StringComparison.OrdinalIgnoreCase)
            ? Interop.HotkeyMode.Toggle
            : Interop.HotkeyMode.Hold;

    [JsonIgnore]
    public DictationOutputOptions OutputOptions => new()
    {
        DropFinalPeriod = DropFinalPeriod,
        DropLeadingCapital = DropLeadingCapital,
        TrailingSpace = TrailingSpace,
        AutoEnter = AutoEnter,
    };

    /// <summary>История распознанного — чтобы текст не пропадал, если вставка не удалась.</summary>
    public static string VoiceHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "voice-history.json");

    /// <summary>Счётчики откатов для обучения на отмене.</summary>
    public static string UndoLearnPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "undo-learn.json");

    /// <summary>Файл сокращений автозамены — рядом с настройками.</summary>
    public static string SnippetsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "snippets.json");

    /// <summary>Файл со списками исключений — рядом с настройками.</summary>
    public static string ExceptionsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "exceptions.json");

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var text = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, Json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл настроек не должен мешать запуску: работаем на значениях по умолчанию
            // и честно говорим об этом в логе, чтобы причина «настройки сбросились» была видна.
            Log.Write($"настройки: файл не прочитан ({ex.GetType().Name}) — беру значения по умолчанию");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"настройки: не удалось сохранить ({ex.GetType().Name})");
        }
    }
}
