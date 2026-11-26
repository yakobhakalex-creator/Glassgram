using System.Collections.ObjectModel;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<PinnedMessageItem> pinnedMessages = [];
    private readonly List<MessageRow> chatSearchResults = [];
    private CancellationTokenSource? chatSearchCancellation;
    private int pinnedLoadVersion;
    private int chatSearchVersion;
    private int chatSearchIndex = -1;
    private long chatSearchNextFromMessageId;
    private int? chatSearchTotalCount;
    private bool chatSearchControlsReady;
    private bool settingChatSearchFilters;
    private int pinnedIndex;

    private sealed record SearchOption(string Title, string Value);
    private sealed record PinnedMessageItem(MessageRow Message)
    {
        public string Preview => Message.Text.Length > 0 ? Message.Text : Message.MediaKind switch
        {
            "photo" => "Фотография", "video" => "Видео", "animation" => "Анимация",
            "document" => "Документ", "voice" => "Голосовое сообщение", "sticker" => "Стикер", _ => "Сообщение"
        };
        public string DateText => Message.Date.ToString("g");
    }

    private void InitializePinnedAndChatSearch()
    {
        if (chatSearchControlsReady) return;
        chatSearchControlsReady = true;
        PinnedMessagesList.ItemsSource = pinnedMessages;
        ChatSearchMedia.ItemsSource = new[]
        {
            new SearchOption("Все типы", ""), new SearchOption("Фото", "searchMessagesFilterPhoto"),
            new SearchOption("Видео", "searchMessagesFilterVideo"), new SearchOption("Файлы", "searchMessagesFilterDocument"),
            new SearchOption("Ссылки", "searchMessagesFilterUrl"), new SearchOption("Аудио", "searchMessagesFilterAudio"),
            new SearchOption("Голосовые", "searchMessagesFilterVoiceNote"), new SearchOption("Кружки", "searchMessagesFilterVideoNote"),
            new SearchOption("Анимации", "searchMessagesFilterAnimation")
        };
        ChatSearchMedia.DisplayMemberPath = nameof(SearchOption.Title);
        ChatSearchMedia.SelectedIndex = 0;
    }

    private async Task PreparePinnedMessagesAsync(long chatId, long openVersion)
    {
        InitializePinnedAndChatSearch();
        settingChatSearchFilters = true;
        var senderOptions = new List<SearchOption> { new("Все отправители", "") , new("Я", $"user:{savedChatId}") };
        if (knownChats.TryGetValue(chatId, out var chat) && chat.UserId != 0 && chat.UserId != savedChatId)
            senderOptions.Add(new SearchOption("Собеседник", $"user:{chat.UserId}"));
        ChatSearchSender.ItemsSource = senderOptions;
        ChatSearchSender.DisplayMemberPath = nameof(SearchOption.Title);
        ChatSearchSender.SelectedIndex = 0;
        settingChatSearchFilters = false;
        ResetChatSearch(false);
        await LoadPinnedMessagesAsync(chatId, openVersion);
    }

    private async Task LoadPinnedMessagesAsync(long chatId, long openVersion)
    {
        if (telegram is null) return;
        var version = ++pinnedLoadVersion;
        var found = new Dictionary<long, MessageRow>();
        try
        {
            try
            {
                var latest = await telegram.RequestAsync(new { @type = "getChatPinnedMessage", chat_id = chatId }, lifetime.Token);
                var row = MessageRow.FromJson(latest);
                if (row.Id != 0) found[row.Id] = row;
            }
            catch (TdException ex) when (ex.Code == 404) { }

            long from = 0;
            do
            {
                var page = await ChatSearchService.SearchAsync(telegram, chatId, "", from, null, new { @type = "searchMessagesFilterPinned" }, lifetime.Token);
                foreach (var message in page.Messages)
                {
                    var row = MessageRow.FromJson(message);
                    if (row.Id != 0) found[row.Id] = row;
                }
                from = page.NextFromMessageId;
            }
            while (from != 0 && found.Count < 500 && version == pinnedLoadVersion && openVersion == chatOpenVersion);

            if (version != pinnedLoadVersion || openVersion != chatOpenVersion || activeChat?.Id != chatId) return;
            var ordered = found.Values.OrderByDescending(message => message.Id).Select(message => new PinnedMessageItem(message)).ToArray();
            SynchronizePinnedCollection(ordered);
            pinnedIndex = Math.Clamp(pinnedIndex, 0, Math.Max(0, pinnedMessages.Count - 1));
            UpdatePinnedMessageBar();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (version == pinnedLoadVersion && activeChat?.Id == chatId && ex is not OperationCanceledException)
                MessengerStatus.Text = "Не удалось загрузить закреплённые сообщения. " + ex.Message;
        }
    }

    private void SynchronizePinnedCollection(IReadOnlyList<PinnedMessageItem> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var existing = pinnedMessages.FirstOrDefault(item => item.Message.Id == desired[index].Message.Id);
            if (existing is null) pinnedMessages.Insert(index, desired[index]);
            else
            {
                var oldIndex = pinnedMessages.IndexOf(existing);
                if (oldIndex != index) pinnedMessages.Move(oldIndex, index);
                pinnedMessages[index] = desired[index];
            }
        }
        while (pinnedMessages.Count > desired.Count) pinnedMessages.RemoveAt(pinnedMessages.Count - 1);
    }

    private void UpdatePinnedMessageBar()
    {
        var hidden = activeChat is not null && preferences.HiddenPinnedBars.Contains(activeChat.Id);
        PinnedMessageBar.Visibility = pinnedMessages.Count > 0 && !hidden ? Visibility.Visible : Visibility.Collapsed;
        if (pinnedMessages.Count == 0) return;
        pinnedIndex = Math.Clamp(pinnedIndex, 0, pinnedMessages.Count - 1);
        PinnedMessagePreview.Text = pinnedMessages[pinnedIndex].Preview;
        PinnedMessageCount.Text = $"{pinnedIndex + 1}/{pinnedMessages.Count}";
    }

    private async void OpenCurrentPinnedMessage(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs args)
    {
        if (activeChat is null || pinnedMessages.Count == 0) return;
        await NavigateToMessageAsync(activeChat.Id, pinnedMessages[pinnedIndex].Message.Id);
    }

    private void PreviousPinnedMessage(object sender, RoutedEventArgs args)
    {
        if (pinnedMessages.Count == 0) return;
        pinnedIndex = (pinnedIndex + 1) % pinnedMessages.Count;
        UpdatePinnedMessageBar();
    }

    private void NextPinnedMessage(object sender, RoutedEventArgs args)
    {
        if (pinnedMessages.Count == 0) return;
        pinnedIndex = (pinnedIndex - 1 + pinnedMessages.Count) % pinnedMessages.Count;
        UpdatePinnedMessageBar();
    }

    private void HidePinnedMessageBar(object sender, RoutedEventArgs args)
    {
        if (activeChat is null) return;
        preferences.HiddenPinnedBars.Add(activeChat.Id);
        SavePreferences();
        UpdatePinnedMessageBar();
    }

    private void OpenPinnedMessagesList(object sender, RoutedEventArgs args)
    {
        ProfilePanel.Visibility = StickersPanel.Visibility = Visibility.Collapsed;
        PinnedMessagesPanel.Visibility = Visibility.Visible;
        ShowSidePanel($"Закреплённые · {pinnedMessages.Count}");
    }

    private void ShowPinnedMessageBar(object sender, RoutedEventArgs args)
    {
        if (activeChat is null) return;
        preferences.HiddenPinnedBars.Remove(activeChat.Id);
        SavePreferences();
        UpdatePinnedMessageBar();
    }

    private async void PinnedMessageItemClick(object sender, ItemClickEventArgs args)
    {
        if (activeChat is null || args.ClickedItem is not PinnedMessageItem item) return;
        await NavigateToMessageAsync(activeChat.Id, item.Message.Id);
    }

    private void RefreshPinnedMessages(long chatId)
    {
        if (activeChat?.Id == chatId) _ = LoadPinnedMessagesAsync(chatId, chatOpenVersion);
    }

    private async void SearchMessages(object sender, TextChangedEventArgs args)
    {
        await QueueChatSearchAsync();
    }

    private async Task QueueChatSearchAsync()
    {
        if (activeChat is null) return;
        var version = ++chatSearchVersion;
        chatSearchCancellation?.Cancel();
        chatSearchCancellation?.Dispose();
        chatSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = chatSearchCancellation.Token;
        var query = MessageSearch.Text.Trim();
        if (query.Length == 0) { ResetChatSearch(true); return; }
        ChatSearchProgress.IsActive = true;
        ChatSearchStatus.Text = "Поиск…";
        try
        {
            await Task.Delay(350, token);
            await LoadChatSearchPageAsync(version, true, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadChatSearchPageAsync(int version, bool firstPage, CancellationToken token)
    {
        if (telegram is null || activeChat is null) return;
        var chatId = activeChat.Id;
        var query = MessageSearch.Text.Trim();
        var sender = SelectedChatSearchSender();
        var filter = SelectedChatSearchFilter();
        var from = firstPage ? 0 : chatSearchNextFromMessageId;
        try
        {
            var page = await ChatSearchService.SearchAsync(telegram, chatId, query, from, sender, filter, token);
            if (version != chatSearchVersion || activeChat?.Id != chatId || query != MessageSearch.Text.Trim()) return;
            if (firstPage) { chatSearchResults.Clear(); chatSearchIndex = -1; }
            var known = chatSearchResults.Select(message => message.Id).ToHashSet();
            if (!histories.TryGetValue(chatId, out var history)) return;
            foreach (var message in page.Messages)
            {
                var row = MessageRow.FromJson(message);
                history.Messages[row.Id] = row;
                if (known.Add(row.Id)) chatSearchResults.Add(row);
            }
            chatSearchNextFromMessageId = page.NextFromMessageId;
            chatSearchTotalCount = page.TotalCount;
            ChatSearchMore.Visibility = chatSearchNextFromMessageId != 0 ? Visibility.Visible : Visibility.Collapsed;
            if (chatSearchResults.Count == 0) ChatSearchStatus.Text = "Ничего не найдено";
            else
            {
                if (firstPage) chatSearchIndex = 0;
                UpdateChatSearchStatus();
                await NavigateCurrentChatSearchResultAsync();
            }
        }
        catch (Exception ex) when (ex is TdException or JsonException)
        {
            if (version == chatSearchVersion) ChatSearchStatus.Text = "Ошибка поиска: " + ex.Message;
        }
        finally { if (version == chatSearchVersion) ChatSearchProgress.IsActive = false; }
    }

    private object? SelectedChatSearchSender()
    {
        var value = (ChatSearchSender.SelectedItem as SearchOption)?.Value ?? "";
        if (!value.StartsWith("user:", StringComparison.Ordinal) || !long.TryParse(value[5..], out var id)) return null;
        return new Dictionary<string, object?> { ["@type"] = "messageSenderUser", ["user_id"] = id };
    }

    private object? SelectedChatSearchFilter()
    {
        var value = (ChatSearchMedia.SelectedItem as SearchOption)?.Value ?? "";
        return value.Length == 0 ? null : new Dictionary<string, string> { ["@type"] = value };
    }

    private void ChatSearchFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (settingChatSearchFilters || MessageSearch.Text.Trim().Length == 0) return;
        _ = QueueChatSearchAsync();
    }

    private void UpdateChatSearchStatus()
    {
        var total = chatSearchTotalCount is int count ? count.ToString() : chatSearchResults.Count.ToString();
        ChatSearchStatus.Text = chatSearchIndex >= 0 ? $"{chatSearchIndex + 1} из {total}" : $"Найдено: {total}";
    }

    private async Task NavigateCurrentChatSearchResultAsync()
    {
        if (activeChat is null || chatSearchIndex < 0 || chatSearchIndex >= chatSearchResults.Count) return;
        await NavigateToMessageAsync(activeChat.Id, chatSearchResults[chatSearchIndex].Id);
    }

    private async void PreviousChatSearchResult(object sender, RoutedEventArgs args)
    {
        if (chatSearchResults.Count == 0) return;
        chatSearchIndex = Math.Min(chatSearchResults.Count - 1, chatSearchIndex + 1);
        if (chatSearchIndex == chatSearchResults.Count - 1 && chatSearchNextFromMessageId != 0)
            await LoadChatSearchPageAsync(chatSearchVersion, false, chatSearchCancellation?.Token ?? lifetime.Token);
        UpdateChatSearchStatus();
        await NavigateCurrentChatSearchResultAsync();
    }

    private async void NextChatSearchResult(object sender, RoutedEventArgs args)
    {
        if (chatSearchResults.Count == 0) return;
        chatSearchIndex = Math.Max(0, chatSearchIndex - 1);
        UpdateChatSearchStatus();
        await NavigateCurrentChatSearchResultAsync();
    }

    private async void LoadMoreChatSearchResults(object sender, RoutedEventArgs args)
    {
        if (chatSearchNextFromMessageId == 0) return;
        ChatSearchProgress.IsActive = true;
        await LoadChatSearchPageAsync(chatSearchVersion, false, chatSearchCancellation?.Token ?? lifetime.Token);
    }

    private void ResetChatSearch(bool restoreHistory)
    {
        chatSearchCancellation?.Cancel();
        chatSearchResults.Clear();
        chatSearchIndex = -1;
        chatSearchNextFromMessageId = 0;
        chatSearchTotalCount = null;
        ChatSearchStatus.Text = "";
        ChatSearchProgress.IsActive = false;
        ChatSearchMore.Visibility = Visibility.Collapsed;
        if (restoreHistory && activeChat is not null) RenderHistory(activeChat.Id);
    }

    private void MessageTextLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBlock block) return;
        block.TextHighlighters.Clear();
        var query = MessageSearch.Visibility == Visibility.Visible ? MessageSearch.Text.Trim() : "";
        if (query.Length == 0 || block.Text.Length == 0) return;
        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 198, 62)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0))
        };
        var start = 0;
        while ((start = block.Text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = query.Length });
            start += query.Length;
        }
        if (highlighter.Ranges.Count > 0) block.TextHighlighters.Add(highlighter);
    }
}
