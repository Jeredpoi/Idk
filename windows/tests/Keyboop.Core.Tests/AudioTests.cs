using Keyboop.Core.Audio;
using Xunit;

namespace Keyboop.Core.Tests;

public class AudioResamplerTests
{
    [Fact]
    public void DownmixAveragesStereoChannels()
    {
        // Кадры: (1, 0), (0, 1), (1, 1) → 0.5, 0.5, 1.0
        float[] stereo = [1f, 0f, 0f, 1f, 1f, 1f];
        Assert.Equal(new[] { 0.5f, 0.5f, 1f }, AudioResampler.DownmixToMono(stereo, channels: 2));
    }

    [Fact]
    public void DownmixLeavesMonoUntouched()
    {
        float[] mono = [0.1f, 0.2f, 0.3f];
        Assert.Equal(mono, AudioResampler.DownmixToMono(mono, channels: 1));
    }

    [Fact]
    public void ResampleIsNoOpWhenRatesMatch()
    {
        float[] mono = [0.1f, 0.2f, 0.3f];
        Assert.Equal(mono, AudioResampler.Resample(mono, 16000, 16000));
    }

    /// <summary>
    /// Понижение 48 кГц → 16 кГц должно дать ровно треть сэмплов: это самый частый реальный случай,
    /// потому что микшер Windows обычно держит микрофон именно на 48 кГц.
    /// </summary>
    [Fact]
    public void ResampleProducesExpectedLengthForCommonDeviceRate()
    {
        var source = new float[48000];
        var result = AudioResampler.Resample(source, 48000);
        Assert.Equal(16000, result.Length);
    }

    [Fact]
    public void ResamplePreservesConstantSignal()
    {
        var source = new float[44100];
        Array.Fill(source, 0.5f);

        var result = AudioResampler.Resample(source, 44100);

        Assert.NotEmpty(result);
        Assert.All(result, v => Assert.Equal(0.5, (double)v, precision: 5));
    }

    /// <summary>Линейная интерполяция обязана сохранять края сигнала без сдвига.</summary>
    [Fact]
    public void ResamplePreservesEndpoints()
    {
        float[] ramp = [0f, 0.25f, 0.5f, 0.75f, 1f];
        var result = AudioResampler.Resample(ramp, 5, 9);

        Assert.Equal(9, result.Length);
        Assert.Equal(0.0, (double)result[0], precision: 5);
        Assert.Equal(1.0, (double)result[^1], precision: 5);
    }

    [Fact]
    public void ResampleHandlesEmptyInput()
    {
        Assert.Empty(AudioResampler.Resample(Array.Empty<float>(), 48000));
    }

    [Fact]
    public void ResampleRejectsNonPositiveRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Resample(new[] { 0f, 1f }, 0));
    }

    [Fact]
    public void ToWhisperFormatCombinesDownmixAndResample()
    {
        // Две секунды стерео на 48 кГц → одна дорожка 16 кГц длиной в две секунды.
        var stereo = new float[48000 * 2 * 2];
        var result = AudioResampler.ToWhisperFormat(stereo, channels: 2, sourceRate: 48000);
        Assert.Equal(32000, result.Length);
    }
}

public class AudioAnalysisTests
{
    private const int Rate = AudioResampler.TargetSampleRate;

    [Fact]
    public void RmsOfSilenceIsZero()
    {
        Assert.Equal(0f, AudioAnalysis.Rms(new float[1000]));
    }

    [Fact]
    public void RmsOfConstantSignalEqualsItsMagnitude()
    {
        var samples = new float[1000];
        Array.Fill(samples, 0.5f);
        Assert.Equal(0.5, (double)AudioAnalysis.Rms(samples), precision: 5);
    }

    /// <summary>Короткое касание хоткея отменяем молча — это не диктовка.</summary>
    [Fact]
    public void RejectsRecordingShorterThanMinimum()
    {
        var samples = new float[(int)(Rate * 0.2)];
        Array.Fill(samples, 0.5f);
        Assert.Equal(CaptureRejection.TooShort, AudioAnalysis.Evaluate(samples, Rate));
    }

    /// <summary>
    /// Тишину до whisper не пускаем: на пустом аудио он галлюцинирует титрами
    /// («Продолжение следует»), и это выглядит как выдуманный текст из ниоткуда.
    /// </summary>
    [Fact]
    public void RejectsSilentRecordingOfSufficientLength()
    {
        var samples = new float[Rate * 2];
        Assert.Equal(CaptureRejection.Silence, AudioAnalysis.Evaluate(samples, Rate));
    }

    [Fact]
    public void AcceptsAudibleRecording()
    {
        var samples = new float[Rate * 2];
        Array.Fill(samples, 0.05f);
        Assert.Equal(CaptureRejection.None, AudioAnalysis.Evaluate(samples, Rate));
    }

    /// <summary>Длительность считается по частоте, а не по «сколько пришло буферов».</summary>
    [Fact]
    public void ComputesDurationFromSampleCount()
    {
        Assert.Equal(1.5, AudioAnalysis.DurationSeconds(Rate + (Rate / 2), Rate), precision: 5);
    }
}
