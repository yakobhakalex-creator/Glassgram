using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glassgram.Telegram;

internal static class TdJson
{
    public static JsonElement Field(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;

    public static string Text(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public static long Number(this JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
    public static bool Flag(this JsonElement value) => value.ValueKind is JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    public static DateTime LocalDate(this JsonElement value)
    {
        var seconds = value.Number();
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : DateTime.MinValue;
    }

    public static string MessageText(this JsonElement message)
    {
        return ContentText(message.Field("content"));
    }

    public static string ContentText(this JsonElement content)
    {
        var text = content.Field("text").Field("text").Text();
        if (!string.IsNullOrEmpty(text)) return text;
        var caption = content.Field("caption").Field("text").Text();
        if (!string.IsNullOrEmpty(caption)) return caption;
        return content.Field("@type").Text() switch
        {
            "messagePhoto" => "Фото",
            "messageVideo" => "Видео",
            "messageAnimation" => "GIF",
            "messageSticker" => "Стикер",
            "messageVoiceNote" => "Голосовое сообщение",
            "messageAudio" => "Аудио",
            "messageDocument" => "Файл",
            _ => "Сообщение"
        };
    }
}

internal sealed class ChatRow(long id) : INotifyPropertyChanged
{
    public long Id { get; } = id;
    public string Title { get; private set; } = "";
    public string Preview { get; private set; } = "";
    public string TimeText { get; private set; } = "";
    public string UnreadText { get; private set; } = "";
    public Visibility UnreadVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public int UnreadCount { get; private set; }
    public string AvatarLetter => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "•";
    public ImageSource? Avatar { get; private set; }
    public long Order { get; private set; }
    public bool IsMain { get; private set; }
    public bool IsArchive { get; private set; }
    public bool IsSaved { get; set; }
    public long UserId { get; private set; }
    public string Kind { get; private set; } = "";
    public long LastReadOutboxId { get; private set; }
    public int PhotoFileId { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(JsonElement chat)
    {
        Title = IsSaved ? "Избранное" : chat.Field("title").Text();
        Preview = chat.Field("last_message").ValueKind is JsonValueKind.Object ? TdJson.MessageText(chat.Field("last_message")) : "";
        var date = chat.Field("last_message").Field("date").LocalDate();
        TimeText = date == DateTime.MinValue ? "" : date.Date == DateTime.Today ? date.ToString("HH:mm") : date.ToString("dd.MM.yy");
        UnreadCount = (int)chat.Field("unread_count").Number();
        UnreadText = UnreadCount > 99 ? "99+" : UnreadCount.ToString();
        LastReadOutboxId = chat.Field("last_read_outbox_message_id").Number();
        UserId = chat.Field("type").Field("@type").Text() == "chatTypePrivate" ? chat.Field("type").Field("user_id").Number() : 0;
        Kind = chat.Field("type").Field("@type").Text();
        Order = Math.Max(Order, chat.Field("last_message").Field("date").Number());
        foreach (var list in chat.Field("chat_lists").Items())
        {
            if (list.Field("@type").Text() == "chatListMain") IsMain = true;
            if (list.Field("@type").Text() == "chatListArchive") IsArchive = true;
        }
        foreach (var position in chat.Field("positions").Items())
        {
            var list = position.Field("list").Field("@type").Text();
            var order = position.Field("order").Number();
            if (list == "chatListMain") { IsMain = order != 0; Order = Math.Max(Order, order); }
            if (list == "chatListArchive") { IsArchive = order != 0; Order = Math.Max(Order, order); }
        }
        var small = chat.Field("photo").Field("small");
        PhotoFileId = (int)small.Field("id").Number();
        SetAvatar(small.Field("local").Field("path").Text());
        RaiseAll();
    }

    public void ApplyList(string listType, bool present = true, long order = 0)
    {
        if (listType == "chatListMain") IsMain = present;
        if (listType == "chatListArchive") IsArchive = present;
        if (present) Order = Math.Max(Order, order);
    }

    public void SetAvatar(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Avatar = new BitmapImage(new Uri(path)); PropertyChanged?.Invoke(this, new(nameof(Avatar))); }
        catch (UriFormatException) { }
    }

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Title), nameof(Preview), nameof(TimeText), nameof(UnreadText), nameof(UnreadVisibility), nameof(AvatarLetter) })
            PropertyChanged?.Invoke(this, new(name));
    }
}

internal sealed class MessageRow : INotifyPropertyChanged
{
    public long Id { get; init; }
    public long ChatId { get; init; }
    public DateTime Date { get; init; }
    public string Text { get; init; } = "";
    public string Quote { get; init; } = "";
    public string Author { get; init; } = "";
    public bool Outgoing { get; init; }
    public bool Pending { get; init; }
    public bool Failed { get; init; }
    public bool IsDate { get; init; }
    public Visibility DateVisibility => IsDate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BubbleVisibility => IsDate ? Visibility.Collapsed : Visibility.Visible;
    public Visibility QuoteVisibility => Quote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public HorizontalAlignment Alignment => Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public string TimeText => Date == DateTime.MinValue ? "" : Date.ToString("HH:mm");
    public string DayText => Date.ToString("d MMMM yyyy");
    public string Status { get; private set; } = "";
    public Brush BubbleBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 48, 65, 82));
    public event PropertyChangedEventHandler? PropertyChanged;

    public static MessageRow FromJson(JsonElement message)
    {
        var reply = message.Field("reply_to");
        var quote = reply.Field("quote").Field("text").Field("text").Text();
        if (quote.Length == 0 && reply.Field("content").ValueKind == JsonValueKind.Object)
            quote = TdJson.ContentText(reply.Field("content"));
        if (reply.ValueKind == JsonValueKind.Undefined || reply.ValueKind == JsonValueKind.Null) quote = "";
        var sender = message.Field("sender_id");
        return new MessageRow
        {
            Id = message.Field("id").Number(),
            ChatId = message.Field("chat_id").Number(),
            Date = message.Field("date").LocalDate(),
            Text = TdJson.MessageText(message),
            Quote = quote,
            Author = sender.Field("@type").Text() == "messageSenderChat" ? "Канал" : "",
            Outgoing = message.Field("is_outgoing").Flag(),
            Pending = message.Field("sending_state").Field("@type").Text() == "messageSendingStatePending",
            Failed = message.Field("sending_state").Field("@type").Text() == "messageSendingStateFailed"
        };
    }

    public void UpdateAppearance(bool light, long lastReadOutbox)
    {
        Status = !Outgoing ? "" : Failed ? "!" : Pending ? "◷" : Id <= lastReadOutbox ? "✓✓" : "✓";
        var color = Outgoing
            ? light ? Windows.UI.Color.FromArgb(230, 195, 226, 245) : Windows.UI.Color.FromArgb(210, 43, 101, 132)
            : light ? Windows.UI.Color.FromArgb(240, 255, 255, 255) : Windows.UI.Color.FromArgb(185, 52, 68, 84);
        BubbleBrush = new SolidColorBrush(color);
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(BubbleBrush)));
    }
}
