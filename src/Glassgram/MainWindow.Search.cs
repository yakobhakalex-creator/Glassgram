using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glassgram;

public partial class MainWindow
{
    private readonly GlobalSearchState searchState = new();
    private readonly DispatcherTimer searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(GlobalSearchService.DebounceMs) };
    private readonly List<RecentSearchResult> recentSearches = [];
    private bool searchActive;

    private void InitializeSearch()
    {
        SearchResultsList.ItemsSource = searchState.Results;
        searchDebounce.Tick += async (_, _) => await PerformSearchAsync(searchState.Query, searchState.Generation);
    }

    private void SearchFocus(object sender, RoutedEventArgs args)
    {
        if (ChatSearch.Text.Trim().Length == 0) ShowRecentSearches();
    }

    private void SearchChats(object sender, TextChangedEventArgs args)
    {
        if (sender is not TextBox box) return;
        var query = box.Text.Trim();
        var queryChanged = !string.Equals(searchState.Query, query, StringComparison.Ordinal);
        searchState.Query = query;
        searchState.Generation++;
        searchState.Cts?.Cancel();
        searchState.Cts = new CancellationTokenSource();
        searchDebounce.Stop();
        if (query.Length == 0)
        {
            ClearSearchResults();
            RefreshChatList();
            if (box.FocusState != FocusState.Unfocused) ShowRecentSearches();
            return;
        }
        if (queryChanged)
        {
            searchState.Chats.Clear();
            searchState.Contacts.Clear();
            searchState.Messages.Clear();
            searchState.PublicChats.Clear();
            searchState.Results.Clear();
            searchState.NextMessageOffset = "";
            searchState.HasMoreMessages = false;
        }
        searchActive = true;
        searchState.IsSearching = true;
        searchState.HasSearched = true;
        searchState.Error = null;
        SearchUI.Visibility = Visibility.Visible;
        ChatList.Visibility = Visibility.Collapsed;
        searchDebounce.Start();
        UpdateSearchUI();
    }

    private void ClearSearchResults()
    {
        searchState.Chats.Clear();
        searchState.Contacts.Clear();
        searchState.Messages.Clear();
        searchState.PublicChats.Clear();
        searchState.Results.Clear();
        searchState.HasMoreMessages = false;
        searchState.IsLoadingMoreMessages = false;
        searchState.NextMessageOffset = "";
        searchActive = false;
        searchState.IsSearching = false;
        SearchUI.Visibility = Visibility.Collapsed;
        ChatList.Visibility = Visibility.Visible;
        ChatList.ItemsSource = visibleChats;
        RecentSearchHeader.Visibility = Visibility.Collapsed;
    }

    private void ShowRecentSearches()
    {
        if (recentSearches.Count == 0 || ChatSearch.Text.Trim().Length != 0) return;
        searchActive = true;
        SearchUI.Visibility = Visibility.Visible;
        ChatList.Visibility = Visibility.Collapsed;
        SearchProgressRing.IsActive = false;
        SearchLoadingText.Visibility = Visibility.Collapsed;
        EmptySearchResults.Visibility = Visibility.Collapsed;
        SearchErrorText.Visibility = Visibility.Collapsed;
        RecentSearchHeader.Visibility = Visibility.Visible;
        searchState.Results.Clear();
        foreach (var recent in recentSearches) searchState.Results.Add(recent);
        LoadMoreSearchMessages.Visibility = Visibility.Collapsed;
    }

    private async Task LoadRecentSearchesAsync(long accountId, CancellationToken token)
    {
        var entries = await RecentSearchStore.LoadAsync(accountId, token);
        recentSearches.Clear();
        foreach (var entry in entries.Take(20))
        {
            var recent = new RecentSearchResult
            {
                ChatId = entry.ChatId, Title = entry.Title, Username = entry.Username, Type = entry.Type,
                PhotoFileId = entry.PhotoFileId, AvatarPath = entry.AvatarPath
            };
            recent.LoadAvatar();
            recentSearches.Add(recent);
        }
    }

    private async Task AddRecentSearchAsync(long chatId, string title, string username, string type, int photoFileId, string avatarPath)
    {
        if (savedChatId == 0 || chatId == 0 || title.Length == 0) return;
        recentSearches.RemoveAll(item => item.ChatId == chatId);
        var recent = new RecentSearchResult
        {
            ChatId = chatId, Title = title, Username = username, Type = type,
            PhotoFileId = photoFileId, AvatarPath = avatarPath
        };
        recent.LoadAvatar();
        recentSearches.Insert(0, recent);
        if (recentSearches.Count > 20) recentSearches.RemoveRange(20, recentSearches.Count - 20);
        await RecentSearchStore.SaveAsync(savedChatId, recentSearches.Select(item => item.ToEntry()), lifetime.Token);
    }

    private async void RemoveRecentSearch(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentSearchResult recent) return;
        recentSearches.RemoveAll(item => item.ChatId == recent.ChatId);
        searchState.Results.Remove(recent);
        await RecentSearchStore.SaveAsync(savedChatId, recentSearches.Select(item => item.ToEntry()), lifetime.Token);
        if (recentSearches.Count == 0) ClearSearchResults();
    }

    private async void ClearRecentSearches(object sender, RoutedEventArgs args)
    {
        recentSearches.Clear();
        searchState.Results.Clear();
        await RecentSearchStore.SaveAsync(savedChatId, [], lifetime.Token);
        ClearSearchResults();
    }

    private void UpdateSearchUI()
    {
        if (searchState.Query.Length == 0)
        {
            ChatList.ItemsSource = visibleChats;
            SearchUI.Visibility = Visibility.Collapsed;
            EmptySearchResults.Visibility = Visibility.Collapsed;
            SearchProgressRing.IsActive = false;
            SearchLoadingText.Visibility = Visibility.Collapsed;
            return;
        }

        SearchUI.Visibility = Visibility.Visible;
        RecentSearchHeader.Visibility = Visibility.Collapsed;
        var allEmpty = searchState.Chats.Count == 0 && searchState.Contacts.Count == 0 &&
                       searchState.Messages.Count == 0 && searchState.PublicChats.Count == 0;

        EmptySearchResults.Visibility = allEmpty && !searchState.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        SearchProgressRing.IsActive = searchState.IsSearching;
        SearchLoadingText.Visibility = searchState.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        SearchErrorText.Text = searchState.Error ?? "";
        SearchErrorText.Visibility = searchState.Error != null ? Visibility.Visible : Visibility.Collapsed;

        if (searchActive) SyncSearchResults();
    }

    private void SyncSearchResults()
    {
        if (searchState.Chats.Count > 0) searchState.Chats[0].SectionTitle = "Чаты и контакты";
        else if (searchState.Contacts.Count > 0) searchState.Contacts[0].SectionTitle = "Чаты и контакты";
        if (searchState.PublicChats.Count > 0) searchState.PublicChats[0].SectionTitle = "Результаты общего поиска";
        if (searchState.Messages.Count > 0) searchState.Messages[0].SectionTitle = "Сообщения";
        var desired = searchState.Chats.Cast<object>()
            .Concat(searchState.Contacts)
            .Concat(searchState.PublicChats)
            .Concat(searchState.Messages)
            .ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var key = SearchResultKey(desired[index]);
            if (index < searchState.Results.Count && SearchResultKey(searchState.Results[index]) == key) continue;
            var existing = -1;
            for (var candidate = index + 1; candidate < searchState.Results.Count; candidate++)
                if (SearchResultKey(searchState.Results[candidate]) == key) { existing = candidate; break; }
            if (existing >= 0) searchState.Results.Move(existing, index);
            else searchState.Results.Insert(index, desired[index]);
        }
        while (searchState.Results.Count > desired.Length) searchState.Results.RemoveAt(searchState.Results.Count - 1);
        LoadMoreSearchMessages.Visibility = searchState.HasMoreMessages && !searchState.IsLoadingMoreMessages ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SearchResultKey(object item) => item switch
    {
        SearchChatResult chat => $"chat:{chat.ChatId}",
        SearchContactResult contact => $"contact:{contact.UserId}",
        SearchPublicChatResult chat => $"public:{chat.ChatId}",
        SearchMessageResult message => $"message:{message.ChatId}:{message.MessageId}",
        RecentSearchResult recent => $"recent:{recent.ChatId}",
        _ => item.GetHashCode().ToString()
    };

    private async Task PerformSearchAsync(string query, int generation)
    {
        if (telegram is null) return;
        var token = searchState.Cts?.Token ?? CancellationToken.None;
        try
        {
            searchState.IsSearching = true;
            DispatcherQueue.TryEnqueue(UpdateSearchUI);

            // Local search available from first character
            var (localChats, localContacts) = await GlobalSearchService.SearchLocalChatsAndContactsAsync(telegram, query, token);
            if (generation != searchState.Generation) return;
            foreach (var c in localChats) if (searchState.Chats.All(x => x.ChatId != c.ChatId)) searchState.Chats.Add(c);
            foreach (var c in localContacts) if (searchState.Contacts.All(x => x.UserId != c.UserId)) searchState.Contacts.Add(c);
            UpdateSearchUI();

            // Server search from 3 characters
            if (query.Length >= 3)
            {
                var (serverChats, publicChats) = await GlobalSearchService.SearchServerChatsAndPublicAsync(telegram, query, token);
                if (generation != searchState.Generation) return;
                foreach (var c in serverChats) if (searchState.Chats.All(x => x.ChatId != c.ChatId)) searchState.Chats.Add(c);
                foreach (var c in publicChats) if (searchState.PublicChats.All(x => x.ChatId != c.ChatId)) searchState.PublicChats.Add(c);
                UpdateSearchUI();
            }

            // Global message search from 3 characters
            if (query.Length >= 3)
            {
                var page = await GlobalSearchService.SearchMessagesGlobalAsync(telegram, query, "", GlobalSearchService.MessageLimit, token);
                var messages = page.Messages;
                if (generation != searchState.Generation) return;
                foreach (var m in messages)
                    if (searchState.Messages.All(x => x.ChatId != m.ChatId || x.MessageId != m.MessageId))
                        searchState.Messages.Add(m);

                // Enrich message chat titles
                await EnrichMessageChatTitlesAsync(token);
                if (generation != searchState.Generation) return;

                searchState.NextMessageOffset = page.NextOffset;
                searchState.HasMoreMessages = page.NextOffset.Length > 0;
                UpdateSearchUI();
            }

            // Download avatars for new results
            await DownloadSearchAvatarsAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (TdException ex) when (ex.Code == 429) // FLOOD_WAIT
        {
            if (generation == searchState.Generation)
            {
                searchState.Error = "Слишком много запросов. Подождите немного.";
                searchState.IsSearching = false;
            }
        }
        catch (Exception ex) when (ex is TdException or JsonException or InvalidOperationException)
        {
            if (generation == searchState.Generation)
            {
                searchState.Error = "Ошибка поиска: " + ex.Message;
                searchState.IsSearching = false;
            }
        }
        finally
        {
            if (generation == searchState.Generation)
            {
                searchState.IsSearching = false;
                DispatcherQueue.TryEnqueue(UpdateSearchUI);
            }
        }
    }

    private async Task EnrichMessageChatTitlesAsync(CancellationToken token)
    {
        var chatIds = searchState.Messages.Where(m => m.ChatTitle.Length == 0).Select(m => m.ChatId).Distinct().ToArray();
        foreach (var chatId in chatIds)
        {
            if (token.IsCancellationRequested) break;
            var chat = await GlobalSearchService.GetChatAsync(telegram!, chatId, token);
            if (chat is JsonElement element)
            {
                var title = element.Field("title").Text();
                foreach (var msg in searchState.Messages.Where(m => m.ChatId == chatId))
                    msg.ChatTitle = title;
            }
        }
    }

    private async Task DownloadSearchAvatarsAsync(CancellationToken token)
    {
        var fileIds = searchState.Chats.Where(c => c.PhotoFileId != 0 && c.Avatar is null).Select(c => c.PhotoFileId).Distinct()
            .Concat(searchState.Contacts.Where(c => c.PhotoFileId != 0 && c.Avatar is null).Select(c => c.PhotoFileId)).Distinct()
            .Concat(searchState.PublicChats.Where(c => c.PhotoFileId != 0 && c.Avatar is null).Select(c => c.PhotoFileId)).Distinct();
        foreach (var fileId in fileIds)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var file = await telegram!.RequestAsync(new { @type = "downloadFile", file_id = fileId, priority = 1, offset = 0, limit = 0, synchronous = true }, token);
                var path = file.Field("local").Field("path").Text();
                if (path.Length == 0) continue;
                foreach (var chat in searchState.Chats.Where(item => item.PhotoFileId == fileId)) chat.SetAvatar(path);
                foreach (var contact in searchState.Contacts.Where(item => item.PhotoFileId == fileId)) contact.SetAvatar(path);
                foreach (var chat in searchState.PublicChats.Where(item => item.PhotoFileId == fileId)) chat.SetAvatar(path);
            }
            catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
        }
    }

    private async void OnLoadMoreSearchMessagesClick(object sender, RoutedEventArgs args)
    {
        if (searchState.IsLoadingMoreMessages || !searchState.HasMoreMessages || telegram is null || string.IsNullOrWhiteSpace(searchState.Query)) return;
        searchState.IsLoadingMoreMessages = true;
        UpdateSearchUI();
        var generation = searchState.Generation;
        var token = searchState.Cts?.Token ?? CancellationToken.None;
        try
        {
            var page = await GlobalSearchService.SearchMessagesGlobalAsync(telegram, searchState.Query, searchState.NextMessageOffset, GlobalSearchService.MessageLimit, token);
            var messages = page.Messages;
            if (generation != searchState.Generation) return;
            foreach (var m in messages)
                if (searchState.Messages.All(x => x.ChatId != m.ChatId || x.MessageId != m.MessageId))
                    searchState.Messages.Add(m);
            await EnrichMessageChatTitlesAsync(token);
            if (generation != searchState.Generation) return;
            searchState.NextMessageOffset = page.NextOffset;
            searchState.HasMoreMessages = page.NextOffset.Length > 0;
            UpdateSearchUI();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is TdException or JsonException)
        {
            if (generation == searchState.Generation) searchState.Error = "Не удалось загрузить больше сообщений: " + ex.Message;
        }
        finally
        {
            if (generation == searchState.Generation)
            {
                searchState.IsLoadingMoreMessages = false;
                DispatcherQueue.TryEnqueue(UpdateSearchUI);
            }
        }
    }

    private async void SelectSearchResult(object sender, SelectionChangedEventArgs args)
    {
        if (SearchResultsList.SelectedItem is not { } item) return;
        SearchResultsList.SelectedItem = null; // Allow re-selection

        switch (item)
        {
            case SearchChatResult chat:
                await OpenChatAsync(chat.ChatId);
                await AddRecentSearchAsync(chat.ChatId, chat.Title, chat.Username, chat.Type, chat.PhotoFileId, chat.AvatarPath);
                ClearSearchResults();
                ChatSearch.Text = "";
                break;
            case SearchContactResult contact:
                if (contact.UserId != 0)
                {
                    try
                    {
                        var chat = await telegram!.RequestAsync(new { @type = "createPrivateChat", user_id = contact.UserId, force = false }, lifetime.Token);
                        await OpenChatAsync(chat.Field("id").Number());
                        await AddRecentSearchAsync(chat.Field("id").Number(), contact.DisplayName, contact.Username, "Контакт", contact.PhotoFileId, contact.AvatarPath);
                        ClearSearchResults();
                        ChatSearch.Text = "";
                    }
                    catch (Exception ex) when (ex is TdException or OperationCanceledException)
                    {
                        MessengerStatus.Text = "Не удалось открыть чат с контактом: " + ex.Message;
                    }
                }
                break;
            case SearchPublicChatResult publicChat:
                await OpenChatAsync(publicChat.ChatId);
                await AddRecentSearchAsync(publicChat.ChatId, publicChat.Title, publicChat.Username, publicChat.Type, publicChat.PhotoFileId, publicChat.AvatarPath);
                ClearSearchResults();
                ChatSearch.Text = "";
                break;
            case SearchMessageResult message:
                await OpenChatAsync(message.ChatId);
                if (knownChats.TryGetValue(message.ChatId, out var messageChat))
                    await AddRecentSearchAsync(message.ChatId, messageChat.Title, "", "Чат", messageChat.PhotoFileId, "");
                await NavigateToMessageAsync(message.ChatId, message.MessageId);
                ClearSearchResults();
                ChatSearch.Text = "";
                break;
            case RecentSearchResult recent:
                await OpenChatAsync(recent.ChatId);
                await AddRecentSearchAsync(recent.ChatId, recent.Title, recent.Username, recent.Type, recent.PhotoFileId, recent.AvatarPath);
                ClearSearchResults();
                ChatSearch.Text = "";
                break;
        }
    }

    private async Task NavigateToMessageAsync(long chatId, long messageId)
    {
        if (!histories.TryGetValue(chatId, out var history)) return;
        if (!history.Messages.TryGetValue(messageId, out var row))
        {
            // Load history around message
            try
            {
                var result = await telegram!.RequestAsync(new { @type = "getMessage", chat_id = chatId, message_id = messageId }, lifetime.Token);
                row = MessageRow.FromJson(result);
                if (!history.Messages.ContainsKey(messageId)) history.Messages[messageId] = row;
            }
            catch (Exception ex) when (ex is TdException or OperationCanceledException)
            {
                MessengerStatus.Text = "Не удалось загрузить сообщение: " + ex.Message;
                return;
            }
        }
        RenderHistory(chatId);
        // Scroll to message
        if (row is not null)
        {
            MessageList.UpdateLayout();
            MessageList.ScrollIntoView(row);
            await Task.Delay(60, lifetime.Token);
            MessageList.UpdateLayout();
            MessageList.ScrollIntoView(row);
            // Highlight briefly
            row.BubbleBorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 200, 50));
            row.NotifyPropertyChanged(nameof(MessageRow.BubbleBorderBrush));
            await Task.Delay(2000, lifetime.Token);
            row.UpdateAppearance(lightTheme, activeChat?.LastReadOutboxId ?? 0);
        }
    }

    internal void HandleSearchEscape(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && searchActive)
        {
            ClearSearchResults();
            ChatSearch.Text = "";
            ChatSearch.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
    }
}
