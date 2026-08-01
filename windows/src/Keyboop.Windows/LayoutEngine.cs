using Keyboop.Core.Layout;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;

namespace Keyboop.Windows;

/// <summary>
/// Исправление раскладки: следит за набором, на границе слова спрашивает детектор и, если слово
/// набрано не в той раскладке, перепечатывает его и переключает язык.
///
/// ⚠️ ГРАНИЦА ОТВЕТСТВЕННОСТИ. Всё, что решает, ЧТО делать, живёт в Keyboop.Core и покрыто
/// тестами. Здесь только то, что умеет одна Windows: прочитать клавишу, напечатать замену,
/// переключить раскладку. Держать эту границу важно — она единственная причина, по которой
/// логику детектора можно проверять без Windows вообще.
/// </summary>
internal sealed class LayoutEngine
{
    private const uint VK_BACK = 0x08;
    private const uint VK_TAB = 0x09;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_SPACE = 0x20;
    private const uint VK_PRIOR = 0x21;   // PageUp
    private const uint VK_NEXT = 0x22;    // PageDown
    private const uint VK_END = 0x23;
    private const uint VK_HOME = 0x24;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_DELETE = 0x2E;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;     // Alt

    private readonly KeystrokeBuffer _buffer = new();
    private readonly AntiResonanceGuard _antiResonance = new();
    private readonly LayoutData _data;
    private readonly IExceptionStore _exceptions;

    internal LayoutEngine(LayoutData data, IExceptionStore exceptions)
    {
        _data = data;
        _exceptions = exceptions;
        _antiResonance.Logger = Log.Write;
    }

    /// <summary>Исправлять раскладку автоматически на границе слова.</summary>
    internal bool AutoEnabled { get; set; } = true;

    /// <summary>Слово исправлено: сколько символов заменили и в какую сторону (для лога и трея).</summary>
    internal event Action<bool>? Converted;

    /// <summary>
    /// Нажатие клавиши. Вызывается из колбэка хука, поэтому здесь нет ничего дорогого:
    /// одно чтение раскладки активного окна, один <c>ToUnicodeEx</c> и работа с памятью.
    /// </summary>
    internal void OnKeyDown(uint virtualKey, uint scanCode)
    {
        // Ctrl или Alt означают сочетание, а не текст. Буфер после такого недостоверен.
        if (IsHeld(VK_CONTROL) || IsHeld(VK_MENU))
        {
            _buffer.Clear();
            return;
        }

        switch (virtualKey)
        {
            case VK_BACK:
                _buffer.Backspace();
                return;

            case VK_SPACE:
            case VK_TAB:
            case VK_RETURN:
                HandleBoundary(virtualKey);
                return;

            case VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN:
            case VK_HOME or VK_END or VK_PRIOR or VK_NEXT:
            case VK_ESCAPE or VK_DELETE:
                // Курсор уехал — дальше мы уже не знаем, что на экране.
                _buffer.Clear();
                return;
        }

        var layout = KeyboardLayoutSwitcher.ForegroundLayout();
        var chars = KeyDecoder.Decode(virtualKey, scanCode, layout);

        if (KeyDecoder.IsPrintable(chars))
        {
            _buffer.Append(chars);
        }
    }

    /// <summary>Клик мышью или смена окна: где каретка — мы больше не знаем.</summary>
    internal void ResetContext() => _buffer.Clear();

    /// <summary>
    /// Ручное переключение по хоткею: направление определяем по содержимому слова, а не по
    /// детектору — человек уже решил сам.
    /// </summary>
    internal void ConvertManually()
    {
        var target = _buffer.WordForConversion();
        if (target is null)
        {
            return;
        }

        var item = target.Value;
        var toCyrillic = item.Word.HasCyrillic()
            ? false
            : item.Word.HasLatinLetter() || !KeyboardLayoutSwitcher.ForegroundIsCyrillic();

        var converted = Keymap.Convert(item.Word, toCyrillic);
        if (converted == item.Word)
        {
            return;
        }

        Apply(item, converted, toCyrillic, completedOnly: false, reason: "хоткей");
    }

    private void HandleBoundary(uint virtualKey)
    {
        var whitespace = virtualKey switch
        {
            VK_TAB => "\t",
            VK_RETURN => "\n",
            _ => " ",
        };

        _buffer.Boundary(whitespace);

        if (!AutoEnabled || !_data.IsLoaded)
        {
            return;
        }

        // Целимся строго в ЗАВЕРШЁННОЕ слово: человек мог уже начать следующее, и без этого
        // завершённое осиротело бы и молча не починилось.
        var target = _buffer.WordForConversion(completedOnly: true);
        if (target is null)
        {
            return;
        }

        var item = target.Value;

        // Спасение смешанного слова идёт первым: обычный детектор такие слова всегда оставляет
        // как есть, и без этой ветки они застревали бы наполовину переключёнными.
        var decision = LayoutDetector.MixedRescue(item.Word, _data);
        if (!decision.ShouldConvert)
        {
            var previous = _buffer.ContextWord(forCurrent: false);
            decision = LayoutDetector.Decide(item.Word, _data, _exceptions, previous);
        }

        if (!decision.ShouldConvert)
        {
            return;
        }

        var converted = Keymap.SmartConvert(
            item.Word, decision.ToCyrillic, w => _data.WordsRu.Contains(w));

        if (converted == item.Word)
        {
            return;
        }

        if (!_antiResonance.Allow(item.Word, converted))
        {
            // Резонанс: цикл рвём и начинаем с чистого листа, иначе он раскрутится снова.
            _buffer.Clear();
            return;
        }

        Apply(item, converted, decision.ToCyrillic, completedOnly: true, reason: "авто");
    }

    private void Apply(
        ConversionTarget item, string converted, bool toCyrillic, bool completedOnly, string reason)
    {
        // Печатаем замену ВМЕСТЕ с хвостом: удаление считается от каретки, а между словом и
        // кареткой уже лежит пробел (и, возможно, начало следующего слова).
        if (!TextInjector.ReplaceText(item.DeleteCount, converted + item.Tail))
        {
            Log.Write("замена: система не приняла пакет — слово оставлено как есть");
            return;
        }

        if (completedOnly)
        {
            _buffer.ApplyCompletedConversion(converted);
        }
        else
        {
            _buffer.ApplyConversion(converted);
        }

        KeyboardLayoutSwitcher.Switch(toCyrillic);

        // В лог только измеримое: длина и направление, без содержимого слова.
        Log.Write($"convert-word({reason}): {item.DeleteCount} симв. "
                  + $"{item.Word.ScriptClass()} → {(toCyrillic ? "RU" : "EN")}");

        Converted?.Invoke(toCyrillic);
    }

    private static bool IsHeld(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
