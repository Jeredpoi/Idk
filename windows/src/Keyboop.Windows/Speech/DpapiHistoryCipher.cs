using System.Runtime.Versioning;
using System.Security.Cryptography;
using Keyboop.Core.Speech;

namespace Keyboop.Windows.Speech;

/// <summary>
/// Шифрование истории диктовок средствами самой Windows (DPAPI, область текущего пользователя).
///
/// ⚠️ ЗАЧЕМ ЭТО ВООБЩЕ. В файле истории лежат расшифровки того, что человек говорил вслух:
/// сообщения, адреса, иногда пароли, иногда разговоры о здоровье. Обычный JSON рядом с настройками
/// читается любым процессом, запущенным от этого же пользователя, и любой программой синхронизации,
/// которая заберёт папку в облако. В macOS-версии история шифруется — здесь она этого ждала.
///
/// ЧЕСТНАЯ МОДЕЛЬ УГРОЗ, чтобы не обещать лишнего. DPAPI привязывает ключ к учётной записи
/// Windows. Это закрывает: чтение файла из-под другой учётной записи, копирование файла на другую
/// машину, попадание его в облачную синхронизацию в читаемом виде. Это НЕ закрывает: программу,
/// уже запущенную от имени этого же человека, — она может расшифровать файл ровно так же, как мы.
/// Абсолютной защиты здесь и не бывает; настоящая защита — короткий срок жизни записей, и он есть.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiHistoryCipher : IHistoryCipher
{
    /// <summary>
    /// Дополнительная привязка к назначению файла. Не секрет: она не даёт расшифровать этими же
    /// вызовами данные, зашифрованные другой частью системы, и наоборот.
    /// </summary>
    private static readonly byte[] Purpose = "Keyboop voice history"u8.ToArray();

    public byte[] Protect(byte[] plain) =>
        ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] cipher) =>
        ProtectedData.Unprotect(cipher, Purpose, DataProtectionScope.CurrentUser);
}
