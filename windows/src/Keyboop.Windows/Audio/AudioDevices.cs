using Keyboop.Windows.Diagnostics;
using NAudio.CoreAudioApi;

namespace Keyboop.Windows.Audio;

/// <summary>Микрофон в списке: устойчивый идентификатор и то, что видит человек.</summary>
internal readonly record struct AudioInput(string Id, string Name);

/// <summary>
/// Список микрофонов.
///
/// Зачем это вообще нужно: «устройство по умолчанию» в Windows — не то же самое, что нужный
/// микрофон. Гарнитура, веб-камера и встроенный микрофон ноутбука соревнуются за это звание,
/// и Windows переназначает его сама при каждом подключении. Человек, у которого диктовка вдруг
/// начала писать тишину, чаще всего столкнулся именно с этим.
///
/// Храним идентификатор устройства, а не его номер в списке: номера перетасовываются при каждом
/// подключении, и сохранённый «второй в списке» после перезагрузки означал бы уже другой микрофон.
/// </summary>
internal static class AudioDevices
{
    /// <summary>Пустой идентификатор означает «как решит Windows».</summary>
    internal const string SystemDefault = "";

    internal static IReadOnlyList<AudioInput> Inputs()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            var result = new List<AudioInput>(devices.Count);
            foreach (var device in devices)
            {
                result.Add(new AudioInput(device.ID, device.FriendlyName));
                device.Dispose();
            }

            return result;
        }
        catch (Exception ex)
        {
            // Список микрофонов — не то, ради чего стоит ронять окно настроек. Пустой список
            // означает «только устройство по умолчанию», и это рабочее состояние.
            Log.Write($"аудио: список устройств не получен — {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Устройство по идентификатору. null — «берите умолчание»: и когда ничего не выбрано,
    /// и когда выбранного микрофона уже нет.
    ///
    /// ⚠️ Отсутствие устройства НЕ должно ломать диктовку. Микрофон отключают от USB каждый день;
    /// отказаться записывать вовсе означало бы наказать человека за то, что он вынул провод.
    /// </summary>
    internal static MMDevice? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (device.ID == id)
                {
                    return device;
                }

                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"аудио: устройство не найдено — {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        Log.Write("аудио: выбранный микрофон недоступен — беру устройство по умолчанию");
        return null;
    }
}
