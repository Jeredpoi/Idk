using System.Runtime.InteropServices;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Interop;

/// <summary>
/// Чтение и переключение раскладки активного окна.
///
/// ⚠️ ГЛАВНАЯ ТОНКОСТЬ WINDOWS: <c>ActivateKeyboardLayout</c> меняет раскладку только у ВЫЗЫВАЮЩЕГО
/// потока. Мы фоновое приложение, поэтому вызов у себя не сделает ничего видимого — человек
/// продолжит печатать в старой раскладке. Единственный работающий способ переключить чужое окно —
/// отправить ему <c>WM_INPUTLANGCHANGEREQUEST</c>. Именно так это делают штатный переключатель
/// и все сторонние.
/// </summary>
internal static class KeyboardLayoutSwitcher
{
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const int INPUTLANGCHANGE_FORWARD = 0x0002;

    private const ushort LANG_ENGLISH = 0x09;
    private const ushort LANG_RUSSIAN = 0x19;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Идентификатор языка (младшее слово HKL).</summary>
    private static ushort PrimaryLanguage(IntPtr hkl) => (ushort)((ulong)hkl & 0x3FF);

    /// <summary>Раскладка активного окна — кириллическая?</summary>
    internal static bool ForegroundIsCyrillic()
    {
        var hkl = ForegroundLayout();
        return hkl != IntPtr.Zero && PrimaryLanguage(hkl) == LANG_RUSSIAN;
    }

    internal static IntPtr ForegroundLayout()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var thread = GetWindowThreadProcessId(window, out _);
        return GetKeyboardLayout(thread);
    }

    /// <summary>
    /// Переключить активное окно на «другой» язык: с кириллицы на латиницу и обратно.
    /// Возвращает направление, в котором переключились, либо null, если не вышло.
    /// </summary>
    internal static bool? Toggle()
    {
        var toCyrillic = !ForegroundIsCyrillic();
        return Switch(toCyrillic) ? toCyrillic : null;
    }

    /// <summary>Все раскладки, включённые у пользователя.</summary>
    internal static IntPtr[] InstalledLayouts()
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return [];
        }

        var list = new IntPtr[count];
        GetKeyboardLayoutList(count, list);
        return list;
    }

    /// <summary>
    /// Переключить активное окно на кириллицу или латиницу. Возвращает false, если подходящей
    /// раскладки среди включённых нет — и об этом обязательно надо сказать человеку, иначе он
    /// увидит «переключение не работает» без объяснения.
    ///
    /// ⚠️ Латиницу ищем НЕ по «английскому языку». В macOS-версии это стоило двух отчётов от
    /// одного человека: штатные латинские раскладки объявляют первым языком вовсе не английский
    /// (ABC-AZERTY → французский, QWERTZ → немецкий), и переключение работало ровно в одну
    /// сторону — человек уходил в русский и обратно уже не возвращался. Поэтому латиницей
    /// считаем любую НЕ кириллическую раскладку, а настоящий английский лишь предпочитаем.
    /// </summary>
    internal static bool Switch(bool toCyrillic)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var layouts = InstalledLayouts();
        if (layouts.Length == 0)
        {
            return false;
        }

        IntPtr target = IntPtr.Zero;

        if (toCyrillic)
        {
            target = layouts.FirstOrDefault(h => PrimaryLanguage(h) == LANG_RUSSIAN);
        }
        else
        {
            // Настоящий английский предпочитаем, но не требуем.
            target = layouts.FirstOrDefault(h => PrimaryLanguage(h) == LANG_ENGLISH);
            if (target == IntPtr.Zero)
            {
                target = layouts.FirstOrDefault(h => PrimaryLanguage(h) != LANG_RUSSIAN);
            }
        }

        if (target == IntPtr.Zero)
        {
            Log.Write($"раскладка: среди включённых нет {(toCyrillic ? "кириллической" : "латинской")} "
                      + $"(всего {layouts.Length})");
            return false;
        }

        // PostMessage, а не SendMessage: синхронная отправка ждала бы ответа чужого окна, а мы
        // вызываемся с пути обработки ввода, где ждать нельзя вообще ничего.
        return PostMessageW(window, WM_INPUTLANGCHANGEREQUEST, new IntPtr(INPUTLANGCHANGE_FORWARD), target);
    }
}
