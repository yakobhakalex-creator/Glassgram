using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public bool CanSendBasicMessages { get; private set; } = true;
    public bool CanSendPhotos { get; private set; } = true;
    public bool CanSendVideos { get; private set; } = true;
    public bool CanSendDocuments { get; private set; } = true;

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
        var permissions = chat.Field("permissions");
        if (permissions.ValueKind == JsonValueKind.Object)
        {
            CanSendBasicMessages = permissions.Field("can_send_basic_messages").Flag();
            CanSendPhotos = permissions.Field("can_send_photos").Flag();
            CanSendVideos = permissions.Field("can_send_videos").Flag();
            CanSendDocuments = permissions.Field("can_send_documents").Flag();
        }
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
    private static readonly Regex LinkPattern = new(@"https?://[^\s<>()]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    public string RawJson { get; init; } = "";
    public string MediaKind { get; private set; } = "";
    public string StickerFormat { get; private set; } = "";
    public int PrimaryFileId { get; private set; }
    public int PreviewFileId { get; private set; }
    public string MediaPath { get; private set; } = "";
    public string PreviewPath { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string MimeType { get; private set; } = "";
    public long FileSize { get; private set; }
    public double TransferProgress { get; private set; }
    public string TransferText { get; private set; } = "";
    public ImageSource? MediaPreview { get; private set; }
    public string PrimaryLink => LinkPattern.Match(Text) is { Success: true } match ? match.Value : "";
    public Visibility LinkVisibility => PrimaryLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaVisibility => MediaKind.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => MediaKind == "photo" || MediaKind == "animation" && !MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || MediaKind == "sticker" && StickerFormat == "stickerFormatWebp" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoVisibility => MediaKind == "video" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InlineVideoVisibility => MediaKind == "sticker" && StickerFormat == "stickerFormatWebm" || MediaKind == "animation" && MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TgsVisibility => MediaKind == "sticker" && StickerFormat == "stickerFormatTgs" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DocumentVisibility => MediaKind == "document" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransferVisibility => TransferText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FileSizeText => FileSize <= 0 ? "" : FileSize < 1024 * 1024 ? $"{FileSize / 1024d:0.#} КБ" : $"{FileSize / 1024d / 1024d:0.#} МБ";
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
        var row = new MessageRow
        {
            Id = message.Field("id").Number(),
            ChatId = message.Field("chat_id").Number(),
            Date = message.Field("date").LocalDate(),
            Text = TdJson.MessageText(message),
            Quote = quote,
            Author = sender.Field("@type").Text() == "messageSenderChat" ? "Канал" : "",
            Outgoing = message.Field("is_outgoing").Flag(),
            Pending = message.Field("sending_state").Field("@type").Text() == "messageSendingStatePending",
            Failed = message.Field("sending_state").Field("@type").Text() == "messageSendingStateFailed",
            RawJson = message.GetRawText()
        };
        row.ReadMedia(message.Field("content"));
        return row;
    }

    private void ReadMedia(JsonElement content)
    {
        var type = content.Field("@type").Text();
        JsonElement primary = default;
        JsonElement preview = default;
        switch (type)
        {
            case "messagePhoto":
                MediaKind = "photo";
                var sizes = content.Field("photo").Field("sizes").Items().OrderBy(size => size.Field("width").Number() * size.Field("height").Number()).ToArray();
                if (sizes.Length > 0)
                {
                    primary = sizes[^1].Field("photo");
                    preview = sizes.FirstOrDefault(size => size.Field("width").Number() >= 320).Field("photo");
                    if (preview.ValueKind == JsonValueKind.Undefined) preview = sizes[Math.Min(sizes.Length - 1, sizes.Length / 2)].Field("photo");
                }
                break;
            case "messageAnimation":
                MediaKind = "animation";
                var animation = content.Field("animation");
                primary = animation.Field("animation");
                preview = animation.Field("thumbnail").Field("file");
                FileName = animation.Field("file_name").Text();
                MimeType = animation.Field("mime_type").Text();
                break;
            case "messageVideo":
                MediaKind = "video";
                var video = content.Field("video");
                primary = video.Field("video");
                preview = video.Field("thumbnail").Field("file");
                FileName = video.Field("file_name").Text();
                MimeType = video.Field("mime_type").Text();
                break;
            case "messageSticker":
                MediaKind = "sticker";
                var sticker = content.Field("sticker");
                primary = sticker.Field("sticker");
                preview = sticker.Field("thumbnail").Field("file");
                StickerFormat = sticker.Field("format").Field("@type").Text();
                FileName = sticker.Field("emoji").Text() + " sticker";
                break;
            case "messageDocument":
                MediaKind = "document";
                var document = content.Field("document");
                primary = document.Field("document");
                preview = document.Field("thumbnail").Field("file");
                FileName = document.Field("file_name").Text();
                MimeType = document.Field("mime_type").Text();
                break;
        }
        ApplyInitialFile(primary, true);
        ApplyInitialFile(preview, false);
    }

    private void ApplyInitialFile(JsonElement file, bool primary)
    {
        if (file.ValueKind != JsonValueKind.Object) return;
        var id = (int)file.Field("id").Number();
        if (primary)
        {
            PrimaryFileId = id;
            FileSize = file.Field("size").Number();
            MediaPath = file.Field("local").Field("path").Text();
        }
        else
        {
            PreviewFileId = id;
            PreviewPath = file.Field("local").Field("path").Text();
        }
        RefreshMediaPreview();
    }

    public void ApplyFile(JsonElement file)
    {
        var id = (int)file.Field("id").Number();
        var local = file.Field("local");
        var path = local.Field("path").Text();
        if (id == PrimaryFileId && path.Length > 0) MediaPath = path;
        if (id == PreviewFileId && path.Length > 0) PreviewPath = path;
        if (id == PrimaryFileId)
        {
            var expected = Math.Max(1, file.Field("expected_size").Number());
            var downloaded = local.Field("downloaded_size").Number();
            TransferProgress = Math.Clamp(downloaded * 100d / expected, 0, 100);
            TransferText = local.Field("is_downloading_active").Flag() ? $"Загрузка {TransferProgress:0}%" : "";
        }
        RefreshMediaPreview();
        RaiseMedia();
    }

    private void RefreshMediaPreview()
    {
        var path = PreviewPath.Length > 0 ? PreviewPath : MediaKind is "photo" or "animation" || StickerFormat == "stickerFormatWebp" ? MediaPath : "";
        if (path.Length == 0 || !File.Exists(path)) return;
        try { MediaPreview = new BitmapImage(new Uri(path)); }
        catch (UriFormatException) { }
    }

    private void RaiseMedia()
    {
        foreach (var name in new[] { nameof(MediaPreview), nameof(MediaPath), nameof(PreviewPath), nameof(TransferProgress), nameof(TransferText), nameof(TransferVisibility), nameof(FileSizeText) })
            PropertyChanged?.Invoke(this, new(name));
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
