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
    private bool searchActive;

    private void InitializeSearch()
    {
        searchDebounce.Tick += async (_, _) => await PerformSearchAsync(searchState.Query, searchState.Generation);
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
            return;
        }
        if (queryChanged)
        {
            searchState.Chats.Clear();
            searchState.Contacts.Clear();
            searchState.Messages.Clear();
            searchState.PublicChats.Clear();
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
        searchState.HasMoreMessages = false;
        searchState.IsLoadingMoreMessages = false;
        searchState.NextMessageOffset = "";
        searchActive = false;
        searchState.IsSearching = false;
        SearchUI.Visibility = Visibility.Collapsed;
        SearchResultsList.ItemsSource = null;
        ChatList.Visibility = Visibility.Visible;
        ChatList.ItemsSource = visibleChats;
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
        var allEmpty = searchState.Chats.Count == 0 && searchState.Contacts.Count == 0 &&
                       searchState.Messages.Count == 0 && searchState.PublicChats.Count == 0;

        EmptySearchResults.Visibility = allEmpty && !searchState.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        SearchProgressRing.IsActive = searchState.IsSearching;
        SearchLoadingText.Visibility = searchState.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        SearchErrorText.Text = searchState.Error ?? "";
        SearchErrorText.Visibility = searchState.Error != null ? Visibility.Visible : Visibility.Collapsed;

        if (searchActive) RenderSearchResults();
    }

    private void RenderSearchResults()
    {
        var results = new List<object>();
        results.AddRange(searchState.Chats);
        results.AddRange(searchState.Contacts);
        results.AddRange(searchState.PublicChats);
        results.AddRange(searchState.Messages);
        if (searchState.Chats.Count > 0) searchState.Chats[0].SectionTitle = "Чаты и контакты";
        else if (searchState.Contacts.Count > 0) searchState.Contacts[0].SectionTitle = "Чаты и контакты";
        if (searchState.PublicChats.Count > 0) searchState.PublicChats[0].SectionTitle = "Результаты общего поиска";
        if (searchState.Messages.Count > 0) searchState.Messages[0].SectionTitle = "Сообщения";
        SearchResultsList.ItemsSource = results;
        LoadMoreSearchMessages.Visibility = searchState.HasMoreMessages && !searchState.IsLoadingMoreMessages ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is SearchChatResult chat)
        {
            if (args.InRecycleQueue) args.ItemContainer.ContentTemplate = null;
            else if (chat.Avatar is not null && args.ItemContainer.ContentTemplate is DataTemplate template)
            {
                // Template already binds Avatar
            }
        }
        else if (args.Item is SearchContactResult contact)
        {
            if (args.InRecycleQueue) args.ItemContainer.ContentTemplate = null;
        }
        else if (args.Item is SearchPublicChatResult publicChat)
        {
            if (args.InRecycleQueue) args.ItemContainer.ContentTemplate = null;
        }
        else if (args.Item is SearchMessageResult message)
        {
            if (args.InRecycleQueue) args.ItemContainer.ContentTemplate = null;
        }
    }

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

            // Server search from 3 characters
            if (query.Length >= 3)
            {
                var (serverChats, publicChats) = await GlobalSearchService.SearchServerChatsAndPublicAsync(telegram, query, token);
                if (generation != searchState.Generation) return;
                foreach (var c in serverChats) if (searchState.Chats.All(x => x.ChatId != c.ChatId)) searchState.Chats.Add(c);
                foreach (var c in publicChats) if (searchState.PublicChats.All(x => x.ChatId != c.ChatId)) searchState.PublicChats.Add(c);
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
            try { await telegram!.RequestAsync(new { @type = "downloadFile", file_id = fileId, priority = 1, offset = 0, limit = 0, synchronous = false }, token); }
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
                ClearSearchResults();
                ChatSearch.Text = "";
                break;
            case SearchMessageResult message:
                await OpenChatAsync(message.ChatId);
                await NavigateToMessageAsync(message.ChatId, message.MessageId);
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
