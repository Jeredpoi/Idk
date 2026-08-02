namespace Keyboop.Core.Speech;

/// <summary>Модель whisper в каталоге.</summary>
/// <param name="Name">Имя файла без префикса и расширения: «base», «large-v3-turbo».</param>
/// <param name="Size">Размер файла, как его показывают человеку.</param>
/// <param name="NoteKey">Ключ описания в таблице строк — каталог статичен, а язык выбирается на показе.</param>
/// <param name="Sha256">Ожидаемая контрольная сумма файла на закреплённой ревизии.</param>
public readonly record struct WhisperModel(string Name, string Size, string NoteKey, string Sha256);

/// <summary>
/// Каталог моделей whisper и правила их скачивания.
///
/// ⚠️ ЦЕЛОСТНОСТЬ ФАЙЛА ЗДЕСЬ — НЕ ФОРМАЛЬНОСТЬ. Модель — это бинарный файл, который потом
/// разбирает нативный парсер внутри нашего процесса. Скачать его с изменяемого адреса и запустить,
/// ничего не проверив, значит согласиться исполнить что угодно, что окажется по этому адресу.
/// Поэтому:
///
/// 1. Адрес закреплён на НЕИЗМЕНЯЕМУЮ ревизию репозитория. Содержимое HuggingFace адресуется
///    коммитом, то есть байты по такой ссылке зафиксированы навсегда.
/// 2. После скачивания сверяется SHA-256 с зашитым здесь значением. Несовпадение — отказ,
///    файл не устанавливается и удаляется.
///
/// Оба правила перенесены из macOS-версии, где они появились после аудита. Зеркала здесь нет
/// сознательно: в оригинале это сервер автора, и подставлять сторонний хост в цепочку доверия
/// ради скорости не стоит — при недоступности HuggingFace остаётся ручной выбор файла.
///
/// Сеть используется ТОЛЬКО по явному нажатию кнопки «Скачать». Фоном не качается ничего.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Неизменяемая ревизия репозитория ggerganov/whisper.cpp, на которую закреплены все модели.</summary>
    public const string PinnedRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public static readonly IReadOnlyList<WhisperModel> All =
    [
        new("base", "142 МБ", "model.base.note",
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "466 МБ", "model.small.note",
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("medium", "1,5 ГБ", "model.medium.note",
            "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        new("large-v3-turbo", "1,6 ГБ", "model.large.note",
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
    ];

    public static string FileName(string model) => $"ggml-{model}.bin";

    public static string Url(string model) =>
        $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{PinnedRevision}/{FileName(model)}";

    /// <summary>Размер в единицах текущего языка: каталог хранит русские, английскому нужны свои.</summary>
    public static string LocalizedSize(string size) =>
        L10n.Current == Lang.Ru ? size : size.Replace("МБ", "MB").Replace("ГБ", "GB").Replace(',', '.');
}
