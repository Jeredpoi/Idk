using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Значки трея рисуем сами.
///
/// Так значок может показывать ТЕКУЩЕЕ СОСТОЯНИЕ — язык раскладки, запись, паузу, — а это
/// единственный способ ответить на вопрос «работает ли оно вообще», не заглядывая в лог.
/// Готовых картинок для этого понадобилось бы полтора десятка, и все пришлось бы держать в
/// репозитории.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color Coral = Color.FromArgb(0xFF, 0x6B, 0x4A);
    private static readonly Color Muted = Color.FromArgb(0x8A, 0x8A, 0x8E);
    private static readonly Color RecordRed = Color.FromArgb(0xE0, 0x3B, 0x3B);

    private readonly Dictionary<string, Icon> _cache = new(StringComparer.Ordinal);

    /// <summary>Значок с кодом языка: «RU» или «EN».</summary>
    internal Icon Layout(string code) => Get($"layout:{code}", () => Build(code, Coral));

    /// <summary>Идёт запись с микрофона.</summary>
    internal Icon Listening => Get("rec", () => Build("●", RecordRed));

    /// <summary>Идёт распознавание.</summary>
    internal Icon Processing => Get("proc", () => Build("…", Muted));

    /// <summary>Перехват снят.</summary>
    internal Icon Paused => Get("paused", () => Build("‖", Muted));

    private Icon Get(string key, Func<Icon> make)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = make();
        _cache[key] = icon;
        return icon;
    }

    private static Icon Build(string text, Color background)
    {
        // 32×32: Windows сама уменьшит до нужного размера, а на мониторах с масштабированием
        // мелкий исходник выглядел бы мылом.
        using var bitmap = new Bitmap(32, 32);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(background);
            g.FillEllipse(fill, 1, 1, 29, 29);

            // Размер шрифта зависит от длины: «RU» и «●» требуют разного, иначе двухбуквенный
            // код вылезает за круг.
            var size = text.Length > 1 ? 14f : 18f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            g.DrawString(text, font, foreground, new RectangleF(0, 0, 32, 32), format);
        }

        // GetHicon отдаёт НЕуправляемый дескриптор: Icon.FromHandle им только оборачивается и
        // владельцем не становится. Поэтому копируем значок и дескриптор освобождаем сами —
        // иначе на каждой перерисовке утекала бы GDI-ручка, а их у процесса конечное число.
        var handle = bitmap.GetHicon();

        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
    }
}
