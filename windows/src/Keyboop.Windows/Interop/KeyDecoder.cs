using System.Runtime.InteropServices;
using System.Text;

namespace Keyboop.Windows.Interop;

/// <summary>
/// Что напечатала бы клавиша в текущей раскладке активного окна.
///
/// Нам нужен именно символ, а не код клавиши: движок работает с набранной строкой. Windows
/// умеет это сама через <c>ToUnicodeEx</c> — надо лишь передать ей раскладку того окна, куда
/// человек печатает, а не нашего процесса.
/// </summary>
internal static class KeyDecoder
{
    /// <summary>
    /// Не менять состояние клавиатуры ядра (Windows 10 1607+).
    ///
    /// ⚠️ БЕЗ ЭТОГО ФЛАГА КЛАСС ЛОМАЕТ ВВОД. <c>ToUnicodeEx</c> по умолчанию ПОТРЕБЛЯЕТ состояние
    /// мёртвой клавиши: опросив клавишу, мы бы забирали себе диакритику, и у человека переставали
    /// набираться «ü», «é», «ñ» — во всех программах и без единой ошибки в логе. Флаг делает
    /// вызов чисто читающим.
    /// </summary>
    private const uint DoNotChangeKernelState = 0x4;

    private const int VK_SHIFT = 0x10;
    private const int VK_CAPITAL = 0x14;

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPArray)] char[] pwszBuff,
        int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>
    /// Символы, которые дала бы клавиша. Пустая строка — клавиша не печатает (стрелки, F-ряд)
    /// либо это мёртвая клавиша.
    /// </summary>
    internal static string Decode(uint virtualKey, uint scanCode, IntPtr layout)
    {
        if (layout == IntPtr.Zero)
        {
            return string.Empty;
        }

        // Состояние собираем сами, а не через GetKeyboardState: в хуке та функция отдаёт состояние
        // очереди НАШЕГО потока, которое к чужому окну отношения не имеет.
        var state = new byte[256];
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            state[VK_SHIFT] = 0x80;
        }

        if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0)
        {
            state[VK_CAPITAL] = 0x01;
        }

        var buffer = new char[8];
        var count = ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Length,
            DoNotChangeKernelState, layout);

        // Отрицательное значение означает мёртвую клавишу: сама по себе она ничего не печатает,
        // символ появится только вместе со следующей. Для буфера это «ничего».
        return count <= 0 ? string.Empty : new string(buffer, 0, count);
    }

    /// <summary>Печатает ли эта строка видимый символ (управляющие отбрасываем).</summary>
    internal static bool IsPrintable(string s) =>
        s.Length > 0 && !char.IsControl(s[0]);
}
