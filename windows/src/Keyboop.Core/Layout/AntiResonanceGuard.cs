namespace Keyboop.Core.Layout;

/// <summary>
/// Предохранитель против «быстрого циклического переключения» — когда раскладка дёргается
/// A→B→A→B десятки раз в секунду.
///
/// ПРИЧИНА. Авто-конверсия печатает синтетику. Если хоть одно наше событие не опознано как наше,
/// оно возвращается в буфер, меняет текущее слово, и правка срабатывает снова → синтетика → …
/// Петля крутится в темпе миллисекунд. Защита «слово не равно прошлому результату» хранит одно
/// значение и осцилляцию A→B→A не ловит.
///
/// РЕШЕНИЕ — не подавление частоты (его пробовали, оно само даёт лаг), а детект осцилляции плюс
/// заморозка. Барьер общий: гасит видимый симптом независимо от микропричины и пишет об этом
/// в лог, чтобы первопричину можно было найти. Применяется ТОЛЬКО к авто-конверсии: человек с
/// хоткеем резонировать не может.
/// </summary>
public sealed class AntiResonanceGuard
{
    private readonly TimeSpan _window;
    private readonly int _maxFlips;
    private readonly TimeSpan _freezeFor;

    private readonly List<(string Produced, DateTime At)> _recent = [];
    private DateTime _frozenUntil = DateTime.MinValue;
    private bool _didLogFreeze;

    /// <summary>Инъектируемые часы для тестов.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Куда писать факт заморозки. Содержимое слов не передаём.</summary>
    public Action<string>? Logger { get; set; }

    public AntiResonanceGuard(
        TimeSpan? window = null, int maxFlips = 6, TimeSpan? freezeFor = null)
    {
        _window = window ?? TimeSpan.FromSeconds(0.7);
        _maxFlips = maxFlips;
        _freezeFor = freezeFor ?? TimeSpan.FromSeconds(2.5);
    }

    /// <summary>
    /// Спросить перед авто-конверсией. false означает резонанс: вызывающий ОБЯЗАН пропустить
    /// конверсию и желательно сбросить буфер, чтобы разорвать цикл.
    /// </summary>
    public bool Allow(string word, string produced)
    {
        var now = Clock();

        if (now < _frozenUntil)
        {
            return false;
        }

        _recent.RemoveAll(e => now - e.At > _window);

        // Осцилляция: мы собираемся ПРОИЗВЕСТИ форму, которую только что конвертировали ПРОЧЬ.
        // В авто-режиме такого быть не должно, значит это резонанс.
        var oscillation = _recent.Any(e => e.Produced == word);
        _recent.Add((produced, now));

        if (!oscillation && _recent.Count <= _maxFlips)
        {
            return true;
        }

        _frozenUntil = now + _freezeFor;
        _recent.Clear();

        if (!_didLogFreeze)
        {
            _didLogFreeze = true;
            Logger?.Invoke($"anti-resonance: циклическое переключение — авто заморожено на "
                           + $"{_freezeFor.TotalSeconds:0.#} с ({(oscillation ? "осцилляция" : "шторм")})");
        }

        return false;
    }

    /// <summary>Заморожены ли авто-конверсии прямо сейчас.</summary>
    public bool IsFrozen => Clock() < _frozenUntil;

    /// <summary>Сбросить историю (но не заморозку — она истекает по времени сама).</summary>
    public void ResetHistory() => _recent.Clear();
}
