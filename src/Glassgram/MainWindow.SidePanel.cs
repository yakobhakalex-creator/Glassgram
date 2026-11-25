using System.Collections.ObjectModel;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

// Interaction and paging patterns adapted from Unigram (GPL-3.0), commit
// 9354bcb753be3b16086bd3886b8adec062ccdd91. See THIRD_PARTY_NOTICES.md.

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<StickerPanelItem> panelStickers = [];
    private readonly ObservableCollection<StickerSetPanelItem> panelStickerSets = [];
    private readonly ObservableCollection<SharedMediaItem> profileSharedItems = [];
    private int stickerLoadVersion;
    private long sharedFromMessageId;
    private string sharedFilter = "searchMessagesFilterPhotoAndVideo";

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
        ProfileKind.Text = activeChat.UserId != 0 ? "Пользователь" : activeChat.BasicGroupId != 0 ? "Группа" : "Канал или супергруппа";
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
                ProfileKind.Text = user.Field("type").Field("@type").Text() == "userTypeBot" ? "Бот" : "Пользователь";
            }
            else if (activeChat.BasicGroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getBasicGroup", basic_group_id = activeChat.BasicGroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getBasicGroupFullInfo", basic_group_id = activeChat.BasicGroupId }, lifetime.Token);
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
                ProfileKind.Text = "Группа";
            }
            else if (activeChat.SupergroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getSupergroup", supergroup_id = activeChat.SupergroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getSupergroupFullInfo", supergroup_id = activeChat.SupergroupId }, lifetime.Token);
                ProfileUsername.Text = Username(group.Field("usernames"));
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
                ProfileKind.Text = group.Field("is_channel").Flag() ? "Канал" : "Супергруппа";
            }
            var counts = await Task.WhenAll(
                GetSharedCountAsync("searchMessagesFilterPhotoAndVideo"),
                GetSharedCountAsync("searchMessagesFilterDocument"),
                GetSharedCountAsync("searchMessagesFilterUrl"));
            ProfileMediaCount.Text = counts[0].ToString();
            ProfileFileCount.Text = counts[1].ToString();
            ProfileLinkCount.Text = counts[2].ToString();
            ProfileSharedCategory.SelectedIndex = 0;
            await LoadSharedMediaAsync(true);
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

    private async void ProfileSharedCategoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProfilePanel?.Visibility != Visibility.Visible) return;
        sharedFilter = (ProfileSharedCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "searchMessagesFilterPhotoAndVideo";
        await LoadSharedMediaAsync(true);
    }

    private async void LoadMoreSharedMedia(object sender, RoutedEventArgs args) => await LoadSharedMediaAsync(false);

    private async Task LoadSharedMediaAsync(bool reset)
    {
        if (telegram is null || activeChat is null) return;
        if (reset) { sharedFromMessageId = 0; profileSharedItems.Clear(); }
        ProfileSharedGrid.ItemsSource = profileSharedItems;
        ProfileSharedStatus.Text = "Загрузка…";
        try
        {
            var result = await telegram.RequestAsync(new
            {
                @type = "searchChatMessages", chat_id = activeChat.Id, topic_id = (object?)null,
                query = "", sender_id = (object?)null, from_message_id = sharedFromMessageId,
                offset = 0, limit = 30,
                filter = new Dictionary<string, object?> { ["@type"] = sharedFilter },
                message_thread_id = 0L, saved_messages_topic_id = 0L
            }, lifetime.Token);
            foreach (var message in result.Field("messages").Items())
            {
                var row = MessageRow.FromJson(message);
                profileSharedItems.Add(new SharedMediaItem(row, row.MediaPreview, row.FileName.Length > 0 ? row.FileName : row.Text, row.PrimaryLink));
            }
            sharedFromMessageId = result.Field("next_from_message_id").Number();
            ProfileSharedMoreButton.Visibility = sharedFromMessageId != 0 ? Visibility.Visible : Visibility.Collapsed;
            ProfileSharedStatus.Text = profileSharedItems.Count == 0 ? "Ничего не найдено" : $"Показано: {profileSharedItems.Count}";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (!lifetime.IsCancellationRequested) ProfileSharedStatus.Text = "Не удалось загрузить: " + ex.Message;
        }
    }

    private async void OpenSharedMedia(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not SharedMediaItem item) return;
        if (!string.IsNullOrWhiteSpace(item.Link)) { await Windows.System.Launcher.LaunchUriAsync(new Uri(item.Link)); return; }
        var source = new Border { DataContext = item.Message };
        if (item.Message.MediaKind == "document") DownloadOrOpenDocument(source, new RoutedEventArgs());
        else OpenMessageMedia(source, new TappedRoutedEventArgs());
    }

    private async void OpenStickerPanel(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is null) return;
        ShowSidePanel("Стикеры");
        ProfilePanel.Visibility = Visibility.Collapsed;
        StickersPanel.Visibility = Visibility.Visible;
        StickerGrid.ItemsSource = panelStickers;
        StickerSetList.ItemsSource = panelStickerSets;
        EmojiCategory.ItemsSource = UnigramEmojiCatalog.Groups;
        EmojiCategory.SelectedIndex = Math.Max(0, EmojiCategory.SelectedIndex);
        EmojiGrid.ItemsSource = UnigramEmojiCatalog.Groups[EmojiCategory.SelectedIndex].Items;
        await LoadStickersAsync((StickerCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent");
    }

    private async void StickerCategoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (StickersPanel?.Visibility != Visibility.Visible) return;
        await LoadStickersAsync((StickerCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent");
    }

    private void EmojiCategoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (EmojiCategory.SelectedItem is UnigramEmojiGroup group) EmojiGrid.ItemsSource = group.Items;
    }

    private async void StickerSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter) await SearchStickersAsync();
    }

    private async void SearchStickers(object sender, RoutedEventArgs args) => await SearchStickersAsync();

    private async Task SearchStickersAsync()
    {
        if (telegram is null || string.IsNullOrWhiteSpace(StickerSearch.Text)) return;
        var version = ++stickerLoadVersion;
        panelStickers.Clear(); panelStickerSets.Clear();
        try
        {
            var result = await telegram.RequestAsync(new { @type = "searchStickers", sticker_type = new Dictionary<string, object?> { ["@type"] = "stickerTypeRegular" }, emojis = StickerSearch.Text.Trim(), query = "", input_language_codes = Array.Empty<string>(), offset = 0, limit = 100 }, lifetime.Token);
            foreach (var sticker in result.Field("stickers").Items())
            {
                if (version != stickerLoadVersion) return;
                if (await CreateStickerItemAsync(sticker) is { } item) panelStickers.Add(item);
            }
            SidePanelTitle.Text = "Поиск стикеров";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or IOException) { MessengerStatus.Text = "Поиск стикеров не выполнен. " + ex.Message; }
    }

    private async void OpenStickerSet(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not StickerSetPanelItem set || telegram is null) return;
        var version = ++stickerLoadVersion; panelStickers.Clear();
        try
        {
            var full = await telegram.RequestAsync(new { @type = "getStickerSet", set_id = set.Id }, lifetime.Token);
            foreach (var sticker in full.Field("stickers").Items())
            {
                if (version != stickerLoadVersion) return;
                if (await CreateStickerItemAsync(sticker) is { } item) panelStickers.Add(item);
            }
            SidePanelTitle.Text = set.Title;
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or IOException) { MessengerStatus.Text = "Набор не загружен. " + ex.Message; }
    }

    private async Task LoadStickersAsync(string category)
    {
        var emoji = category == "emoji";
        EmojiGrid.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        StickerGrid.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
        EmojiCategory.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        StickerSetList.Visibility = !emoji && category == "installed" ? Visibility.Visible : Visibility.Collapsed;
        if (emoji)
        {
            stickerLoadVersion++;
            SidePanelTitle.Text = "Эмодзи";
            return;
        }
        if (telegram is null) return;
        var version = ++stickerLoadVersion;
        panelStickers.Clear();
        panelStickerSets.Clear();
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
                    panelStickerSets.Add(new StickerSetPanelItem(set.Field("id").Number(), set.Field("title").Text()));
                    var full = await telegram.RequestAsync(new { @type = "getStickerSet", set_id = set.Field("id").Number() }, lifetime.Token);
                    stickers.AddRange(full.Field("stickers").Items().Take(8));
                    if (stickers.Count >= 96) break;
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
        preferences.RecentEmoji.Remove(emoji);
        preferences.RecentEmoji.Insert(0, emoji);
        if (preferences.RecentEmoji.Count > 40) preferences.RecentEmoji.RemoveRange(40, preferences.RecentEmoji.Count - 40);
        SavePreferences();
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
internal sealed record StickerSetPanelItem(long Id, string Title);
internal sealed record SharedMediaItem(MessageRow Message, ImageSource? Preview, string Title, string Link);
