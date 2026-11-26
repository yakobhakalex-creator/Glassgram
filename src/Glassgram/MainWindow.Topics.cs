using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<ForumTopicRow> forumTopics = [];
    private readonly Dictionary<HistoryContext, ChatHistory> contextualHistories = [];
    private readonly Dictionary<HistoryContext, double> contextScrollPositions = [];
    private readonly Dictionary<HistoryContext, string> contextDrafts = [];
    private HistoryContext? activeHistoryContext;
    private CancellationTokenSource? forumSearchCancellation;
    private int forumLoadVersion;
    private int forumOffsetDate;
    private long forumOffsetMessageId;
    private int forumOffsetTopicId;
    private bool forumHasMore;
    private bool canManageForumTopics;

    private object? ActiveTopicId => activeHistoryContext is { Kind: HistoryContextKind.ForumTopic } context
        ? new Dictionary<string, object?> { ["@type"] = "messageTopicForum", ["forum_topic_id"] = (int)context.ContextId }
        : activeHistoryContext is { Kind: HistoryContextKind.MessageThread } thread
            ? new Dictionary<string, object?> { ["@type"] = "messageTopicThread", ["message_thread_id"] = thread.ContextId }
            : null;
    private long ActiveSendChatId => activeHistoryContext?.ChatId ?? activeChat?.Id ?? 0;

    private void InsertContextMessage(MessageRow row)
    {
        if (activeHistoryContext is not HistoryContext context || row.ChatId != context.ChatId || !contextualHistories.TryGetValue(context, out var history)) return;
        history.Messages[row.Id] = row;
        RenderContextHistory(context, history);
    }

    private sealed record ForumTopicRow(int Id, string Name, long IconEmojiId, string Icon, string Preview, int UnreadCount, bool IsPinned, bool IsClosed, bool IsHidden, int LastDate, long LastMessageId)
    {
        public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : "";
        public string StateText => string.Join(" · ", new[] { IsPinned ? "закреплена" : "", IsClosed ? "закрыта" : "", IsHidden ? "скрыта" : "" }.Where(x => x.Length > 0));
    }

    private async Task PrepareForumAsync(ChatRow chat, long version)
    {
        LeaveHistoryContextCore(renderMain: false);
        ForumTopicsButton.Visibility = Visibility.Collapsed;
        ForumTopicsPanel.Visibility = Visibility.Collapsed;
        if (telegram is null || chat.SupergroupId == 0) return;
        try
        {
            var supergroup = await telegram.RequestAsync(new { @type = "getSupergroup", supergroup_id = chat.SupergroupId }, lifetime.Token);
            if (version != chatOpenVersion || activeChat?.Id != chat.Id) return;
            if (!supergroup.Field("is_forum").Flag()) return;
            var member = await telegram.RequestAsync(new { @type = "getChatMember", chat_id = chat.Id, member_id = new { @type = "messageSenderUser", user_id = savedChatId } }, lifetime.Token);
            var status = member.Field("status");
            canManageForumTopics = status.Field("@type").Text() == "chatMemberStatusCreator" || status.Field("rights").Field("can_manage_topics").Flag();
            CreateForumTopicButton.Visibility = canManageForumTopics ? Visibility.Visible : Visibility.Collapsed;
            ForumTopicsButton.Visibility = Visibility.Visible;
            ForumTopicList.ItemsSource ??= forumTopics;
            await LoadForumTopicsAsync(reset: true);
            ForumTopicsPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Темы недоступны. " + ex.Message; }
    }

    private async void ShowForumTopics(object sender, RoutedEventArgs args)
    {
        LeaveHistoryContextCore(renderMain: true);
        ForumTopicsPanel.Visibility = Visibility.Visible;
        if (forumTopics.Count == 0) await LoadForumTopicsAsync(reset: true);
    }

    private void HideForumTopics(object sender, RoutedEventArgs args) => ForumTopicsPanel.Visibility = Visibility.Collapsed;

    private async void ForumTopicSearchChanged(object sender, TextChangedEventArgs args)
    {
        forumSearchCancellation?.Cancel();
        var cancellation = forumSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await Task.Delay(350, cancellation.Token); if (!cancellation.IsCancellationRequested) await LoadForumTopicsAsync(reset: true); }
        catch (OperationCanceledException) { }
    }

    private async void LoadMoreForumTopics(object sender, RoutedEventArgs args) => await LoadForumTopicsAsync(reset: false);

    private async Task LoadForumTopicsAsync(bool reset)
    {
        if (telegram is null || activeChat is null) return;
        var version = ++forumLoadVersion;
        if (reset) { forumOffsetDate = 0; forumOffsetMessageId = 0; forumOffsetTopicId = 0; forumHasMore = true; }
        if (!forumHasMore) return;
        ForumTopicsStatus.Text = "Загрузка…";
        try
        {
            var result = await telegram.RequestAsync(new { @type = "getForumTopics", chat_id = activeChat.Id, query = ForumTopicSearch.Text.Trim(), offset_date = forumOffsetDate, offset_message_id = forumOffsetMessageId, offset_forum_topic_id = forumOffsetTopicId, limit = 50 }, lifetime.Token);
            if (version != forumLoadVersion) return;
            var received = result.Field("topics").Items().ToArray();
            if (reset) forumTopics.Clear();
            var known = forumTopics.Select(x => x.Id).ToHashSet();
            foreach (var topic in received)
            {
                var info = topic.Field("info");
                var last = topic.Field("last_message");
                var id = (int)info.Field("forum_topic_id").Number();
                if (id == 0 || !known.Add(id)) continue;
                var icon = info.Field("icon").Field("custom_emoji_id").Number() != 0 ? "✦" : "#";
                forumTopics.Add(new(id, info.Field("name").Text(), info.Field("icon").Field("custom_emoji_id").Number(), icon, TdJson.MessageText(last), (int)topic.Field("unread_count").Number(), topic.Field("is_pinned").Flag(), info.Field("is_closed").Flag(), info.Field("is_hidden").Flag(), (int)last.Field("date").Number(), last.Field("id").Number()));
            }
            var tail = received.LastOrDefault();
            var tailInfo = tail.Field("info");
            var tailMessage = tail.Field("last_message");
            forumOffsetDate = (int)tailMessage.Field("date").Number();
            forumOffsetMessageId = tailMessage.Field("id").Number();
            forumOffsetTopicId = (int)tailInfo.Field("forum_topic_id").Number();
            forumHasMore = received.Length == 50 && forumOffsetTopicId != 0;
            ForumTopicsMoreButton.Visibility = forumHasMore ? Visibility.Visible : Visibility.Collapsed;
            ForumTopicsStatus.Text = forumTopics.Count == 0 ? "Темы не найдены" : $"Тем: {forumTopics.Count}";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { if (version == forumLoadVersion) ForumTopicsStatus.Text = "Не удалось загрузить темы. " + ex.Message; }
    }

    private async void OpenForumTopic(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ForumTopicRow topic || activeChat is null) return;
        ForumTopicsPanel.Visibility = Visibility.Collapsed;
        var context = HistoryContext.ForumTopic(activeChat.Id, topic.Id);
        HistoryContextTitle.Text = topic.Name;
        HistoryContextSubtitle.Text = topic.StateText.Length == 0 ? "Тема форума" : "Тема форума · " + topic.StateText;
        await OpenContextHistoryAsync(context, topic.Id);
    }

    private async void CreateForumTopic(object sender, RoutedEventArgs args)
    {
        if (!canManageForumTopics || telegram is null || activeChat is null) return;
        var input = new TextBox { PlaceholderText = "Название темы", MaxLength = 128 };
        var emoji = new NumberBox { Header = "ID custom emoji (0 — стандартная иконка)", Minimum = 0, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(input); panel.Children.Add(emoji);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Новая тема", Content = panel, PrimaryButtonText = "Создать", CloseButtonText = "Отмена" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        await ExecuteTopicActionAsync(new { @type = "createForumTopic", chat_id = activeChat.Id, name = input.Text.Trim(), is_name_implicit = false, icon = new { @type = "forumTopicIcon", color = 0x6FB9F0, custom_emoji_id = double.IsNaN(emoji.Value) ? 0L : (long)emoji.Value } }, "Тема создана");
    }

    private void ForumTopicContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!canManageForumTopics || (sender as FrameworkElement)?.DataContext is not ForumTopicRow topic) return;
        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Переименовать", Tag = topic }; rename.Click += EditForumTopic; flyout.Items.Add(rename);
        var pin = new MenuFlyoutItem { Text = topic.IsPinned ? "Открепить" : "Закрепить", Tag = topic }; pin.Click += ToggleForumTopicPinned; flyout.Items.Add(pin);
        var close = new MenuFlyoutItem { Text = topic.IsClosed ? "Открыть снова" : "Закрыть", Tag = topic }; close.Click += ToggleForumTopicClosed; flyout.Items.Add(close);
        var notifications = new MenuFlyoutItem { Text = "Уведомления", Tag = topic }; notifications.Click += EditForumTopicNotifications; flyout.Items.Add(notifications);
        if (topic.Id != 1) { var delete = new MenuFlyoutItem { Text = "Удалить", Tag = topic }; delete.Click += DeleteForumTopic; flyout.Items.Add(delete); }
        flyout.ShowAt(sender, args.TryGetPosition(sender, out var point) ? point : new Windows.Foundation.Point(0, 0));
    }

    private async void EditForumTopic(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ForumTopicRow topic || activeChat is null) return;
        var input = new TextBox { Text = topic.Name, MaxLength = 128 };
        var emoji = new NumberBox { Header = "ID custom emoji (0 — без custom emoji)", Minimum = 0, Value = topic.IconEmojiId, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(input); if (topic.Id != 1) panel.Children.Add(emoji);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Изменить тему", Content = panel, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        var emojiId = double.IsNaN(emoji.Value) ? topic.IconEmojiId : (long)emoji.Value;
        await ExecuteTopicActionAsync(new { @type = "editForumTopic", chat_id = activeChat.Id, forum_topic_id = topic.Id, name = input.Text.Trim(), edit_icon_custom_emoji = topic.Id != 1 && emojiId != topic.IconEmojiId, icon_custom_emoji_id = emojiId }, "Тема изменена");
    }

    private async void ToggleForumTopicPinned(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is ForumTopicRow topic && activeChat is not null)
            await ExecuteTopicActionAsync(new { @type = "toggleForumTopicIsPinned", chat_id = activeChat.Id, forum_topic_id = topic.Id, is_pinned = !topic.IsPinned }, topic.IsPinned ? "Тема откреплена" : "Тема закреплена");
    }

    private async void ToggleForumTopicClosed(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is ForumTopicRow topic && activeChat is not null)
            await ExecuteTopicActionAsync(new { @type = "toggleForumTopicIsClosed", chat_id = activeChat.Id, forum_topic_id = topic.Id, is_closed = !topic.IsClosed }, topic.IsClosed ? "Тема открыта" : "Тема закрыта");
    }

    private async void DeleteForumTopic(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ForumTopicRow topic || activeChat is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Удалить тему?", Content = $"Тема «{topic.Name}» и её сообщения будут удалены.", PrimaryButtonText = "Удалить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ExecuteTopicActionAsync(new { @type = "deleteForumTopic", chat_id = activeChat.Id, forum_topic_id = topic.Id }, "Тема удалена");
    }

    private async void EditForumTopicNotifications(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ForumTopicRow topic || activeChat is null || telegram is null) return;
        try
        {
            var current = await telegram.RequestAsync(new { @type = "getForumTopic", chat_id = activeChat.Id, forum_topic_id = topic.Id }, lifetime.Token);
            var settings = current.Field("notification_settings");
            var enabled = settings.Field("mute_for").Number() == 0;
            var toggle = new ToggleSwitch { Header = "Уведомления этой темы", IsOn = enabled };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Уведомления темы", Content = toggle, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
            node["use_default_mute_for"] = false;
            node["mute_for"] = toggle.IsOn ? 0 : int.MaxValue;
            await ExecuteTopicActionAsync(new { @type = "setForumTopicNotificationSettings", chat_id = activeChat.Id, forum_topic_id = topic.Id, notification_settings = node }, "Настройки уведомлений сохранены");
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { MessengerStatus.Text = "Настройки уведомлений недоступны. " + ex.Message; }
    }

    private async Task ExecuteTopicActionAsync(object request, string success)
    {
        if (telegram is null) return;
        try { await telegram.RequestAsync(request, lifetime.Token); MessengerStatus.Text = success; await LoadForumTopicsAsync(reset: true); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Операция с темой не выполнена. " + ex.Message; }
    }

    private async void OpenMessageComments(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null) return;
        var priorOffset = messageScroller?.VerticalOffset ?? 0;
        try
        {
            var info = await telegram.RequestAsync(new { @type = "getMessageThread", chat_id = row.ChatId, message_id = row.Id }, lifetime.Token);
            var threadChatId = info.Field("chat_id").Number();
            var threadId = info.Field("message_thread_id").Number();
            if (threadChatId == 0 || threadId == 0) throw new InvalidOperationException("Обсуждение недоступно.");
            if (activeChat is not null) scrollPositions[activeChat.Id] = priorOffset;
            var context = HistoryContext.MessageThread(threadChatId, threadId, row.ChatId, row.Id);
            HistoryContextTitle.Text = "Комментарии";
            HistoryContextSubtitle.Text = row.Text.Length > 0 ? row.Text : "Публикация канала";
            await OpenContextHistoryAsync(context, row.Id, info.Field("messages"));
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or InvalidOperationException) { MessengerStatus.Text = "Комментарии недоступны. " + ex.Message; }
    }

    private async Task OpenContextHistoryAsync(HistoryContext context, long requestMessageId, JsonElement initial = default)
    {
        if (telegram is null) return;
        if (activeHistoryContext is HistoryContext old && messageScroller is not null) contextScrollPositions[old] = messageScroller.VerticalOffset;
        activeHistoryContext = context;
        suppressDraftSave = true;
        MessageInput.Text = contextDrafts.GetValueOrDefault(context, "");
        suppressDraftSave = false;
        HistoryContextBar.Visibility = Visibility.Visible;
        PinnedMessageBar.Visibility = Visibility.Collapsed;
        ForumTopicsPanel.Visibility = Visibility.Collapsed;
        if (!contextualHistories.TryGetValue(context, out var history)) contextualHistories[context] = history = new ChatHistory();
        if (initial.ValueKind == JsonValueKind.Array) AddContextMessages(history, initial);
        if (history.Messages.Count == 0) await FetchContextPageAsync(context, history, requestMessageId, 0);
        RenderContextHistory(context, history);
        RestoreContextScroll(context);
    }

    private async Task<int> FetchContextPageAsync(HistoryContext context, ChatHistory history, long requestMessageId, long fromMessageId)
    {
        if (telegram is null || history.Loading) return 0;
        history.Loading = true;
        try
        {
            object request = context.Kind == HistoryContextKind.ForumTopic
                ? new { @type = "getForumTopicHistory", chat_id = context.ChatId, forum_topic_id = (int)context.ContextId, from_message_id = fromMessageId, offset = 0, limit = 100 }
                : new { @type = "getMessageThreadHistory", chat_id = context.SourceChatId, message_id = requestMessageId, from_message_id = fromMessageId, offset = 0, limit = 100 };
            var result = await telegram.RequestAsync(request, lifetime.Token);
            var messages = result.Field("messages");
            var count = AddContextMessages(history, messages);
            var ids = messages.Items().Select(x => x.Field("id").Number()).Where(x => x != 0).ToArray();
            if (ids.Length > 0) history.PageCursorId = ids.Min();
            history.HasMore = count > 0 && history.PageCursorId != 0;
            return count;
        }
        finally { history.Loading = false; }
    }

    private static int AddContextMessages(ChatHistory history, JsonElement messages)
    {
        var count = 0;
        foreach (var message in messages.Items()) { var row = MessageRow.FromJson(message); if (row.Id != 0) { history.Messages[row.Id] = row; count++; } }
        return count;
    }

    private void RenderContextHistory(HistoryContext context, ChatHistory history)
    {
        if (activeHistoryContext != context) return;
        RenderRows(history, context.ChatId);
    }

    private void RenderRows(ChatHistory history, long chatId)
    {
        visibleMessages.Clear();
        DateTime? day = null; MessageRow? previous = null;
        var chat = knownChats.GetValueOrDefault(chatId) ?? activeChat;
        foreach (var row in history.Messages.Values.OrderBy(x => x.Date).ThenBy(x => x.Id))
        {
            RegisterMessageFiles(row);
            if (day != row.Date.Date) { visibleMessages.Add(new MessageRow { IsDate = true, Date = row.Date }); day = row.Date.Date; }
            row.UpdateAppearance(lightTheme, chat?.LastReadOutboxId ?? 0);
            row.GroupedWithPrevious = previous is not null && previous.Outgoing == row.Outgoing && previous.Author == row.Author && row.Date - previous.Date < TimeSpan.FromMinutes(5);
            visibleMessages.Add(row); previous = row;
        }
    }

    private async void ReloadHistoryContext(object sender, RoutedEventArgs args)
    {
        if (activeHistoryContext is not HistoryContext context) return;
        contextualHistories.Remove(context);
        await OpenContextHistoryAsync(context, context.Kind == HistoryContextKind.MessageThread ? context.SourceMessageId : context.ContextId);
    }

    private void LeaveHistoryContext(object sender, RoutedEventArgs args) => LeaveHistoryContextCore(renderMain: true);

    private void LeaveHistoryContextCore(bool renderMain)
    {
        if (activeHistoryContext is HistoryContext context && messageScroller is not null) contextScrollPositions[context] = messageScroller.VerticalOffset;
        activeHistoryContext = null;
        suppressDraftSave = true;
        MessageInput.Text = activeChat is null ? "" : preferences.Drafts.GetValueOrDefault(activeChat.Id, "");
        suppressDraftSave = false;
        HistoryContextBar.Visibility = Visibility.Collapsed;
        if (renderMain && activeChat is not null) { RenderHistory(activeChat.Id); RestoreScrollPosition(activeChat.Id); }
    }

    private void RestoreContextScroll(HistoryContext context)
    {
        MessageList.UpdateLayout();
        if (contextScrollPositions.TryGetValue(context, out var offset)) messageScroller?.ChangeView(null, offset, null, true);
        else messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null, true);
    }

    private async Task<bool> LoadOlderContextHistoryAsync()
    {
        if (activeHistoryContext is not HistoryContext context || !contextualHistories.TryGetValue(context, out var history) || !history.HasMore || history.Loading) return false;
        var oldHeight = messageScroller?.ExtentHeight ?? 0; var oldOffset = messageScroller?.VerticalOffset ?? 0;
        var requestId = context.Kind == HistoryContextKind.MessageThread ? context.SourceMessageId : context.ContextId;
        var count = await FetchContextPageAsync(context, history, requestId, history.PageCursorId);
        if (count > 0 && activeHistoryContext == context) { RenderContextHistory(context, history); MessageList.UpdateLayout(); messageScroller?.ChangeView(null, oldOffset + (messageScroller.ExtentHeight - oldHeight), null, true); }
        return count > 0;
    }
}
