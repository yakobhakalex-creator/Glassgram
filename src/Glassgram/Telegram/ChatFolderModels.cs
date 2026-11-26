// Folder synchronization and ordering patterns adapted from Unigram (GPL-3.0),
// commit 9354bcb753be3b16086bd3886b8adec062ccdd91. See THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Glassgram.Telegram;

internal sealed class ChatFolderRow : INotifyPropertyChanged
{
    public int Id { get; init; }
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "\uE8B7";
    public int UnreadCount { get; private set; }
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility UnreadVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public JsonElement Folder { get; set; }
    public bool IsAll => Id == 0;
    public string ListKey => IsAll ? "chatListMain" : $"chatListFolder:{Id}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetUnread(int count)
    {
        if (UnreadCount == count) return;
        UnreadCount = count;
        OnChanged(nameof(UnreadCount));
        OnChanged(nameof(UnreadText));
        OnChanged(nameof(UnreadVisibility));
    }

    public static string ReadTitle(JsonElement info)
    {
        var name = info.Field("name").Field("text");
        return name.ValueKind == JsonValueKind.String ? name.Text() : name.Field("text").Text();
    }

    public static string IconGlyph(string name) => name switch
    {
        "All" => "\uE8A9", "Unread" => "\uE715", "Unmuted" => "\uE767", "Bots" => "\uE99A",
        "Channels" => "\uE789", "Groups" => "\uE902", "Private" => "\uE77B", "Custom" => "\uE8B7",
        _ => "\uE8B7"
    };

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

internal static class ChatFolderList
{
    public static string Key(int folderId) => folderId == 0 ? "chatListMain" : $"chatListFolder:{folderId}";
    public static bool Contains(ChatRow chat, int folderId) => chat.GetOrder(Key(folderId)) != 0;
    public static IReadOnlyList<ChatRow> Sort(IEnumerable<ChatRow> chats, int folderId) => chats
        .Where(chat => Contains(chat, folderId))
        .OrderByDescending(chat => chat.GetOrder(Key(folderId)))
        .ThenByDescending(chat => chat.Id)
        .ToArray();
}
