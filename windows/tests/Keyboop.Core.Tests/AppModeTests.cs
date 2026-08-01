using Keyboop.Core.Layout;
using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Тесты про то, ГДЕ программе работать нельзя. Цена ошибки здесь несимметрична: не исправить
/// раскладку — досадно, а стереть клип на таймлинии видеоредактора — испортить чужую работу.
/// </summary>
public class BuiltinAppModesTests
{
    /// <summary>В терминале Backspace ломает уже введённую команду.</summary>
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("alacritty.exe")]
    [InlineData("mintty.exe")]
    public void TerminalsAreOff(string exe)
    {
        Assert.Equal(AppMode.Off, BuiltinAppModes.For(exe));
    }

    /// <summary>В видеоредакторе Backspace удаляет клип, а пробел запускает воспроизведение.</summary>
    [Theory]
    [InlineData("Adobe Premiere Pro.exe")]
    [InlineData("AfterFX.exe")]
    [InlineData("Resolve.exe")]
    [InlineData("ProTools.exe")]
    [InlineData("blender.exe")]
    public void MediaEditorsAreOff(string exe)
    {
        Assert.Equal(AppMode.Off, BuiltinAppModes.For(exe));
    }

    /// <summary>Ввод уходит в чужую систему — наша модель набранного там заведомо неверна.</summary>
    [Theory]
    [InlineData("mstsc.exe")]
    [InlineData("VirtualBoxVM.exe")]
    [InlineData("AnyDesk.exe")]
    public void RemoteAndVirtualMachinesAreOff(string exe)
    {
        Assert.Equal(AppMode.Off, BuiltinAppModes.For(exe));
    }

    [Theory]
    [InlineData("Code.exe")]
    [InlineData("devenv.exe")]
    [InlineData("idea64.exe")]
    [InlineData("cursor.exe")]
    public void CodeEditorsAreSoft(string exe)
    {
        Assert.Equal(AppMode.Soft, BuiltinAppModes.For(exe));
    }

    /// <summary>Обычные программы — обычный режим, иначе мы отключили бы себя везде.</summary>
    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("Telegram.exe")]
    [InlineData("WINWORD.EXE")]
    [InlineData("notepad.exe")]
    public void EverythingElseIsNormal(string exe)
    {
        Assert.Equal(AppMode.Normal, BuiltinAppModes.For(exe));
    }

    /// <summary>Имя файла приходит от Windows как есть — регистр совпадать не обязан.</summary>
    [Theory]
    [InlineData("CMD.EXE")]
    [InlineData("cmd.exe")]
    [InlineData("Cmd.Exe")]
    public void MatchesRegardlessOfCase(string exe)
    {
        Assert.Equal(AppMode.Off, BuiltinAppModes.For(exe));
    }

    [Fact]
    public void UnknownProcessIsNormal()
    {
        Assert.Equal(AppMode.Normal, BuiltinAppModes.For(string.Empty));
    }
}

public class SoftModeFilterTests
{
    /// <summary>
    /// Одиночные буквы в коде — это переменные и флаги командной строки, а не русские предлоги,
    /// набранные не в той раскладке. Именно они дают самые обидные ложные срабатывания.
    /// </summary>
    [Theory]
    [InlineData("c")]
    [InlineData("d")]
    [InlineData("i")]
    [InlineData("rm")]
    [InlineData("cd")]
    public void SkipsShortTokens(string word)
    {
        Assert.True(SoftModeFilter.ShouldSkip(word));
    }

    [Theory]
    [InlineData("ааа")]
    [InlineData("ссс")]
    [InlineData("wwww")]
    public void SkipsRepeatedLetters(string word)
    {
        Assert.True(SoftModeFilter.ShouldSkip(word));
    }

    /// <summary>Проза в комментариях и сообщениях коммитов чинится и в мягком режиме.</summary>
    [Theory]
    [InlineData("ghbdtn")]
    [InlineData("rjvvbn")]
    [InlineData("здравствуйте")]
    public void KeepsRealWordsEligible(string word)
    {
        Assert.False(SoftModeFilter.ShouldSkip(word));
    }

    /// <summary>Концевая пунктуация на решение не влияет — считаем по ядру слова.</summary>
    [Fact]
    public void IgnoresTrailingPunctuationWhenMeasuring()
    {
        Assert.True(SoftModeFilter.ShouldSkip("cd."));
        Assert.False(SoftModeFilter.ShouldSkip("ghbdtn."));
    }
}
