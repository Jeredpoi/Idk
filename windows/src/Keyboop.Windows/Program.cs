using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows;

internal static class Program
{
    /// <summary>
    /// Единственный экземпляр. Два процесса поставили бы два перехватчика на один хоткей, и
    /// диктовка запускалась бы дважды с одного нажатия — с двумя записями одного микрофона.
    /// </summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main()
    {
        _single = new Mutex(initiallyOwned: true, "Keyboop.Windows.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        Log.Write("=== Keyboop запущен ===");
        Application.Run(new TrayApp());
        Log.Write("=== Keyboop завершён ===");

        _single.ReleaseMutex();
        _single.Dispose();
    }
}
