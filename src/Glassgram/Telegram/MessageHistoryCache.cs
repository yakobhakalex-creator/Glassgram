using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glassgram.Telegram;

internal static class MessageHistoryCache
{
    private const int CachedMessageLimit = 500;
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "MessageCache");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IReadOnlyList<MessageRow>> LoadAsync(long chatId, CancellationToken token)
    {
        var path = CachePath(chatId);
        if (!File.Exists(path)) return [];
        await Gate.WaitAsync(token);
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, token);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var rawMessages = JsonSerializer.Deserialize<string[]>(bytes) ?? [];
            var messages = new List<MessageRow>(rawMessages.Length);
            foreach (var raw in rawMessages)
            {
                token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(raw);
                var row = MessageRow.FromJson(document.RootElement);
                if (row.Id != 0) messages.Add(row);
            }
            return messages;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return [];
        }
        finally { Gate.Release(); }
    }

    public static async Task SaveAsync(long chatId, IEnumerable<MessageRow> messages, CancellationToken token)
    {
        var rawMessages = messages
            .Where(message => !message.IsDate && message.RawJson.Length > 0)
            .OrderByDescending(message => message.Date).ThenByDescending(message => message.Id)
            .Take(CachedMessageLimit)
            .Select(message => message.RawJson)
            .ToArray();
        if (rawMessages.Length == 0) return;

        byte[] protectedBytes;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rawMessages);
            protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException) { return; }
        var path = CachePath(chatId);
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Root);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, token);
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { Gate.Release(); }
    }

    private static string CachePath(long chatId) => Path.Combine(Root, chatId.ToString() + ".bin");
}
