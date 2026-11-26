using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glassgram.Telegram;

internal sealed class SearchChatResult : INotifyPropertyChanged
{
    public string SectionTitle { get; set; } = "";
    public long ChatId { get; init; }
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Type { get; set; } = "";
    public string AvatarLetter => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "•";
    public ImageSource? Avatar { get; set; }
    public int PhotoFileId { get; set; }
    public bool IsVerified { get; set; }
    public bool IsScam { get; set; }
    public bool IsFake { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
    public void SetAvatar(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Avatar = new BitmapImage(new Uri(path)); Raise(nameof(Avatar)); }
        catch (UriFormatException) { }
    }
}

internal sealed class SearchMessageResult : INotifyPropertyChanged
{
    public string SectionTitle { get; set; } = "";
    public long ChatId { get; init; }
    public long MessageId { get; init; }
    public string ChatTitle { get; set; } = "";
    public string Preview { get; set; } = "";
    public DateTime Date { get; set; }
    public bool Outgoing { get; set; }
    public string TimeText => Date == DateTime.MinValue ? "" : Date.Date == DateTime.Today ? Date.ToString("HH:mm") : Date.ToString("dd.MM.yy");
    public string SenderName { get; set; } = "";
    public string Title => ChatTitle;
    public string Username => "";
    public string Type => "Сообщение";
    public string AvatarLetter => ChatTitle.Length > 0 ? ChatTitle[..1].ToUpperInvariant() : "•";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

internal sealed class SearchContactResult : INotifyPropertyChanged
{
    public string SectionTitle { get; set; } = "";
    public long UserId { get; init; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Username { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string DisplayName => $"{FirstName} {LastName}".Trim();
    public string Title => DisplayName;
    public string Type => "Контакт";
    public string AvatarLetter => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "•";
    public ImageSource? Avatar { get; set; }
    public int PhotoFileId { get; set; }
    public bool IsVerified { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
    public void SetAvatar(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Avatar = new BitmapImage(new Uri(path)); Raise(nameof(Avatar)); }
        catch (UriFormatException) { }
    }
}

internal sealed class SearchPublicChatResult : INotifyPropertyChanged
{
    public string SectionTitle { get; set; } = "";
    public long ChatId { get; init; }
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Type { get; set; } = "";
    public int MembersCount { get; set; }
    public string AvatarLetter => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "•";
    public ImageSource? Avatar { get; set; }
    public int PhotoFileId { get; set; }
    public bool IsVerified { get; set; }
    public bool IsScam { get; set; }
    public bool IsFake { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
    public void SetAvatar(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Avatar = new BitmapImage(new Uri(path)); Raise(nameof(Avatar)); }
        catch (UriFormatException) { }
    }
}

internal enum SearchCategory
{
    ChatsAndContacts,
    Global,
    Messages
}

internal sealed class GlobalSearchState
{
    public string Query { get; set; } = "";
    public bool IsSearching { get; set; }
    public bool HasSearched { get; set; }
    public int Generation { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public List<SearchChatResult> Chats { get; } = [];
    public List<SearchMessageResult> Messages { get; } = [];
    public List<SearchContactResult> Contacts { get; } = [];
    public List<SearchPublicChatResult> PublicChats { get; } = [];
    public string? Error { get; set; }
    public bool IsLoadingMoreMessages { get; set; }
    public bool HasMoreMessages { get; set; }
    public string NextMessageOffset { get; set; } = "";
}
