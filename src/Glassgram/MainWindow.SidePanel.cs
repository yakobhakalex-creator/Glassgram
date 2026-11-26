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
    private int profileLoadVersion;
    private int sharedLoadVersion;
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
        profileLoadVersion++;
        sharedLoadVersion++;
        panelStickers.Clear();
        FutureSidePanel.Visibility = Visibility.Collapsed;
        FutureSidePanelColumn.Width = new GridLength(0);
        ProfilePanel.Visibility = StickersPanel.Visibility = PinnedMessagesPanel.Visibility = Visibility.Collapsed;
    }

    private async void OpenProfilePanel(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is not { } chat) return;
        var version = ++profileLoadVersion;
        stickerLoadVersion++;
        panelStickers.Clear();
        ProfilePanel.Visibility = Visibility.Collapsed;
        PinnedMessagesPanel.Visibility = Visibility.Collapsed;
        ProfileSharedCategory.SelectedIndex = 0;
        sharedFilter = "searchMessagesFilterPhotoAndVideo";
        ShowSidePanel("Профиль");
        StickersPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
        ProfileName.Text = chat.Title;
        ProfileAvatarLetter.Text = chat.AvatarLetter;
        ProfileAvatarBrush.ImageSource = chat.Avatar;
        ProfileUsername.Text = "";
        ProfileDescription.Text = "";
        ProfileMembersSection.Visibility = Visibility.Collapsed;
        ProfileMemberSearch.Text = "";
        memberQuery = "";
        memberCategory = "members";
        memberLoadGeneration++;
        profileMembers.Clear();
        ProfileStatus.Text = ActiveChatStatus.Text;
        ProfileKind.Text = chat.UserId != 0 ? "Пользователь" : chat.BasicGroupId != 0 ? "Группа" : "Канал или супергруппа";
        ProfileMediaCount.Text = ProfileFileCount.Text = ProfileLinkCount.Text = "…";
        var shared = LoadSharedMediaAsync(true);
        bool IsCurrent() => version == profileLoadVersion && activeChat?.Id == chat.Id;
        try
        {
            if (chat.UserId != 0)
            {
                var user = await telegram.RequestAsync(new { @type = "getUser", user_id = chat.UserId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getUserFullInfo", user_id = chat.UserId }, lifetime.Token);
                if (!IsCurrent()) return;
                ProfileName.Text = string.Join(' ', new[] { user.Field("first_name").Text(), user.Field("last_name").Text() }.Where(x => x.Length > 0));
                ProfileUsername.Text = Username(user.Field("usernames"));
                ProfileDescription.Text = full.Field("bio").Field("text").Text();
                ProfileStatus.Text = DescribeStatus(user.Field("status"));
                ProfileKind.Text = user.Field("type").Field("@type").Text() == "userTypeBot" ? "Бот" : "Пользователь";
            }
            else if (chat.BasicGroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getBasicGroup", basic_group_id = chat.BasicGroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getBasicGroupFullInfo", basic_group_id = chat.BasicGroupId }, lifetime.Token);
                if (!IsCurrent()) return;
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
                ProfileKind.Text = "Группа";
                await PrepareMembersAsync(chat, full);
            }
            else if (chat.SupergroupId != 0)
            {
                var group = await telegram.RequestAsync(new { @type = "getSupergroup", supergroup_id = chat.SupergroupId }, lifetime.Token);
                var full = await telegram.RequestAsync(new { @type = "getSupergroupFullInfo", supergroup_id = chat.SupergroupId }, lifetime.Token);
                if (!IsCurrent()) return;
                ProfileUsername.Text = Username(group.Field("usernames"));
                ProfileStatus.Text = $"{group.Field("member_count").Number()} участников";
                ProfileDescription.Text = full.Field("description").Text();
                ProfileKind.Text = group.Field("is_channel").Flag() ? "Канал" : "Супергруппа";
                await PrepareMembersAsync(chat, full);
            }
            var counts = await Task.WhenAll(
                GetSharedCountAsync(chat.Id, "searchMessagesFilterPhotoAndVideo"),
                GetSharedCountAsync(chat.Id, "searchMessagesFilterDocument"),
                GetSharedCountAsync(chat.Id, "searchMessagesFilterUrl"));
            if (!IsCurrent()) return;
            ProfileMediaCount.Text = counts[0].ToString();
            ProfileFileCount.Text = counts[1].ToString();
            ProfileLinkCount.Text = counts[2].ToString();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (IsCurrent() && !lifetime.IsCancellationRequested) ProfileDescription.Text = "Не удалось загрузить профиль: " + ex.Message;
        }
        finally { await shared; }
    }

    private static string Username(JsonElement usernames)
    {
        var value = usernames.Field("active_usernames").Items().Select(x => x.Text()).FirstOrDefault(x => x.Length > 0);
        return value is null ? "" : "@" + value;
    }

    private async Task<long> GetSharedCountAsync(long chatId, string filterType)
    {
        if (telegram is null) return 0;
        var result = await telegram.RequestAsync(new { @type = "getChatMessageCount", chat_id = chatId, topic_id = (object?)null, filter = new Dictionary<string, object?> { ["@type"] = filterType }, return_local = false }, lifetime.Token);
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
        if (telegram is null || activeChat is not { } chat || (!reset && sharedFromMessageId == 0)) return;
        var version = ++sharedLoadVersion;
        var filter = sharedFilter;
        bool IsCurrent() => version == sharedLoadVersion && activeChat?.Id == chat.Id;
        if (reset) { sharedFromMessageId = 0; profileSharedItems.Clear(); }
        ProfileSharedGrid.ItemsSource = profileSharedItems;
        ProfileSharedMoreButton.IsEnabled = false;
        ProfileSharedStatus.Text = "Загрузка…";
        try
        {
            var result = await telegram.RequestAsync(new
            {
                @type = "searchChatMessages", chat_id = chat.Id, topic_id = (object?)null,
                query = "", sender_id = (object?)null, from_message_id = sharedFromMessageId,
                offset = 0, limit = 30,
                filter = new Dictionary<string, object?> { ["@type"] = filter }
            }, lifetime.Token);
            if (!IsCurrent()) return;
            foreach (var message in result.Field("messages").Items())
            {
                var row = MessageRow.FromJson(message);
                var link = filter == "searchMessagesFilterUrl" ? GetSharedLink(message, row) : "";
                var title = link.Length > 0 ? link : row.FileName.Length > 0 ? row.FileName : row.Text;
                profileSharedItems.Add(new SharedMediaItem(row, title, link));
            }
            sharedFromMessageId = result.Field("next_from_message_id").Number();
            ProfileSharedMoreButton.Visibility = sharedFromMessageId != 0 ? Visibility.Visible : Visibility.Collapsed;
            ProfileSharedStatus.Text = profileSharedItems.Count == 0 ? "Ничего не найдено" : $"Показано: {profileSharedItems.Count}";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (IsCurrent() && !lifetime.IsCancellationRequested) ProfileSharedStatus.Text = "Не удалось загрузить: " + ex.Message;
        }
        finally { if (IsCurrent()) ProfileSharedMoreButton.IsEnabled = true; }
    }

    private static string GetSharedLink(JsonElement message, MessageRow row)
    {
        var content = message.Field("content");
        var previewUrl = content.Field("link_preview").Field("url").Text();
        if (previewUrl.Length > 0) return previewUrl;
        foreach (var name in new[] { "text", "caption" })
        {
            var text = content.Field(name).Field("text").Text();
            foreach (var entity in content.Field(name).Field("entities").Items())
            {
                var type = entity.Field("type");
                if (type.Field("@type").Text() == "textEntityTypeTextUrl") return type.Field("url").Text();
                if (type.Field("@type").Text() != "textEntityTypeUrl") continue;
                var offset = entity.Field("offset").Number();
                var length = entity.Field("length").Number();
                if (offset >= 0 && length > 0 && offset + length <= text.Length)
                {
                    var link = text.Substring((int)offset, (int)length);
                    return link.Contains("://", StringComparison.Ordinal) ? link : "https://" + link;
                }
            }
        }
        return row.PrimaryLink;
    }

    private async void OpenSharedMedia(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not SharedMediaItem item) return;
        if (Uri.TryCreate(item.Link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
            return;
        }
        var source = new Border { DataContext = item.Message };
        if (item.Message.MediaKind == "document") DownloadOrOpenDocument(source, new RoutedEventArgs());
        else OpenMessageMedia(source, new TappedRoutedEventArgs());
    }

    private async void SharedThumbnailLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image { DataContext: SharedMediaItem { Message: var row } } ||
            telegram is null || row.MediaPreview is not null || row.PreviewFileId == 0 ||
            row.MediaKind is not ("photo" or "video")) return;
        try
        {
            var file = await telegram.RequestAsync(new
            {
                @type = "downloadFile", file_id = row.PreviewFileId, priority = 2,
                offset = 0, limit = 0, synchronous = true
            }, lifetime.Token);
            row.ApplyFile(file);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or TimeoutException) { }
    }

    private async void OpenStickerPanel(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is null) return;
        profileLoadVersion++;
        sharedLoadVersion++;
        ShowSidePanel("Стикеры");
        ProfilePanel.Visibility = Visibility.Collapsed;
        StickersPanel.Visibility = Visibility.Visible;
        StickerGrid.ItemsSource = panelStickers;
        StickerSetList.ItemsSource = panelStickerSets;
        if (EmojiCategory.ItemsSource is null)
        {
            var recent = new UnigramEmojiGroup { Title = "Недавние", Items = preferences.RecentEmoji.ToArray() };
            EmojiCategory.ItemsSource = new[] { recent }.Concat(UnigramEmojiCatalog.Groups).ToArray();
            EmojiCategory.SelectedIndex = recent.Items.Length > 0 ? 0 : 1;
        }
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
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        args.Handled = true;
        await SearchStickersAsync();
    }

    private async void SearchStickers(object sender, RoutedEventArgs args) => await SearchStickersAsync();

    private async Task SearchStickersAsync()
    {
        if (telegram is null || string.IsNullOrWhiteSpace(StickerSearch.Text)) return;
        var version = ++stickerLoadVersion;
        panelStickers.Clear();
        StickerSetList.Visibility = EmojiCategory.Visibility = EmojiGrid.Visibility = Visibility.Collapsed;
        StickerGrid.Visibility = Visibility.Visible;
        SidePanelTitle.Text = "Поиск стикеров";
        try
        {
            var text = StickerSearch.Text.Trim();
            var isEmoji = UnigramEmojiCatalog.Groups.Any(group => group.Items.Contains(text));
            var result = await telegram.RequestAsync(new
            {
                @type = "searchStickers", sticker_type = new Dictionary<string, object?> { ["@type"] = "stickerTypeRegular" },
                emojis = isEmoji ? text : "", query = isEmoji ? "" : text,
                input_language_codes = new[] { "ru", "en" }, offset = 0, limit = 100
            }, lifetime.Token);
            foreach (var sticker in result.Field("stickers").Items())
            {
                if (version != stickerLoadVersion) return;
                if (await CreateStickerItemAsync(sticker) is { } item && version == stickerLoadVersion) panelStickers.Add(item);
            }
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or IOException) { MessengerStatus.Text = "Поиск стикеров не выполнен. " + ex.Message; }
    }

    private async void OpenStickerSet(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not StickerSetPanelItem set || telegram is null) return;
        await LoadStickerSetAsync(set);
    }

    private async Task LoadStickerSetAsync(StickerSetPanelItem set)
    {
        if (telegram is null) return;
        var version = ++stickerLoadVersion; panelStickers.Clear();
        try
        {
            var full = await telegram.RequestAsync(new { @type = "getStickerSet", set_id = set.Id }, lifetime.Token);
            foreach (var sticker in full.Field("stickers").Items())
            {
                if (version != stickerLoadVersion) return;
                if (await CreateStickerItemAsync(sticker) is { } item && version == stickerLoadVersion) panelStickers.Add(item);
            }
            if (version == stickerLoadVersion) SidePanelTitle.Text = set.Title;
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
            panelStickers.Clear();
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
                var sets = (await telegram.RequestAsync(new { @type = "getInstalledStickerSets", sticker_type = new Dictionary<string, object?> { ["@type"] = "stickerTypeRegular" } }, lifetime.Token)).Field("sets").Items();
                if (version != stickerLoadVersion) return;
                foreach (var set in sets)
                    panelStickerSets.Add(new StickerSetPanelItem(set.Field("id").Number(), set.Field("title").Text()));
                if (panelStickerSets.FirstOrDefault() is { } first) await LoadStickerSetAsync(first);
                return;
            }
            else
                stickers.AddRange((await telegram.RequestAsync(new { @type = "getRecentStickers", is_attached = false }, lifetime.Token)).Field("stickers").Items());

            foreach (var sticker in stickers.Take(144))
            {
                if (version != stickerLoadVersion) return;
                var item = await CreateStickerItemAsync(sticker);
                if (item is not null && version == stickerLoadVersion) panelStickers.Add(item);
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
        MessageInput.Text = MessageInput.Text.Remove(start, MessageInput.SelectionLength).Insert(start, emoji);
        MessageInput.SelectionStart = start + emoji.Length;
        MessageInput.Focus(FocusState.Programmatic);
        preferences.RecentEmoji.Remove(emoji);
        preferences.RecentEmoji.Insert(0, emoji);
        if (preferences.RecentEmoji.Count > 40) preferences.RecentEmoji.RemoveRange(40, preferences.RecentEmoji.Count - 40);
        SavePreferences();
        if (EmojiCategory.ItemsSource is UnigramEmojiGroup[] groups)
        {
            groups[0].Items = preferences.RecentEmoji.ToArray();
            if (EmojiCategory.SelectedIndex == 0) EmojiGrid.ItemsSource = groups[0].Items;
        }
    }

    private async Task<StickerPanelItem?> CreateStickerItemAsync(JsonElement sticker)
    {
        if (telegram is null) return null;
        var file = sticker.Field("sticker");
        var fileId = (int)file.Field("id").Number();
        if (fileId == 0) return null;
        var format = sticker.Field("format").Field("@type").Text();
        var previewFile = sticker.Field("thumbnail").Field("file");
        var previewId = (int)previewFile.Field("id").Number();
        var path = previewFile.Field("local").Field("path").Text();
        if (previewId == 0 && format == "stickerFormatWebp") { previewId = fileId; path = file.Field("local").Field("path").Text(); }
        if (previewId != 0)
        {
            try { path = await GetStickerFileAsync(previewId, path, 1); }
            catch (TdException) { path = ""; }
        }
        ImageSource? preview = null;
        if (path.Length > 0 && File.Exists(path))
        {
            try { preview = new BitmapImage(new Uri(path)); } catch (Exception ex) when (ex is ArgumentException or UriFormatException) { }
        }
        var mediaPath = file.Field("local").Field("path").Text();
        return new StickerPanelItem(fileId, sticker.Field("emoji").Text(), (int)sticker.Field("width").Number(), (int)sticker.Field("height").Number(), format, mediaPath, preview);
    }

    private async Task<string> GetStickerFileAsync(int fileId, string path, int priority)
    {
        if (File.Exists(path) || telegram is null) return path;
        var downloaded = await telegram.RequestAsync(new
        {
            @type = "downloadFile", file_id = fileId, priority, offset = 0, limit = 0, synchronous = true
        }, lifetime.Token);
        return downloaded.Field("local").Field("path").Text();
    }

    private async void SendSticker(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not StickerPanelItem sticker || telegram is null || activeChat is null) return;
        try
        {
            var inputFile = new Dictionary<string, object?> { ["@type"] = "inputFileId", ["id"] = sticker.FileId };
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

internal sealed record StickerPanelItem(int FileId, string Emoji, int Width, int Height, string Format, string MediaPath, ImageSource? Preview);
internal sealed record StickerSetPanelItem(long Id, string Title);
internal sealed record SharedMediaItem(MessageRow Message, string Title, string Link);
