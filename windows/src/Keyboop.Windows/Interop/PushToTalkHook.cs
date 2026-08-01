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

/// <summary>
/// Глобальный перехватчик клавиатуры (WH_KEYBOARD_LL) для хоткея диктовки.
///
/// ⚠️ ГЛАВНОЕ ОГРАНИЧЕНИЕ ЭТОГО КЛАССА: колбэк обязан возвращаться БЫСТРО. Windows отводит
/// низкоуровневому хуку ограниченное время (по умолчанию 300 мс, ключ реестра
/// HKCU\Control Panel\Desktop\LowLevelHooksTimeout) и МОЛЧА снимает хук, который не уложился.
/// Симптом для человека — «хоткей просто перестал работать», без единой ошибки. Это ровно тот же
/// класс, что kCGEventTapDisabledByTimeout в macOS-версии, где виновником был дорогой системный
/// вызов на горячем пути. Поэтому здесь нельзя ничего, кроме сравнения чисел и постановки задачи
/// в очередь: ни записи на диск, ни обращения к аудио, ни распознавания.
/// </summary>
internal sealed class PushToTalkHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _holdActive;
    private DateTime _lastToggleAt = DateTime.MinValue;

    /// <summary>
    /// Ссылку на делегат держим полем. Без этого сборщик мусора соберёт его, пока хук ещё жив,
    /// и первый же ввод после сборки уронит процесс в неуправляемом коде.
    /// </summary>
    internal PushToTalkHook()
    {
        _proc = HookCallback;
    }

    /// <summary>Виртуальный код клавиши хоткея (по умолчанию F9).</summary>
    internal int VirtualKey { get; set; } = 0x78;

    /// <summary>Требуемые модификаторы; пустой список означает «голая клавиша».</summary>
    internal IReadOnlyList<int> Modifiers { get; set; } = [];

    internal HotkeyMode Mode { get; set; } = HotkeyMode.Hold;

    /// <summary>Начать запись. Вызывается из колбэка хука — обязан только ставить задачу в очередь.</summary>
    internal event Action? DictationStarted;

    /// <summary>Закончить запись и распознать.</summary>
    internal event Action? DictationStopped;

    /// <summary>Отмена по Escape во время записи.</summary>
    internal event Action? DictationCancelled;

    /// <summary>Идёт ли запись прямо сейчас — источник истины держит VoiceController.</summary>
    internal Func<bool> IsRecording { get; set; } = () => false;

    /// <summary>
    /// Каждое НЕ относящееся к хоткею нажатие — сюда. На этом живёт исправление раскладки.
    ///
    /// ⚠️ Обработчик выполняется прямо в колбэке хука, то есть в том же жёстком лимите времени.
    /// Внутри допустимы только дешёвые вызовы user32 и работа с памятью.
    /// </summary>
    internal Action<uint, uint>? KeyObserved;

    internal void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        // Для WH_KEYBOARD_LL модуль не обязателен, но передать валидный хэндл надёжнее:
        // некоторые сборки .NET отдают null-модуль для управляемой сборки.
        var module = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Не удалось поставить перехватчик клавиатуры (ошибка {Marshal.GetLastWin32Error()}).");
        }

        Log.Write($"хук: перехватчик установлен, хоткей vk=0x{VirtualKey:X2} режим={Mode}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Наша собственная синтетика (вставка распознанного текста) не должна попадать в разбор
        // хоткеев — иначе печать текста могла бы сама себя запустить заново.
        if (data.dwExtraInfo == TextInjector.SyntheticMarker)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (Swallow(data, isDown, isUp))
        {
            return new IntPtr(1);   // проглочено: в приложение не уйдёт
        }

        // Обычное нажатие — отдаём наблюдателю (исправление раскладки). Ошибку здесь глушим
        // намеренно: сбой в разборе слова не должен ронять перехватчик, иначе человек разом
        // теряет и хоткей диктовки, и весь ввод.
        if (isDown && KeyObserved is not null)
        {
            try
            {
                KeyObserved(data.vkCode, data.scanCode);
            }
            catch (Exception ex)
            {
                Log.Write($"хук: наблюдатель нажатий упал — {ex.GetType().Name}: {ex.Message}");
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Разбор нажатия. Возвращает true, если клавишу надо проглотить.
    ///
    /// ПАРНОСТЬ ОБЯЗАТЕЛЬНА: проглотили нажатие — обязаны проглотить и отпускание. Иначе система
    /// считает клавишу зажатой, и у человека начинает жить своей жизнью весь остальной ввод.
    /// В macOS-версии этот класс бага стоил трёх отдельных отчётов («перестал работать пробел»).
    /// </summary>
    private bool Swallow(NativeMethods.KBDLLHOOKSTRUCT data, bool isDown, bool isUp)
    {
        const int VK_ESCAPE = 0x1B;

        if (isDown && data.vkCode == VK_ESCAPE && IsRecording())
        {
            DictationCancelled?.Invoke();
            _holdActive = false;
            return true;
        }

        if (data.vkCode != (uint)VirtualKey)
        {
            return false;
        }

        if (isDown)
        {
            if (!ModifiersHeld())
            {
                return false;
            }

            if (Mode == HotkeyMode.Toggle)
            {
                // Защита от автоповтора зажатой клавиши: без неё удержание чуть дольше обычного
                // успевало выключить запись сразу после включения, и выглядело это как
                // «нажал — ничего не произошло».
                var now = DateTime.UtcNow;
                if ((now - _lastToggleAt).TotalMilliseconds < 300)
                {
                    return true;
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

                return true;
            }

            if (!_holdActive)
            {
                _holdActive = true;
                DictationStarted?.Invoke();
            }

            return true;   // глотаем и автоповтор тоже, иначе клавиша печатается во время записи
        }

        if (isUp)
        {
            if (Mode == HotkeyMode.Hold && _holdActive)
            {
                _holdActive = false;
                DictationStopped?.Invoke();
                return true;
            }

            // В toggle-режиме отпускание тоже глотаем, но только если глотали нажатие —
            // иначе уйдёт непарное отпускание.
            return Mode == HotkeyMode.Toggle && ModifiersHeld();
        }

        return false;
    }

    private bool ModifiersHeld()
    {
        foreach (var vk in Modifiers)
        {
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
