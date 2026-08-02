using System.Runtime.InteropServices;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Interop;

/// <summary>Как ведёт себя хоткей диктовки.</summary>
public enum HotkeyMode
{
    /// <summary>Удерживать: нажал — пишем, отпустил — распознаём.</summary>
    Hold,

    /// <summary>Переключать: нажал — пишем, нажал ещё раз — распознаём.</summary>
    Toggle,
}

/// <summary>Описание хоткея: клавиша плюс обязательные модификаторы.</summary>
public sealed class HotkeyBinding
{
    /// <summary>Виртуальный код клавиши. Ноль означает «хоткей не назначен».</summary>
    public int VirtualKey { get; init; }

    /// <summary>Модификаторы, которые обязаны быть зажаты. Пусто — голая клавиша.</summary>
    public IReadOnlyList<int> Modifiers { get; init; } = [];

    public bool IsAssigned => VirtualKey != 0;
}

/// <summary>
/// Единственный на всё приложение перехватчик клавиатуры (WH_KEYBOARD_LL). Через него проходят
/// оба хоткея — диктовки и ручного переключения раскладки — и наблюдение за обычным набором.
///
/// ⚠️ ГЛАВНОЕ ОГРАНИЧЕНИЕ: колбэк обязан возвращаться БЫСТРО. Windows отводит низкоуровневому
/// хуку ограниченное время (по умолчанию 300 мс, ключ реестра
/// HKCU\Control Panel\Desktop\LowLevelHooksTimeout) и МОЛЧА снимает хук, который не уложился.
/// Симптом для человека — «хоткей просто перестал работать», без единой ошибки. Это тот же класс,
/// что kCGEventTapDisabledByTimeout в macOS-версии, где виновником был дорогой системный вызов на
/// горячем пути. Поэтому здесь нельзя ничего, кроме сравнения чисел и постановки задачи в очередь.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _holdActive;
    private DateTime _lastToggleAt = DateTime.MinValue;

    /// <summary>
    /// ПАРНОСТЬ ГЛОТАНИЙ. Клавиши, у которых мы проглотили нажатие, — их отпускание обязаны
    /// проглотить тоже.
    ///
    /// Зачем это отдельная структура, а не проверка условий на месте: если решать по условию
    /// («зажаты ли модификаторы сейчас»), то ответ на отпускании может отличаться от ответа на
    /// нажатии — модификатор человек отпускает первым. Тогда приложение получает отпускание
    /// клавиши, которую при нём не нажимали, система считает её зажатой, и у человека начинает
    /// жить своей жизнью весь остальной ввод. В macOS-версии этот класс стоил трёх отдельных
    /// отчётов вида «перестал работать пробел, лечится только выходом из программы».
    ///
    /// Самоизлечение: обычное (непроглоченное) нажатие той же клавиши убирает её из набора,
    /// поэтому потерянное отпускание не отравляет следующий ввод.
    /// </summary>
    private readonly HashSet<uint> _swallowedDown = [];

    internal KeyboardHook()
    {
        // Ссылку на делегат держим полем. Без этого сборщик мусора соберёт его, пока хук ещё жив,
        // и первый же ввод после сборки уронит процесс в неуправляемом коде.
        _proc = HookCallback;
    }

    /// <summary>Хоткей диктовки.</summary>
    internal HotkeyBinding Dictation { get; set; } = new() { VirtualKey = 0x78 };   // F9

    /// <summary>Хоткей ручного переключения раскладки последнего слова.</summary>
    internal HotkeyBinding LayoutConvert { get; set; } = new() { VirtualKey = 0x13 };   // Pause

    internal HotkeyMode Mode { get; set; } = HotkeyMode.Hold;

    internal event Action? DictationStarted;

    internal event Action? DictationStopped;

    internal event Action? DictationCancelled;

    /// <summary>Человек попросил переключить последнее слово вручную.</summary>
    internal event Action? LayoutConvertRequested;

    /// <summary>Нажали Caps Lock в режиме «Caps переключает язык».</summary>
    internal event Action? CapsSwitchRequested;

    /// <summary>
    /// Caps Lock переключает язык вместо включения верхнего регистра.
    ///
    /// На Windows это делается честно и просто: низкоуровневый перехватчик, вернувший «проглотить»,
    /// не даёт системе переключить сам замок — ни индикатор, ни регистр не меняются. В macOS-версии
    /// того же пришлось добиваться перепрошивкой раскладки на уровне HID, потому что там замок
    /// защёлкивается НИЖЕ перехватчика и проглатывание событие только прячет.
    /// </summary>
    internal bool CapsSwitchesLayout { get; set; }

    /// <summary>Идёт ли запись прямо сейчас — источник истины держит VoiceController.</summary>
    internal Func<bool> IsRecording { get; set; } = () => false;

    /// <summary>
    /// Человек назначает хоткей в настройках — на это время не перехватываем НИЧЕГО.
    ///
    /// ⚠️ Без этого флага окно назначения нерабочее: человек жмёт комбинацию, которую хочет
    /// назначить, мы узнаём в ней свой текущий хоткей, глотаем нажатие и запускаем старое
    /// действие, а окно настроек события не получает вовсе. Со стороны это выглядит как
    /// «нажимаю переназначить, ничего не происходит» — ровно такие отчёты приходили в
    /// macOS-версии, пока там не появился тот же флаг.
    /// </summary>
    internal bool RecordingMode { get; set; }

    /// <summary>
    /// Каждое не относящееся к хоткеям нажатие — сюда. На этом живёт исправление раскладки.
    /// Обработчик выполняется прямо в колбэке, то есть в том же жёстком лимите времени.
    ///
    /// Возврат true означает «клавишу проглотить»: правка посреди слова печатает эту букву сама,
    /// внутри общего пакета замены. Пропустить её ещё и от системы значило бы получить букву
    /// дважды.
    /// </summary>
    internal Func<uint, uint, bool>? KeyObserved;

    internal void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        var module = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Не удалось поставить перехватчик клавиатуры (ошибка {Marshal.GetLastWin32Error()}).");
        }

        Log.Write($"хук: установлен · диктовка vk=0x{Dictation.VirtualKey:X2} режим={Mode} · "
                  + $"переключение vk=0x{LayoutConvert.VirtualKey:X2}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Наша собственная синтетика (вставка текста, замена слова) в разбор хоткеев не идёт:
        // иначе печать могла бы сама себя запустить заново.
        if (data.dwExtraInfo == TextInjector.SyntheticMarker)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // Идёт назначение хоткея — пропускаем всё насквозь, включая наши текущие хоткеи.
        if (RecordingMode)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (isDown && HandleKeyDown(data))
        {
            return new IntPtr(1);
        }

        if (isUp)
        {
            // Порядок важен: сначала останавливаем запись, потом снимаем запись о парности —
            // иначе отпускание hold-хоткея потерялось бы и диктовка осталась включённой навсегда.
            NotifyReleased(data.vkCode);

            // Отпускание глотаем РОВНО у тех клавиш, чьё нажатие проглотили сами.
            if (_swallowedDown.Remove(data.vkCode))
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Разбор нажатия. true — клавишу надо проглотить.</summary>
    private bool HandleKeyDown(NativeMethods.KBDLLHOOKSTRUCT data)
    {
        const int VK_ESCAPE = 0x1B;

        if (data.vkCode == VK_ESCAPE && IsRecording())
        {
            DictationCancelled?.Invoke();
            _holdActive = false;
            return SwallowDown(data.vkCode);
        }

        const int VK_CAPITAL = 0x14;

        if (CapsSwitchesLayout && data.vkCode == VK_CAPITAL)
        {
            // ⚠️ Проглатываем ВСЕГДА, даже если переключение не удалось. Пропустить нажатие
            // «на всякий случай» означало бы включить капс — то есть сделать ровно то, от чего
            // человек эту настройку и включил.
            CapsSwitchRequested?.Invoke();
            return SwallowDown(data.vkCode);
        }

        if (Matches(LayoutConvert, data.vkCode))
        {
            LayoutConvertRequested?.Invoke();
            return SwallowDown(data.vkCode);
        }

        if (Matches(Dictation, data.vkCode))
        {
            HandleDictationKey();
            return SwallowDown(data.vkCode);
        }

        // Обычное нажатие. Снимаем возможную протухшую пару: если отпускание этой клавиши когда-то
        // потерялось, запись съела бы отпускание следующего, честного нажатия.
        _swallowedDown.Remove(data.vkCode);

        if (KeyObserved is not null)
        {
            try
            {
                if (KeyObserved(data.vkCode, data.scanCode))
                {
                    // Букву напечатали мы сами. Её отпускание тоже придётся проглотить: иначе
                    // приложение получит keyUp клавиши, нажатия которой при нём не было, — ровно
                    // тот непарный случай, ради которого существует _swallowedDown.
                    return SwallowDown(data.vkCode);
                }
            }
            catch (Exception ex)
            {
                // Сбой в разборе слова не должен ронять перехватчик: иначе человек разом теряет
                // и хоткеи, и весь ввод.
                Log.Write($"хук: наблюдатель нажатий упал — {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void HandleDictationKey()
    {
        if (Mode == HotkeyMode.Toggle)
        {
            // Защита от автоповтора зажатой клавиши: без неё удержание чуть дольше обычного
            // успевало выключить запись сразу после включения, и выглядело это как
            // «нажал — ничего не произошло».
            var now = DateTime.UtcNow;
            if ((now - _lastToggleAt).TotalMilliseconds < 300)
            {
                return;
            }

            _lastToggleAt = now;

            if (IsRecording())
            {
                DictationStopped?.Invoke();
            }
            else
            {
                DictationStarted?.Invoke();
            }

            return;
        }

        if (!_holdActive)
        {
            _holdActive = true;
            DictationStarted?.Invoke();
        }
    }

    private bool Matches(HotkeyBinding binding, uint virtualKey)
    {
        if (!binding.IsAssigned || virtualKey != (uint)binding.VirtualKey)
        {
            return false;
        }

        foreach (var modifier in binding.Modifiers)
        {
            if ((NativeMethods.GetAsyncKeyState(modifier) & 0x8000) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Проглотить нажатие, запомнив клавишу: её отпускание тоже нельзя пускать дальше.</summary>
    private bool SwallowDown(uint virtualKey)
    {
        _swallowedDown.Add(virtualKey);
        return true;
    }

    /// <summary>
    /// Отпустили клавишу диктовки в режиме удержания — значит, запись пора останавливать.
    ///
    /// Условие сознательно НЕ проверяет модификаторы: человек отпускает их в произвольном порядке,
    /// и требовать их здесь означало бы иногда не получить команду «стоп» вовсе — то есть оставить
    /// микрофон включённым до следующего нажатия.
    /// </summary>
    private void NotifyReleased(uint virtualKey)
    {
        if (Mode != HotkeyMode.Hold || !_holdActive)
        {
            return;
        }

        if (!Dictation.IsAssigned || virtualKey != (uint)Dictation.VirtualKey)
        {
            return;
        }

        _holdActive = false;
        DictationStopped?.Invoke();
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>
    /// Снять перехват. Состояние сбрасываем полностью: пока хука не было, отпускания проходили
    /// мимо нас, и уцелевшие записи о парности съели бы отпускание следующих честных нажатий.
    /// </summary>
    internal void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _swallowedDown.Clear();
        _holdActive = false;
        Log.Write("хук: перехват снят");
    }

    public void Dispose() => Uninstall();
}
