using System.Security.Cryptography;
using Keyboop.Core.Speech;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Speech;

/// <summary>Чем кончилась загрузка.</summary>
public enum DownloadResult
{
    Ok,

    /// <summary>Человек нажал «Отмена».</summary>
    Cancelled,

    /// <summary>Сеть не отдала файл.</summary>
    NetworkError,

    /// <summary>Файл скачался, но это не тот файл.</summary>
    ChecksumMismatch,

    /// <summary>Не удалось записать на диск.</summary>
    DiskError,
}

/// <summary>
/// Скачивание моделей whisper.
///
/// ⚠️ ЕДИНСТВЕННОЕ МЕСТО ВО ВСЕЙ ПРОГРАММЕ, КОТОРОЕ ХОДИТ В СЕТЬ, и ходит только по явному
/// нажатию кнопки. Распознавание, исправление раскладки и всё остальное работают полностью
/// офлайн — на этом стоит проект, и добавлять сюда что-либо ещё нельзя.
///
/// Порядок действий выбран так, чтобы на диске никогда не оказалось непроверенного файла:
/// качаем во временный файл рядом с целевым, сверяем SHA-256 и только потом переносим на место.
/// Обрыв связи или несовпадение суммы оставляют прежнюю модель нетронутой.
/// </summary>
internal static class ModelDownloader
{
    /// <summary>Куда складываем модели: рядом с настройками, в подпапке.</summary>
    internal static string ModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "models");

    internal static string PathOf(string model) =>
        Path.Combine(ModelsDirectory, ModelCatalog.FileName(model));

    internal static bool IsInstalled(string model) => File.Exists(PathOf(model));

    /// <summary>Удалить скачанную модель. true — файла больше нет.</summary>
    internal static bool Delete(string model)
    {
        try
        {
            var path = PathOf(model);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"модель {model}: не удалилась — {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Скачать модель. <paramref name="progress"/> получает долю от нуля до единицы; если сервер
    /// не сказал размер, доля остаётся отрицательной — вызывающему это значит «показывай
    /// неопределённый прогресс», а не «ноль процентов».
    /// </summary>
    internal static async Task<DownloadResult> DownloadAsync(
        string model, IProgress<double> progress, CancellationToken cancellation)
    {
        var expected = ModelCatalog.All.FirstOrDefault(m => m.Name == model);
        if (string.IsNullOrEmpty(expected.Name))
        {
            return DownloadResult.NetworkError;
        }

        string temporary;

        try
        {
            Directory.CreateDirectory(ModelsDirectory);

            // Временный файл рядом с целевым, а не в системном temp: перенос внутри одного тома
            // атомарен, а между томами это копирование, которое может оборваться на середине.
            temporary = PathOf(model) + ".part";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"модель {model}: папка не создалась — {ex.GetType().Name}");
            return DownloadResult.DiskError;
        }

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            Log.Write($"модель {model}: качаю с закреплённой ревизии {ModelCatalog.PinnedRevision[..8]}");

            using var response = await http
                .GetAsync(ModelCatalog.Url(model), HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            var copied = 0L;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var target = File.Create(temporary))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    copied += read;
                    progress.Report(total > 0 ? (double)copied / total : -1);
                }
            }

            var actual = await Sha256Async(temporary, cancellation).ConfigureAwait(false);

            if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Не тот файл. Что бы это ни было — сбой по дороге или подмена, — в парсер оно
                // не попадёт: удаляем немедленно, не оставляя ничего на диске.
                Log.Write($"модель {model}: контрольная сумма не совпала — файл удалён");
                TryDelete(temporary);
                return DownloadResult.ChecksumMismatch;
            }

            File.Move(temporary, PathOf(model), overwrite: true);
            Log.Write($"модель {model}: скачана и проверена, {copied} байт");
            return DownloadResult.Ok;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            Log.Write($"модель {model}: загрузка отменена");
            return DownloadResult.Cancelled;
        }
        catch (Exception ex)
        {
            // ⚠️ ЛОВИМ ВСЁ, И ЭТО НЕ ЛЕНЬ. Зовут нас из обработчика нажатия кнопки, который
            // объявлен async void: исключение, вышедшее отсюда, ловить уже некому, и оно убивает
            // весь процесс. Раньше здесь стоял список из двух типов, мимо которого проходил,
            // например, UnauthorizedAccessException — папка только для чтения, и человек вместо
            // сообщения об ошибке получал закрывшуюся программу.
            TryDelete(temporary);
            Log.Write($"модель {model}: не скачалась — {ex.GetType().Name}: {ex.Message}");

            return ex switch
            {
                HttpRequestException => DownloadResult.NetworkError,
                IOException or UnauthorizedAccessException => DownloadResult.DiskError,
                _ => DownloadResult.NetworkError,
            };
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Недоудалённый временный файл — не повод шуметь: следующая загрузка его перезапишет.
        }
    }
}
