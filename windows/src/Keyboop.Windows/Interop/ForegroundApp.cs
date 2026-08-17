using System.Runtime.InteropServices;
using Keyboop.Core.Layout;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Interop;

/// <summary>
/// Какая программа сейчас активна и как в ней себя вести.
///
/// ⚠️ РЕЗУЛЬТАТ КЭШИРУЕТСЯ ПО ОКНУ, И ЭТО ОБЯЗАТЕЛЬНО. Спрашивают нас из колбэка перехватчика, то
/// есть на каждое нажатие клавиши, а определение имени процесса — это открытие процесса и чтение
/// его пути, вещь несопоставимо дорогая. Зато <c>GetForegroundWindow</c> стоит копейки, поэтому
/// сравниваем дескриптор окна с прошлым и работаем только когда окно сменилось.
///
/// В macOS-версии дорогой вызов на этом же пути однажды заморозил ввод во всей системе — там
/// перехват синхронный, и пока он думает, стоит и клавиатура, и мышь. В Windows последствие мягче
/// (система просто снимет хук по таймауту), но результат для человека тот же: «перестало работать».
/// </summary>
internal sealed class ForegroundApp
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    /// <summary>
    /// ⚠️ БУФЕР — char[], А НЕ StringBuilder, И ЭТО НЕ ВКУСОВЩИНА.
    ///
    /// Маршалинг StringBuilder в вызов, который дописывает строку, — известный источник порчи
    /// памяти: среда выделяет промежуточный буфер по Capacity, копирует туда и обратно, и любое
    /// расхождение между Capacity и переданным размером система замечает не сразу, а позже —
    /// нарушением доступа где-то в стороне. Такое падение .NET перехватить не может: процесс
    /// исчезает мгновенно и молча, без исключения. Ровно так выглядела наша авария.
    ///
    /// С char[] промежуточного буфера нет вовсе: среда закрепляет массив и передаёт указатель
    /// на него. Размер, который мы сообщаем системе, и размер массива — одно и то же число.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly ExceptionStore _exceptions;

    private IntPtr _cachedWindow = IntPtr.Zero;
    private string _cachedExecutable = string.Empty;
    private string _cachedMode = AppMode.Normal;

    internal ForegroundApp(ExceptionStore exceptions) => _exceptions = exceptions;

    /// <summary>Имя исполняемого файла активной программы, без пути. Пусто — определить не удалось.</summary>
    internal string Executable
    {
        get
        {
            Refresh();
            return _cachedExecutable;
        }
    }

    /// <summary>Режим для активной программы: «», «off» или «soft».</summary>
    internal string Mode
    {
        get
        {
            Refresh();
            return _cachedMode;
        }
    }

    /// <summary>
    /// Забыть кэш. Звать после правки списка исключений: программа могла не меняться, а решение
    /// по ней — да, и без сброса правка не подействовала бы до переключения окон.
    /// </summary>
    internal void Invalidate()
    {
        _cachedWindow = IntPtr.Zero;
        _cachedExecutable = string.Empty;
        _cachedMode = AppMode.Normal;
    }

    private void Refresh()
    {
        var window = GetForegroundWindow();

        if (window == _cachedWindow)
        {
            return;   // самый частый путь: окно не менялось, работать не над чем
        }

        _cachedWindow = window;
        _cachedExecutable = ResolveExecutable(window);

        // Выбор человека сильнее встроенных умолчаний: он мог как добавить программу, так и убрать
        // её из списка, и второе тоже надо уважать.
        var userMode = _exceptions.ModeFor(_cachedExecutable);
        _cachedMode = string.IsNullOrEmpty(userMode)
            ? BuiltinAppModes.For(_cachedExecutable)
            : userMode;

        if (_cachedMode != AppMode.Normal)
        {
            Log.Write($"приложение: {_cachedExecutable} — режим «{_cachedMode}»");
        }
    }

    private static string ResolveExecutable(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(window, out var pid);
        if (pid == 0)
        {
            return string.Empty;
        }

        // PROCESS_QUERY_LIMITED_INFORMATION, а не полный доступ: этого хватает для имени файла и
        // работает даже для процессов с более высокими правами, где полный доступ нам не дадут.
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            // Длина пути в Windows ограничена 32767 символами, но нам нужно только имя файла,
            // а путь такой длины не встречается у настоящих программ. Полкилобайта с запасом.
            var buffer = new char[512];
            var size = (uint)buffer.Length;

            if (!QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                return string.Empty;
            }

            // size — сколько символов система записала, БЕЗ завершающего нуля. Берём ровно
            // столько: читать весь массив значило бы прихватить хвост из нулей.
            if (size == 0 || size > buffer.Length)
            {
                return string.Empty;
            }

            return Path.GetFileName(new string(buffer, 0, (int)size));
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
