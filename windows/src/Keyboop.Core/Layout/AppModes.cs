namespace Keyboop.Core.Layout;

/// <summary>Насколько вольно можно вести себя в конкретной программе.</summary>
public static class AppMode
{
    /// <summary>Обычный режим: чиним как везде.</summary>
    public const string Normal = "";

    /// <summary>Не трогать вообще ничего.</summary>
    public const string Off = "off";

    /// <summary>Чинить только очевидное: прозу, но не команды и не переменные.</summary>
    public const string Soft = "soft";
}

/// <summary>
/// Встроенные умолчания «где не работать».
///
/// ⚠️ ЭТО НЕ ПЕРЕСТРАХОВКА, А ЗАЩИТА ОТ ПОРЧИ ЧУЖОЙ РАБОТЫ. Исправление раскладки устроено как
/// «стереть набранное и напечатать заново», то есть опирается на то, что Backspace стирает символ.
/// В видеоредакторе Backspace удаляет выделенный клип, в терминале ломает уже введённую команду,
/// а пробел в проигрывателе запускает воспроизведение. В macOS-версии этот список появился не из
/// осторожности, а после реальных инцидентов у пользователей.
///
/// Сравниваем по имени исполняемого файла без пути и регистра — это самое устойчивое, что есть:
/// путь установки у всех разный, а заголовок окна меняется от документа.
/// </summary>
public static class BuiltinAppModes
{
    /// <summary>Терминалы, видео- и аудиоредакторы: не трогаем ничего.</summary>
    private static readonly HashSet<string> OffApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Терминалы и консоли
        "cmd.exe", "powershell.exe", "pwsh.exe", "windowsterminal.exe", "conhost.exe",
        "openconsole.exe", "alacritty.exe", "wezterm-gui.exe", "wezterm.exe", "mintty.exe",
        "putty.exe", "kitty.exe", "hyper.exe", "tabby.exe", "far.exe", "conemu64.exe",
        "cmder.exe", "wsl.exe", "ubuntu.exe", "debian.exe",

        // Видео и аудио: Backspace здесь удаляет клип, а пробел запускает воспроизведение
        "adobe premiere pro.exe", "afterfx.exe", "adobe audition.exe", "adobe media encoder.exe",
        "resolve.exe", "fusion.exe", "vegas180.exe", "vegas190.exe", "vegas200.exe",
        "ableton live 11 suite.exe", "ableton live 12 suite.exe", "protools.exe",
        "cubase.exe", "flstudio.exe", "fl64.exe", "reaper.exe", "studioone.exe",
        "blender.exe", "cinema 4d.exe", "3dsmax.exe", "maya.exe",

        // Виртуальные машины и удалённые рабочие столы: ввод уходит в чужую систему,
        // и наша модель набранного там заведомо неверна
        "mstsc.exe", "vmware.exe", "vmplayer.exe", "virtualbox.exe", "virtualboxvm.exe",
        "anydesk.exe", "teamviewer.exe", "rdcman.exe",
    };

    /// <summary>Редакторы кода: прозу и комментарии чиним, команды и переменные — нет.</summary>
    private static readonly HashSet<string> SoftApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "zed.exe",
        "devenv.exe", "sublime_text.exe", "notepad++.exe", "atom.exe",
        "rider64.exe", "idea64.exe", "pycharm64.exe", "clion64.exe", "webstorm64.exe",
        "goland64.exe", "phpstorm64.exe", "rubymine64.exe", "datagrip64.exe",
        "gvim.exe", "emacs.exe",
    };

    /// <summary>
    /// Встроенный режим для имени исполняемого файла. Пустая строка — обычный режим.
    /// Пользовательский список сильнее: его проверяет вызывающий и только потом спрашивает нас.
    /// </summary>
    public static string For(string executableName)
    {
        if (string.IsNullOrEmpty(executableName))
        {
            return AppMode.Normal;
        }

        if (OffApps.Contains(executableName))
        {
            return AppMode.Off;
        }

        return SoftApps.Contains(executableName) ? AppMode.Soft : AppMode.Normal;
    }
}

/// <summary>
/// Фильтр мягкого режима: в редакторах кода не трогаем то, что чаще всего оказывается не словом,
/// а именем переменной или флагом команды.
/// </summary>
public static class SoftModeFilter
{
    /// <summary>
    /// true — слово в мягком режиме трогать не надо.
    ///
    /// Отсекаем короткое (до двух букв) и состоящее из одной повторяющейся буквы. Именно эти два
    /// класса дают самые обидные ложные срабатывания в коде: одиночные «c», «d», «i» — переменные
    /// и флаги, а не русские предлоги, набранные не в той раскладке.
    /// </summary>
    public static bool ShouldSkip(string word)
    {
        var core = Keymap.CoreOf(word).ToLowerInvariant();

        if (core.Length <= 2)
        {
            return true;
        }

        var first = core[0];
        foreach (var c in core)
        {
            if (c != first)
            {
                return false;
            }
        }

        return true;   // все буквы одинаковые: «ааа», «ссс»
    }
}
