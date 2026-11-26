using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glassgram.Telegram;

internal sealed record RecentSearchEntry(long ChatId, string Title, string Username, string Type, int PhotoFileId, string AvatarPath);

internal static class RecentSearchStore
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "RecentSearches");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IReadOnlyList<RecentSearchEntry>> LoadAsync(long accountId, CancellationToken token)
    {
        var path = PathFor(accountId);
        if (accountId == 0 || !File.Exists(path)) return [];
        await Gate.WaitAsync(token);
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, token);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy(accountId), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<RecentSearchEntry[]>(bytes) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return [];
        }
        finally { Gate.Release(); }
    }

    public static async Task SaveAsync(long accountId, IEnumerable<RecentSearchEntry> entries, CancellationToken token)
    {
        if (accountId == 0) return;
        var snapshot = entries.Take(20).ToArray();
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Root);
            var path = PathFor(accountId);
            if (snapshot.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            var encrypted = ProtectedData.Protect(bytes, Entropy(accountId), DataProtectionScope.CurrentUser);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted, token);
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
        finally { Gate.Release(); }
    }

    private static byte[] Entropy(long accountId) => Encoding.UTF8.GetBytes($"Glassgram.RecentSearches:{accountId}");
    private static string PathFor(long accountId) => Path.Combine(Root, accountId.ToString() + ".bin");
}
