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
    private readonly Dictionary<long, ChatRow> knownChats = [];
    private readonly ObservableCollection<ChatRow> visibleChats = [];
    private readonly ObservableCollection<MessageRow> visibleMessages = [];
    private readonly Dictionary<long, ChatHistory> histories = [];
    private readonly Dictionary<long, double> scrollPositions = [];
    private readonly Dictionary<int, long> avatarFiles = [];
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
    private double resizeStartX;
    private double resizeStartWidth;
    private DateTime lastTypingSent;
    private DateTime lastTypingReceived;

    private sealed class ChatHistory
    {
        public Dictionary<long, MessageRow> Messages { get; } = [];
        public long OldestId { get; set; }
        public bool HasMore { get; set; } = true;
        public bool Loading { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    private void StartMessenger()
    {
        if (messengerStarted || telegram is null) return;
        messengerStarted = true;
        ChatList.ItemsSource = visibleChats;
        MessageList.ItemsSource = visibleMessages;
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
        visibleChats.Clear();
        visibleMessages.Clear();
        histories.Clear();
        avatarFiles.Clear();
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
            try
            {
                var saved = await client.RequestAsync(new { @type = "createPrivateChat", user_id = savedChatId, force = false }, lifetime.Token);
                UpsertChat(saved, saved: true);
            }
            catch (TdException) { }
            await Task.WhenAll(LoadChatListAsync(client, "chatListMain"), LoadChatListAsync(client, "chatListArchive"));
            if (pendingNotificationChat is long chatId) await OpenChatAsync(chatId);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
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
            foreach (var id in chats.Field("chat_ids").Items())
            {
                var chatId = id.Number();
                if (knownChats.TryGetValue(chatId, out var existing))
                {
                    existing.ApplyList(listType);
                    RefreshChatList();
                    continue;
                }
                var chat = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, lifetime.Token);
                UpsertChat(chat, sourceList: listType);
            }
            if (loadedAll) break;
        }
    }

    private void UpsertChat(JsonElement chat, bool saved = false, string? sourceList = null)
    {
        var id = chat.Field("id").Number();
        if (id == 0) return;
        if (!knownChats.TryGetValue(id, out var row)) knownChats[id] = row = new ChatRow(id);
        row.IsSaved |= saved || id == savedChatId;
        row.Update(chat);
        if (sourceList is not null) row.ApplyList(sourceList, order: chat.Field("last_message").Field("date").Number());
        if (row.PhotoFileId != 0 && row.Avatar is null)
        {
            avatarFiles[row.PhotoFileId] = id;
            _ = DownloadAvatarAsync(row.PhotoFileId);
        }
        RefreshChatList();
        if (activeChat?.Id == id)
        {
            activeChat = row;
            ActiveChatTitle.Text = row.Title;
            UpdateActiveStatus();
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
        var selected = activeChat?.Id;
        var query = ChatSearch.Text.Trim();
        var rows = knownChats.Values
            .Where(c => c.IsMain || c.IsArchive || c.IsSaved)
            .Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(c => c.IsSaved)
            .ThenByDescending(c => c.IsMain)
            .ThenByDescending(c => c.Order)
            .ToArray();
        if (rows.Length == visibleChats.Count && rows.SequenceEqual(visibleChats)) return;
        visibleChats.Clear();
        foreach (var row in rows) visibleChats.Add(row);
        if (selected is long id && knownChats.TryGetValue(id, out var current) && visibleChats.Contains(current))
            ChatList.SelectedItem = current;
    }

    private void SearchChats(object sender, TextChangedEventArgs args) => RefreshChatList();

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
                SaveScrollPosition();
                _ = telegram.RequestAsync(new { @type = "closeChat", chat_id = activeChat.Id }, lifetime.Token);
            }
            activeChat = chat;
            pendingNotificationChat = null;
            ChatList.SelectedItem = chat;
            EmptyChat.Visibility = Visibility.Collapsed;
            ConversationPage.Visibility = Visibility.Visible;
            ActiveChatTitle.Text = chat.Title;
            MessageInput.Text = "";
            CancelMessageAction(null!, null!);
            UpdateActiveStatus();
            await telegram.RequestAsync(new { @type = "openChat", chat_id = chat.Id }, lifetime.Token);
            if (version != chatOpenVersion) return;
            if (!histories.TryGetValue(chatId, out var history))
            {
                history = new ChatHistory();
                histories[chatId] = history;
                await LoadHistoryAsync(chatId, history, initial: true);
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

    private async Task LoadHistoryAsync(long chatId, ChatHistory history, bool initial = false)
    {
        if (history.Loading || !history.HasMore || history.Messages.Count >= 500 || telegram is null) return;
        history.Loading = true;
        var oldHeight = messageScroller?.ExtentHeight ?? 0;
        var oldOffset = messageScroller?.VerticalOffset ?? 0;
        try
        {
            var result = await telegram.RequestAsync(new
            {
                @type = "getChatHistory", chat_id = chatId, from_message_id = initial ? 0 : history.OldestId,
                offset = 0, limit = initial ? 40 : Math.Min(100, 500 - history.Messages.Count), only_local = false
            }, lifetime.Token);
            var received = result.Field("messages").Items().ToArray();
            var newCount = 0;
            foreach (var message in received)
            {
                var row = MessageRow.FromJson(message);
                if (row.Id != 0 && history.Messages.TryAdd(row.Id, row)) newCount++;
            }
            history.OldestId = history.Messages.Count == 0 ? 0 : history.Messages.Keys.Min();
            if (received.Length == 0 || (!initial && newCount == 0) || history.Messages.Count >= 500) history.HasMore = false;
            history.LastUsed = DateTime.UtcNow;
            if (activeChat?.Id == chatId)
            {
                RenderHistory(chatId);
                MessageList.UpdateLayout();
                if (initial) messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null, true);
                else messageScroller?.ChangeView(null, oldOffset + (messageScroller.ExtentHeight - oldHeight), null, true);
                if (initial) await MarkReadAsync();
            }
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (activeChat?.Id == chatId && !lifetime.IsCancellationRequested) MessengerStatus.Text = "История не загрузилась. " + ex.Message;
        }
        finally { history.Loading = false; }
    }

    private void RenderHistory(long chatId)
    {
        if (!histories.TryGetValue(chatId, out var history)) return;
        visibleMessages.Clear();
        DateTime? lastDay = null;
        var lastRead = knownChats.TryGetValue(chatId, out var chat) ? chat.LastReadOutboxId : 0;
        foreach (var row in history.Messages.Values.OrderBy(m => m.Date).ThenBy(m => m.Id))
        {
            if (lastDay != row.Date.Date)
            {
                visibleMessages.Add(new MessageRow { IsDate = true, Date = row.Date });
                lastDay = row.Date.Date;
            }
            row.UpdateAppearance(lightTheme, lastRead);
            visibleMessages.Add(row);
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
        foreach (var old in histories.Where(h => h.Key != activeChat?.Id).OrderByDescending(h => h.Value.LastUsed).Skip(4).ToArray())
            histories.Remove(old.Key);
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
    }

    private async void MessageScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (activeChat is null || messageScroller is null || !histories.TryGetValue(activeChat.Id, out var history)) return;
        scrollPositions[activeChat.Id] = messageScroller.VerticalOffset;
        if (messageScroller.VerticalOffset < 80 && history.HasMore && !history.Loading)
            await LoadHistoryAsync(activeChat.Id, history);
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
}
