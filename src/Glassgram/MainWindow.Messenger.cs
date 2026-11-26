using System.Collections.ObjectModel;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Glassgram;

public partial class MainWindow
{
    private const int InitialHistoryLimit = 500;
    private static readonly object ChatOrderLogLock = new();
    private static readonly string ChatOrderLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "chat-order-diagnostics.log");
    private readonly Dictionary<long, ChatRow> knownChats = [];
    private readonly List<JsonElement> pendingChatUpdates = [];
    private readonly ObservableCollection<ChatRow> visibleChats = [];
    private readonly ObservableCollection<MessageRow> visibleMessages = [];
    private readonly Dictionary<long, ChatHistory> histories = [];
    private readonly Dictionary<long, double> scrollPositions = [];
    private readonly Dictionary<long, int> historyDepths = [];
    private readonly Dictionary<int, long> avatarFiles = [];
    private readonly Dictionary<int, HashSet<MessageRow>> mediaFiles = [];
    private readonly Dictionary<long, string> userStatuses = [];
    private readonly Dictionary<long, long> lastMarkedRead = [];
    private ScrollViewer? messageScroller;
    private ChatRow? activeChat;
    private MessageRow? selectedMessage;
    private long? replyMessageId;
    private long? editMessageId;
    private long? pendingNotificationChat;
    private long savedChatId;
    private bool messengerStarted;
    private bool messageSending;
    private long chatOpenVersion;
    private bool resizingChats;
    private int historyRestores;
    private double resizeStartX;
    private double resizeStartWidth;
    private DateTime lastTypingSent;
    private DateTime lastTypingReceived;
    private bool showingArchive;
    private bool suppressDraftSave;
    private bool chatListsLoading;

    private sealed class ChatHistory
    {
        public Dictionary<long, MessageRow> Messages { get; } = [];
        public long OldestId { get; set; }
        public long PageCursorId { get; set; }
        public bool HasMore { get; set; } = true;
        public bool Loading { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    private void StartMessenger()
    {
        if (messengerStarted || telegram is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChatOrderLogPath)!);
            lock (ChatOrderLogLock) File.WriteAllText(ChatOrderLogPath, $"{DateTime.UtcNow:O} start messenger{Environment.NewLine}");
        }
        catch (IOException) { }
        messengerStarted = true;
        chatListsLoading = true;
        ChatList.ItemsSource = visibleChats;
        MessageList.ItemsSource = visibleMessages;
        InitializeChatFolders();
        LogChatOrder($"replay queued_updates={pendingChatUpdates.Count}");
        foreach (var update in pendingChatUpdates) HandleMessengerUpdate(update);
        pendingChatUpdates.Clear();
        _ = LoadChatListsAsync(telegram);
        if (pendingNotificationChat is long chatId)
            _ = OpenChatAsync(chatId);
    }

    private void StopMessenger()
    {
        if (!messengerStarted) return;
        messengerStarted = false;
        activeChat = null;
        selectedMessage = null;
        knownChats.Clear();
        pendingChatUpdates.Clear();
        visibleChats.Clear();
        visibleMessages.Clear();
        pinnedMessages.Clear();
        PinnedMessageBar.Visibility = Visibility.Collapsed;
        histories.Clear();
        scrollPositions.Clear();
        historyDepths.Clear();
        avatarFiles.Clear();
        mediaFiles.Clear();
        lastMarkedRead.Clear();
        MessageActions.Visibility = Visibility.Collapsed;
        ConversationPage.Visibility = Visibility.Collapsed;
        EmptyChat.Visibility = Visibility.Visible;
    }

    private async Task LoadChatListsAsync(TdClient client)
    {
        try
        {
            var me = await client.RequestAsync(new { @type = "getMe" }, lifetime.Token);
            savedChatId = me.Field("id").Number();
            await LoadRecentSearchesAsync(savedChatId, lifetime.Token);
            try
            {
                var saved = await client.RequestAsync(new { @type = "createPrivateChat", user_id = savedChatId, force = false }, lifetime.Token);
                UpsertChat(saved, saved: true);
            }
            catch (TdException) { }
            await Task.WhenAll(LoadChatListAsync(client, "chatListMain"), LoadChatListAsync(client, "chatListArchive"));
            chatListsLoading = false;
            RefreshChatList();
            LogChatOrder($"initial-load-complete known={knownChats.Count} visible={visibleChats.Count}");
            if (pendingNotificationChat is long chatId) await OpenChatAsync(chatId);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            chatListsLoading = false;
            if (messengerStarted && !lifetime.IsCancellationRequested) MessengerStatus.Text = "Не удалось загрузить чаты. " + ex.Message;
        }
    }

    private async Task LoadChatListAsync(TdClient client, string listType)
    {
        var list = new Dictionary<string, string> { ["@type"] = listType };
        while (messengerStarted && ReferenceEquals(telegram, client))
        {
            var loadedAll = false;
            try { await client.RequestAsync(new { @type = "loadChats", chat_list = list, limit = 100 }, lifetime.Token); }
            catch (TdException ex) when (ex.Code == 404) { loadedAll = true; }
            var chats = await client.RequestAsync(new { @type = "getChats", chat_list = list, limit = 100000 }, lifetime.Token);
            var chatIds = chats.Field("chat_ids").Items().ToArray();
            LogChatOrder($"getChats list={listType} count={chatIds.Length} loadedAll={loadedAll}");
            foreach (var id in chatIds)
            {
                var chatId = id.Number();
                var expectedRevision = knownChats.TryGetValue(chatId, out var existing) ? existing.PositionRevision : -1;
                var chat = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, lifetime.Token);
                UpsertChat(chat, saved: existing?.IsSaved ?? false, sourceList: listType, expectedPositionRevision: expectedRevision);
            }
            if (loadedAll) break;
        }
    }

    private void UpsertChat(JsonElement chat, bool saved = false, string? sourceList = null, long? expectedPositionRevision = null)
    {
        var id = chat.Field("id").Number();
        if (id == 0) return;
        LogPositions(id, sourceList is null ? "upsert" : $"getChat({sourceList})", chat.Field("positions"));
        var existed = knownChats.TryGetValue(id, out var row);
        row ??= new ChatRow(id);
        if (!existed) knownChats[id] = row;
        row.IsSaved |= saved || id == savedChatId;
        var updatePositions = expectedPositionRevision is null || !existed && expectedPositionRevision == -1 || row.PositionRevision == expectedPositionRevision;
        row.Update(chat, updatePositions);
        if (row.PhotoFileId != 0 && row.Avatar is null)
        {
            avatarFiles[row.PhotoFileId] = id;
            _ = DownloadAvatarAsync(row.PhotoFileId);
        }
        if (!chatListsLoading) RefreshChatList();
        if (activeChat?.Id == id)
        {
            activeChat = row;
            ActiveChatTitle.Text = row.Title;
            UpdateActiveStatus();
            UpdateComposerAvailability();
            RefreshReadMarks();
        }
    }

    private async Task DownloadAvatarAsync(int fileId)
    {
        try { await telegram!.RequestAsync(new { @type = "downloadFile", file_id = fileId, priority = 1, offset = 0, limit = 0, synchronous = false }, lifetime.Token); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void RefreshChatList()
    {
        if (searchActive) return; // Don't refresh normal list during search
        var selected = activeChat?.Id;
        var query = ChatSearch.Text.Trim();
        var listKey = showingArchive ? "chatListArchive" : ChatFolderList.Key(activeFolderId);
        var rows = knownChats.Values
            .Where(c => c.GetOrder(listKey) != 0)
            .Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(c => c.GetOrder(listKey))
            .ThenByDescending(c => c.Id)
            .ToArray();
        if (rows.Length == visibleChats.Count && rows.SequenceEqual(visibleChats)) return;
        visibleChats.Clear();
        foreach (var row in rows) visibleChats.Add(row);
        LogChatOrder($"visible list={listKey} count={rows.Length}");
        if (selected is long id && knownChats.TryGetValue(id, out var current) && visibleChats.Contains(current))
            ChatList.SelectedItem = current;
    }

    private static void LogChatOrder(string message)
    {
        try
        {
            lock (ChatOrderLogLock) File.AppendAllText(ChatOrderLogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private static void LogPositions(long chatId, string source, JsonElement positions)
    {
        foreach (var position in positions.Items())
        {
            var list = ChatRow.ListKey(position.Field("list"));
            var order = position.Field("order");
            LogChatOrder($"{source} chat_id={chatId} list={list} order={order.Number()} order_kind={order.ValueKind} order_raw={order} pinned={position.Field("is_pinned").Flag()}");
        }
    }

    
    private async void SelectChat(object sender, SelectionChangedEventArgs args)
    {
        if (ChatList.SelectedItem is ChatRow chat && chat.Id != activeChat?.Id)
            await OpenChatAsync(chat.Id);
    }

    internal async Task OpenChatFromNotificationAsync(long chatId)
    {
        pendingNotificationChat = chatId;
        Activate();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        if (telegramReady) await OpenChatAsync(chatId);
    }

    private async Task OpenChatAsync(long chatId)
    {
        if (!messengerStarted || telegram is null) return;
        if (voiceRecording && activeChat?.Id != chatId) await FinishVoiceRecordingAsync(false);
        activeVoice?.Stop();
        var version = ++chatOpenVersion;
        try
        {
            if (!knownChats.TryGetValue(chatId, out var chat))
            {
                var result = await telegram.RequestAsync(new { @type = "getChat", chat_id = chatId }, lifetime.Token);
                if (version != chatOpenVersion) return;
                UpsertChat(result);
                chat = knownChats[chatId];
            }
            if (activeChat is not null)
            {
                SaveCurrentDraft();
                SaveScrollPosition();
                _ = telegram.RequestAsync(new { @type = "closeChat", chat_id = activeChat.Id }, lifetime.Token);
            }
            activeChat = chat;
            pendingNotificationChat = null;
            ChatList.SelectedItem = chat;
            EmptyChat.Visibility = Visibility.Collapsed;
            ConversationPage.Visibility = Visibility.Visible;
            ActiveChatTitle.Text = chat.Title;
            ActiveChatAvatarLetter.Text = chat.AvatarLetter;
            ActiveChatAvatarBrush.ImageSource = chat.Avatar;
            UpdateComposerAvailability();
            suppressDraftSave = true;
            MessageInput.Text = preferences.Drafts.GetValueOrDefault(chat.Id, "");
            suppressDraftSave = false;
            CancelMessageAction(null!, null!);
            UpdateActiveStatus();
            await telegram.RequestAsync(new { @type = "openChat", chat_id = chat.Id }, lifetime.Token);
            if (version != chatOpenVersion) return;
            await PreparePinnedMessagesAsync(chat.Id, version);
            if (version != chatOpenVersion) return;
            if (!histories.TryGetValue(chatId, out var history))
            {
                history = new ChatHistory();
                histories[chatId] = history;
                var hasSavedPosition = scrollPositions.TryGetValue(chatId, out var savedOffset);
                historyRestores++;
                try
                {
                    await InitializeHistoryAsync(chatId, history, version);
                    if (historyDepths.TryGetValue(chatId, out var depth))
                    {
                        while (version == chatOpenVersion && activeChat?.Id == chatId && history.HasMore && history.Messages.Count < depth)
                        {
                            var before = history.Messages.Count;
                            await LoadOlderHistoryAsync(chatId, history);
                            if (history.Messages.Count == before) break;
                        }
                    }
                    if (version == chatOpenVersion && activeChat?.Id == chatId && hasSavedPosition)
                        messageScroller?.ChangeView(null, savedOffset, null, true);
                }
                finally { historyRestores--; }
            }
            else
            {
                history.LastUsed = DateTime.UtcNow;
                RenderHistory(chatId);
                RestoreScrollPosition(chatId);
            }
            if (activeChat?.Id == chatId) await MarkReadAsync();
            TrimHistoryCache();
            if (chat.UserId != 0 && activeChat?.Id == chatId) _ = LoadUserStatusAsync(chat.UserId);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            MessengerStatus.Text = "Не удалось открыть чат. " + ex.Message;
        }
    }

    private async Task InitializeHistoryAsync(long chatId, ChatHistory history, long openVersion)
    {
        if (history.Loading || telegram is null) return;
        history.Loading = true;
        SetHistoryLoading(chatId, true, "Загружаем историю…");
        try
        {
            var cached = await MessageHistoryCache.LoadAsync(chatId, lifetime.Token);
            foreach (var row in cached) history.Messages.TryAdd(row.Id, row);
            UpdateOldestMessage(history);
            if (activeChat?.Id == chatId && history.Messages.Count > 0)
            {
                RenderHistory(chatId);
                await ScrollToLatestAsync(chatId);
            }

            history.PageCursorId = 0;
            var freshCount = await FetchHistoryPageAsync(chatId, history, 0);
            while (history.HasMore && freshCount < InitialHistoryLimit)
            {
                var received = await FetchHistoryPageAsync(chatId, history, history.PageCursorId);
                if (received == 0) break;
                freshCount += received;
            }
            TrimToLatest(history, InitialHistoryLimit);
            history.LastUsed = DateTime.UtcNow;
            if (openVersion == chatOpenVersion && activeChat?.Id == chatId)
            {
                RenderHistory(chatId);
                await ScrollToLatestAsync(chatId);
                await MarkReadAsync();
            }
            _ = SaveHistoryCacheAsync(chatId, history);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (activeChat?.Id == chatId && !lifetime.IsCancellationRequested) MessengerStatus.Text = "История не загрузилась. " + ex.Message;
        }
        finally
        {
            history.Loading = false;
            SetHistoryLoading(chatId, false);
        }
    }

    private async Task LoadOlderHistoryAsync(long chatId, ChatHistory history)
    {
        if (history.Loading || !history.HasMore || telegram is null || history.PageCursorId == 0) return;
        history.Loading = true;
        SetHistoryLoading(chatId, true, "Загружаем старые сообщения…");
        var oldHeight = messageScroller?.ExtentHeight ?? 0;
        var oldOffset = messageScroller?.VerticalOffset ?? 0;
        try
        {
            await FetchHistoryPageAsync(chatId, history, history.PageCursorId);
            history.LastUsed = DateTime.UtcNow;
            if (activeChat?.Id == chatId)
            {
                RenderHistory(chatId);
                MessageList.UpdateLayout();
                messageScroller?.ChangeView(null, oldOffset + (messageScroller.ExtentHeight - oldHeight), null, true);
            }
            _ = SaveHistoryCacheAsync(chatId, history);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (activeChat?.Id == chatId && !lifetime.IsCancellationRequested) MessengerStatus.Text = "История не загрузилась. " + ex.Message;
        }
        finally
        {
            history.Loading = false;
            SetHistoryLoading(chatId, false);
        }
    }

    private async Task<int> FetchHistoryPageAsync(long chatId, ChatHistory history, long fromMessageId)
    {
        if (telegram is null) return 0;
        var result = await telegram.RequestAsync(new
        {
            @type = "getChatHistory", chat_id = chatId, from_message_id = fromMessageId,
            offset = 0, limit = 100, only_local = false
        }, lifetime.Token);
        var received = result.Field("messages").Items().ToArray();
        var previousCursor = history.PageCursorId;
        foreach (var message in received)
        {
            var row = MessageRow.FromJson(message);
            if (row.Id == 0) continue;
            // A TDLib file id belongs to the current client/database state. Always replace
            // a cached message with the fresh TDLib object instead of retaining stale ids.
            history.Messages[row.Id] = row;
        }
        UpdateOldestMessage(history);
        if (received.Length > 0) history.PageCursorId = received.Min(message => message.Field("id").Number());
        if (received.Length == 0 || history.PageCursorId == 0 || fromMessageId != 0 && history.PageCursorId >= previousCursor) history.HasMore = false;
        return received.Length;
    }

    private static void UpdateOldestMessage(ChatHistory history) =>
        history.OldestId = history.Messages.Count == 0 ? 0 : history.Messages.Keys.Min();

    private static void TrimToLatest(ChatHistory history, int limit)
    {
        if (history.Messages.Count <= limit) return;
        foreach (var id in history.Messages.Values.OrderByDescending(message => message.Date).ThenByDescending(message => message.Id).Skip(limit).Select(message => message.Id).ToArray())
            history.Messages.Remove(id);
        UpdateOldestMessage(history);
    }

    private async Task ScrollToLatestAsync(long chatId)
    {
        if (activeChat?.Id != chatId || visibleMessages.Count == 0) return;
        var last = visibleMessages[^1];
        MessageList.ScrollIntoView(last);
        await Task.Delay(60, lifetime.Token);
        if (activeChat?.Id != chatId) return;
        MessageList.UpdateLayout();
        MessageList.ScrollIntoView(last);
        messageScroller?.ChangeView(null, double.MaxValue, null, true);
    }

    private async Task SaveHistoryCacheAsync(long chatId, ChatHistory history)
    {
        try { await MessageHistoryCache.SaveAsync(chatId, history.Messages.Values.ToArray(), lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void SetHistoryLoading(long chatId, bool visible, string text = "")
    {
        if (activeChat?.Id != chatId) return;
        HistoryLoadingText.Text = text;
        HistoryLoadingPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HistoryLoadingRing.IsActive = visible;
    }

    private void RenderHistory(long chatId)
    {
        if (!histories.TryGetValue(chatId, out var history)) return;
        visibleMessages.Clear();
        DateTime? lastDay = null;
        var lastRead = knownChats.TryGetValue(chatId, out var chat) ? chat.LastReadOutboxId : 0;
        var query = MessageSearch.Text.Trim();
        MessageRow? previous = null;
        foreach (var row in history.Messages.Values.OrderBy(m => m.Date).ThenBy(m => m.Id).Where(m => query.Length == 0 || m.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            RegisterMessageFiles(row);
            if (lastDay != row.Date.Date)
            {
                visibleMessages.Add(new MessageRow { IsDate = true, Date = row.Date });
                lastDay = row.Date.Date;
            }
            row.UpdateAppearance(lightTheme, lastRead);
            row.GroupedWithPrevious = previous is not null && previous.Outgoing == row.Outgoing && previous.Author == row.Author && row.Date - previous.Date < TimeSpan.FromMinutes(5);
            if (!row.Outgoing && chat is not null)
            {
                row.SenderAvatar = chat.Avatar;
                row.SenderInitial = chat.AvatarLetter;
            }
            visibleMessages.Add(row);
            previous = row;
        }
    }

    private void RegisterMessageFiles(MessageRow row)
    {
        foreach (var fileId in new[] { row.PrimaryFileId, row.PreviewFileId }.Where(id => id != 0))
        {
            if (!mediaFiles.TryGetValue(fileId, out var rows)) mediaFiles[fileId] = rows = [];
            rows.Add(row);
        }
    }

    private void RefreshReadMarks()
    {
        if (activeChat is null || !histories.TryGetValue(activeChat.Id, out var history)) return;
        foreach (var message in history.Messages.Values) message.UpdateAppearance(lightTheme, activeChat.LastReadOutboxId);
    }

    private async Task MarkReadAsync()
    {
        if (activeChat is null || telegram is null || !histories.TryGetValue(activeChat.Id, out var history)) return;
        var last = history.Messages.Values.Where(m => !m.Outgoing).OrderByDescending(m => m.Id).FirstOrDefault();
        if (last is null) return;
        if (lastMarkedRead.GetValueOrDefault(activeChat.Id) >= last.Id) return;
        try
        {
            await telegram.RequestAsync(new
            {
                @type = "viewMessages", chat_id = activeChat.Id, message_ids = new[] { last.Id },
                source = new Dictionary<string, string> { ["@type"] = "messageSourceChatHistory" }, force_read = true
            }, lifetime.Token);
            lastMarkedRead[activeChat.Id] = last.Id;
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void TrimHistoryCache()
    {
        var changed = false;
        foreach (var cached in histories.Where(h => h.Key != activeChat?.Id && h.Value.Messages.Count > InitialHistoryLimit))
        {
            historyDepths[cached.Key] = Math.Max(historyDepths.GetValueOrDefault(cached.Key), cached.Value.Messages.Count);
            TrimToLatest(cached.Value, InitialHistoryLimit);
            changed = true;
        }
        foreach (var old in histories.Where(h => h.Key != activeChat?.Id).OrderByDescending(h => h.Value.LastUsed).Skip(4).ToArray())
        {
            historyDepths[old.Key] = old.Value.Messages.Count;
            histories.Remove(old.Key);
            changed = true;
        }
        if (!changed) return;
        mediaFiles.Clear();
        foreach (var history in histories.Values)
            foreach (var message in history.Messages.Values) RegisterMessageFiles(message);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroller) return scroller;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void MessageListLoaded(object sender, RoutedEventArgs args)
    {
        messageScroller = FindScrollViewer(MessageList);
        if (messageScroller is not null) messageScroller.ViewChanged += MessageScrollChanged;
        MessageList.ContainerContentChanging -= MessageContainerChanging;
        MessageList.ContainerContentChanging += MessageContainerChanging;
    }

    private async void MessageScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (historyRestores > 0 || activeChat is null || messageScroller is null || !histories.TryGetValue(activeChat.Id, out var history)) return;
        scrollPositions[activeChat.Id] = messageScroller.VerticalOffset;
        JumpToLatestButton.Visibility = messageScroller.VerticalOffset < messageScroller.ScrollableHeight - 240 ? Visibility.Visible : Visibility.Collapsed;
        if (messageScroller.VerticalOffset < 80 && history.HasMore && !history.Loading)
            await LoadOlderHistoryAsync(activeChat.Id, history);
        if (messageScroller.VerticalOffset >= messageScroller.ScrollableHeight - 80)
            await MarkReadAsync();
    }

    private void SaveScrollPosition()
    {
        if (activeChat is not null && messageScroller is not null)
            scrollPositions[activeChat.Id] = messageScroller.VerticalOffset;
    }

    private void RestoreScrollPosition(long chatId)
    {
        MessageList.UpdateLayout();
        if (scrollPositions.TryGetValue(chatId, out var offset)) messageScroller?.ChangeView(null, offset, null, true);
        else messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null, true);
    }

    private void ResizeChatPressed(object sender, PointerRoutedEventArgs args)
    {
        resizingChats = true;
        resizeStartX = args.GetCurrentPoint(ChatsPage).Position.X;
        resizeStartWidth = ChatColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(args.Pointer);
    }

    private void ResizeChatMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!resizingChats) return;
        var delta = args.GetCurrentPoint(ChatsPage).Position.X - resizeStartX;
        ChatColumn.Width = new GridLength(Math.Clamp(resizeStartWidth + delta, 240, Math.Max(240, ChatsPage.ActualWidth - 360)));
    }

    private void ResizeChatReleased(object sender, PointerRoutedEventArgs args)
    {
        resizingChats = false;
        ((UIElement)sender).ReleasePointerCapture(args.Pointer);
        preferences.ChatPaneWidth = ChatColumn.Width.Value;
        SavePreferences();
    }

    private void ResizeChatCaptureLost(object sender, PointerRoutedEventArgs args) => resizingChats = false;

    private void ToggleMessageSearch(object sender, RoutedEventArgs args)
    {
        MessageSearch.Visibility = MessageSearch.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ChatSearchTools.Visibility = MessageSearch.Visibility;
        if (MessageSearch.Visibility == Visibility.Visible) MessageSearch.Focus(FocusState.Programmatic);
        else { MessageSearch.Text = ""; ResetChatSearch(true); }
    }

    private void UpdateComposerAvailability()
    {
        if (activeChat is null) return;
        ComposerPanel.Visibility = activeChat.CanSendBasicMessages ? Visibility.Visible : Visibility.Collapsed;
    }
}
