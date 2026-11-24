using System.Security.Cryptography;

namespace Glassgram.Telegram;

internal static class SessionStorage
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "Telegram");
    private static readonly string KeyPath = Path.Combine(Root, "database.key");

    public static string DatabasePath => Path.Combine(Root, "database");
    public static string FilesPath => Path.Combine(Root, "files");

    public static string GetEncryptionKey()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DatabasePath);
        Directory.CreateDirectory(FilesPath);
        if (File.Exists(KeyPath))
            return Convert.ToBase64String(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser));

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        using (var file = new FileStream(KeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.Write(protectedKey);
        return Convert.ToBase64String(key);
    }
}
