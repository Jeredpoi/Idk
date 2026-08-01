using System.Runtime.InteropServices;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Interop;

/// <summary>
/// Вставка текста в активное окно БЕЗ буфера обмена — краеугольный принцип Keyboop.
///
/// Печатаем через SendInput с флагом KEYEVENTF_UNICODE: система доставляет символ как есть,
/// минуя раскладку. Поэтому русский текст печатается при активной английской раскладке и наоборот,
/// а буфер обмена пользователя остаётся нетронутым.
/// </summary>
internal static class TextInjector
{
    /// <summary>
    /// Метка «это наша синтетика» в dwExtraInfo. Перехватчик по ней пропускает наши же события
    /// насквозь, не принимая их за нажатия человека.
    ///
    /// Значение СЛУЧАЙНОЕ на каждый запуск, а не константа: открытая константа позволила бы
    /// стороннему процессу подставить её в свои инжекты и пройти мимо нашей обработки.
    /// </summary>
    internal static readonly UIntPtr SyntheticMarker = CreateMarker();

    /// <summary>
    /// Сколько событий отправляем одним вызовом. SendInput доставляет пакет атомарно относительно
    /// другого ввода, поэтому чем крупнее пакет, тем меньше шансов, что между нашими символами
    /// вклинится реальное нажатие. Потолок держим разумным: слишком большой массив просто дольше
    /// маршалится.
    /// </summary>
    private const int BatchSize = 256;

    private static UIntPtr CreateMarker()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong value = 0;
        while (value == 0)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt64(bytes);
        }

        // На 32-битной сборке UIntPtr вмещает только половину — берём младшие биты.
        return UIntPtr.Size == 8 ? new UIntPtr(value) : new UIntPtr((uint)value);
    }

    /// <summary>
    /// Напечатать текст в активное окно. Возвращает false, если система не приняла ни одного
    /// события — тогда вызывающий обязан сказать об этом человеку, а не молчать.
    /// </summary>
    internal static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        // Идём по кодовым единицам UTF-16, а не по рунам: суррогатную пару Windows собирает сама,
        // если half'ы пришли подряд в одном пакете. Эмодзи и редкие иероглифы так печатаются верно.
        var inputs = new List<NativeMethods.INPUT>(BatchSize);
        var sent = 0;
        var expected = 0;

        foreach (var unit in text)
        {
            inputs.Add(KeyboardInput(unit, keyUp: false));
            inputs.Add(KeyboardInput(unit, keyUp: true));
            expected += 2;

            if (inputs.Count >= BatchSize)
            {
                sent += Flush(inputs);
            }
        }

        sent += Flush(inputs);

        if (sent != expected)
        {
            Log.Write($"ввод: SendInput принял {sent} из {expected} событий (ошибка {Marshal.GetLastWin32Error()})");
        }

        return sent > 0;
    }

    /// <summary>Отправить нажатие обычной клавиши по виртуальному коду (например, Enter).</summary>
    internal static bool PressKey(ushort virtualKey)
    {
        var inputs = new List<NativeMethods.INPUT>(2)
        {
            VirtualKeyInput(virtualKey, keyUp: false),
            VirtualKeyInput(virtualKey, keyUp: true),
        };

        return Flush(inputs) == 2;
    }

    private static int Flush(List<NativeMethods.INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return 0;
        }

        var array = inputs.ToArray();
        inputs.Clear();

        var sent = NativeMethods.SendInput(
            (uint)array.Length, array, Marshal.SizeOf<NativeMethods.INPUT>());

        return (int)sent;
    }

    private static NativeMethods.INPUT KeyboardInput(char unit, bool keyUp)
    {
        var flags = NativeMethods.KEYEVENTF_UNICODE;
        if (keyUp)
        {
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        }

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,          // при KEYEVENTF_UNICODE виртуальный код обязан быть нулём
                    wScan = unit,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = SyntheticMarker,
                },
            },
        };
    }

    private static NativeMethods.INPUT VirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = SyntheticMarker,
                },
            },
        };
    }
}
