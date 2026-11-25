using System.Collections.ObjectModel;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<StickerPanelItem> panelStickers = [];
    private static readonly string[] TelegramEmoji =
    [
        "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "😊", "😇", "🙂", "🙃",
        "😉", "😌", "😍", "🥰", "😘", "😗", "😙", "😚", "😋", "😛", "😝", "😜",
        "🤪", "🤨", "🧐", "🤓", "😎", "🤩", "🥳", "😏", "😒", "😞", "😔", "😟",
        "😕", "🙁", "☹️", "😣", "😖", "😫", "😩", "🥺", "😢", "😭", "😤", "😠",
        "😡", "🤬", "🤯", "😳", "🥵", "🥶", "😱", "😨", "😰", "😥", "😓", "🤗",
        "🤔", "🤭", "🤫", "🤥", "😶", "😐", "😑", "😬", "🙄", "😯", "😦", "😧",
        "😮", "😲", "🥱", "😴", "🤤", "😪", "😵", "🤐", "🥴", "🤢", "🤮", "🤧",
        "😷", "🤒", "🤕", "👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "👏", "🙌",
        "👐", "🤲", "🤝", "🙏", "💪", "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤",
        "🤍", "🤎", "💔", "❣️", "💕", "💞", "💓", "💗", "💖", "💘", "💝", "💯",
        "🔥", "✨", "🎉", "🎊", "✅", "❌", "⚠️", "💬", "👀", "🚀", "🎁", "⭐"
    ];
    private int stickerLoadVersion;

    private void ShowSidePanel(string title)
    {
        var width = Math.Clamp(ChatsPage.ActualWidth * 0.28, 280, 340);
        FutureSidePanelColumn.Width = new GridLength(width);
        FutureSidePanel.Visibility = Visibility.Visible;
        SidePanelTitle.Text = title;
    }

    private void CloseSidePanel(object sender, RoutedEventArgs args)
    {
        stickerLoadVersion++;
        FutureSidePanel.Visibility = Visibility.Collapsed;
        FutureSidePanelColumn.Width = new GridLength(0);
        ProfilePanel.Visibility = StickersPanel.Visibility = Visibility.Collapsed;
    }

    private async void OpenProfilePanel(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is null) return;
        ShowSidePanel("Профиль");
        StickersPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
        ProfileName.Text = activeChat.Title;
        ProfileAvatarLetter.Text = activeChat.AvatarLetter;
        ProfileAvatarBrush.ImageSource = activeChat.Avatar;
        ProfileUsername.Text = "";
        ProfileDescription.Text = "";
        ProfileStatus.Text = ActiveChatStatus.Text;
        try
        {
            if (activeChat.UserId != 0)
            {
                var user = await telegram.RequestAsync(new { @type = "getUser", user_id = activeChat.UserId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getUserFullInfo", user_id = activeChat.UserId }, lifetime.Token);
                ProfileName.Text = string.Join(' ', new[] { user.Field("first_name").Text(), user.Field("last_name").Text() }.Where(x => x.Length > 0));
                ProfileUsername.Text = Username(user.Field("usernames"));
                ProfileDescription.Text = full.Field("bio").Field("text").Text();
                ProfileStatus.Text = DescribeStatus(user.Field("status"));
            }
            else if (activeChat.BasicGroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getBasicGroup", basic_group_id = activeChat.BasicGroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getBasicGroupFullInfo", basic_group_id = activeChat.BasicGroupId }, lifetime.Token);
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
            }
            else if (activeChat.SupergroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getSupergroup", supergroup_id = activeChat.SupergroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getSupergroupFullInfo", supergroup_id = activeChat.SupergroupId }, lifetime.Token);
                ProfileUsername.Text = Username(group.Field("usernames"));
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
            }
            var counts = await Task.WhenAll(
                GetSharedCountAsync("searchMessagesFilterPhotoAndVideo"),
                GetSharedCountAsync("searchMessagesFilterDocument"),
                GetSharedCountAsync("searchMessagesFilterUrl"));
            ProfileMediaCount.Text = counts[0].ToString();
            ProfileFileCount.Text = counts[1].ToString();
            ProfileLinkCount.Text = counts[2].ToString();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (!lifetime.IsCancellationRequested) ProfileDescription.Text = "Не удалось загрузить профиль: " + ex.Message;
        }
    }

    private static string Username(JsonElement usernames)
    {
        var value = usernames.Field("active_usernames").Items().Select(x => x.Text()).FirstOrDefault(x => x.Length > 0);
        return value is null ? "" : "@" + value;
    }

    private async Task<long> GetSharedCountAsync(string filterType)
    {
        if (telegram is null || activeChat is null) return 0;
        var result = await telegram.RequestAsync(new { @type = "getChatMessageCount", chat_id = activeChat.Id, filter = new Dictionary<string, object?> { ["@type"] = filterType }, return_local = false }, lifetime.Token);
        return result.Field("count").Number();
    }

    private async void OpenStickerPanel(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is null) return;
        ShowSidePanel("Стикеры");
        ProfilePanel.Visibility = Visibility.Collapsed;
        StickersPanel.Visibility = Visibility.Visible;
        StickerGrid.ItemsSource = panelStickers;
        EmojiGrid.ItemsSource = TelegramEmoji;
        await LoadStickersAsync((StickerCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent");
    }

    private async void StickerCategoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (StickersPanel?.Visibility != Visibility.Visible) return;
        await LoadStickersAsync((StickerCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent");
    }

    private async Task LoadStickersAsync(string category)
    {
        var emoji = category == "emoji";
        EmojiGrid.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        StickerGrid.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
        if (emoji)
        {
            stickerLoadVersion++;
            SidePanelTitle.Text = "Эмодзи";
            return;
        }
        if (telegram is null) return;
        var version = ++stickerLoadVersion;
        panelStickers.Clear();
        SidePanelTitle.Text = category switch { "favorite" => "Избранные стикеры", "installed" => "Наборы стикеров", _ => "Недавние стикеры" };
        try
        {
            var stickers = new List<JsonElement>();
            if (category == "favorite")
                stickers.AddRange((await telegram.RequestAsync(new { @type = "getFavoriteStickers" }, lifetime.Token)).Field("stickers").Items());
            else if (category == "installed")
            {
                var sets = (await telegram.RequestAsync(new { @type = "getInstalledStickerSets", sticker_type = new Dictionary<string, object?> { ["@type"] = "stickerTypeRegular" } }, lifetime.Token)).Field("sets").Items().Take(12).ToArray();
                foreach (var set in sets)
                {
                    var full = await telegram.RequestAsync(new { @type = "getStickerSet", set_id = set.Field("id").Number() }, lifetime.Token);
                    stickers.AddRange(full.Field("stickers").Items().Take(24));
                    if (stickers.Count >= 144) break;
                }
            }
            else
                stickers.AddRange((await telegram.RequestAsync(new { @type = "getRecentStickers", is_attached = false }, lifetime.Token)).Field("stickers").Items());

            foreach (var sticker in stickers.Take(144))
            {
                if (version != stickerLoadVersion) return;
                var item = await CreateStickerItemAsync(sticker);
                if (item is not null) panelStickers.Add(item);
            }
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or IOException)
        {
            if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Не удалось загрузить стикеры. " + ex.Message;
        }
    }

    private void InsertEmoji(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not string emoji) return;
        var start = Math.Clamp(MessageInput.SelectionStart, 0, MessageInput.Text.Length);
        MessageInput.Text = MessageInput.Text.Insert(start, emoji);
        MessageInput.SelectionStart = start + emoji.Length;
        MessageInput.Focus(FocusState.Programmatic);
    }

    private async Task<StickerPanelItem?> CreateStickerItemAsync(JsonElement sticker)
    {
        if (telegram is null) return null;
        var file = sticker.Field("sticker");
        var fileId = (int)file.Field("id").Number();
        if (fileId == 0) return null;
        var previewFile = sticker.Field("thumbnail").Field("file");
        var previewId = (int)previewFile.Field("id").Number();
        var path = previewFile.Field("local").Field("path").Text();
        if (previewId == 0) { previewId = fileId; path = file.Field("local").Field("path").Text(); }
        if (path.Length == 0 || !File.Exists(path))
        {
            var downloaded = await telegram.RequestAsync(new { @type = "downloadFile", file_id = previewId, priority = 1, offset = 0, limit = 0, synchronous = true }, lifetime.Token);
            path = downloaded.Field("local").Field("path").Text();
        }
        ImageSource? preview = null;
        if (path.Length > 0 && File.Exists(path))
        {
            try { preview = new BitmapImage(new Uri(path)); } catch (Exception ex) when (ex is ArgumentException or UriFormatException) { }
        }
        return new StickerPanelItem(fileId, file.Field("remote").Field("id").Text(), sticker.Field("emoji").Text(), (int)sticker.Field("width").Number(), (int)sticker.Field("height").Number(), preview);
    }

    private async void SendSticker(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not StickerPanelItem sticker || telegram is null || activeChat is null) return;
        try
        {
            var downloaded = await telegram.RequestAsync(new { @type = "downloadFile", file_id = sticker.FileId, priority = 32, offset = 0, limit = 0, synchronous = true }, lifetime.Token);
            var path = downloaded.Field("local").Field("path").Text();
            if (path.Length == 0 || !File.Exists(path)) throw new IOException("TDLib не загрузила оригинал стикера.");
            var inputFile = new Dictionary<string, object?> { ["@type"] = "inputFileLocal", ["path"] = path };
            var inputSticker = new Dictionary<string, object?>
            {
                ["@type"] = "inputSticker",
                ["sticker"] = inputFile,
                ["thumbnail"] = null,
                ["width"] = sticker.Width,
                ["height"] = sticker.Height
            };
            var content = new Dictionary<string, object?>
            {
                ["@type"] = "inputMessageSticker",
                ["sticker"] = inputSticker,
                ["emoji"] = sticker.Emoji
            };
            var message = await telegram.RequestAsync(new
            {
                @type = "sendMessage", chat_id = activeChat.Id, topic_id = (object?)null, reply_to = (object?)null,
                options = (object?)null, reply_markup = (object?)null, input_message_content = content
            }, lifetime.Token);
            InsertMessage(message);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException) { MessengerStatus.Text = "Стикер не отправлен. " + ex.Message; }
    }
}

internal sealed record StickerPanelItem(int FileId, string RemoteId, string Emoji, int Width, int Height, ImageSource? Preview);
