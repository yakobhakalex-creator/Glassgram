using System.Text.Json;
using System.Runtime.InteropServices;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Glassgram;

public partial class MainWindow
{
    private void HandleMessengerUpdate(JsonElement update)
    {
        if (!messengerStarted) return;
        var kind = update.Field("@type").Text();
        var chatId = update.Field("chat_id").Number();
        switch (kind)
        {
            case "updateNewChat":
                UpsertChat(update.Field("chat"));
                break;
            case "updateChatLastMessage":
            case "updateChatTitle":
            case "updateChatPhoto":
            case "updateChatReadInbox":
            case "updateChatReadOutbox":
            case "updateChatIsMarkedAsUnread":
                _ = RefreshChatAsync(chatId);
                break;
            case "updateChatPosition":
                if (knownChats.TryGetValue(chatId, out var positioned))
                {
                    var position = update.Field("position");
                    var order = position.Field("order").Number();
                    positioned.ApplyList(position.Field("list").Field("@type").Text(), order != 0, order);
                    RefreshChatList();
                }
                break;
            case "updateNewMessage":
                InsertMessage(update.Field("message"));
                break;
            case "updateMessageSendSucceeded":
            case "updateMessageSendFailed":
                ReplacePendingMessage(update);
                break;
            case "updateMessageContent":
            case "updateMessageEdited":
                _ = RefreshMessageAsync(chatId, update.Field("message_id").Number());
                break;
            case "updateDeleteMessages":
                DeleteCachedMessages(update);
                break;
            case "updateFile":
                UpdateAvatar(update.Field("file"));
                break;
            case "updateUserStatus":
                UpdateUserStatus(update);
                break;
            case "updateChatAction":
                UpdateChatAction(update);
                break;
            case "updateNotificationGroup":
                ShowIncomingNotifications(update);
                break;
        }
    }

    private async Task RefreshChatAsync(long chatId)
    {
        if (chatId == 0 || telegram is null) return;
        try
        {
            var chat = await telegram.RequestAsync(new { @type = "getChat", chat_id = chatId }, lifetime.Token);
            if (messengerStarted) UpsertChat(chat);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void InsertMessage(JsonElement message)
    {
        var row = MessageRow.FromJson(message);
        if (row.ChatId == 0 || row.Id == 0) return;
        if (!histories.TryGetValue(row.ChatId, out var history)) return;
        var nearBottom = messageScroller is null || messageScroller.VerticalOffset >= messageScroller.ScrollableHeight - 100;
        history.Messages[row.Id] = row;
        if (history.Messages.Count > 500)
        {
            var oldest = history.Messages.Keys.Min();
            history.Messages.Remove(oldest);
            history.OldestId = history.Messages.Keys.Min();
        }
        if (activeChat?.Id != row.ChatId) return;
        var offset = messageScroller?.VerticalOffset ?? 0;
        RenderHistory(row.ChatId);
        MessageList.UpdateLayout();
        messageScroller?.ChangeView(null, nearBottom ? messageScroller.ScrollableHeight : offset, null, true);
        if (nearBottom) _ = MarkReadAsync();
    }

    private void ReplacePendingMessage(JsonElement update)
    {
        var message = update.Field("message");
        var chatId = message.Field("chat_id").Number();
        if (histories.TryGetValue(chatId, out var history))
            history.Messages.Remove(update.Field("old_message_id").Number());
        InsertMessage(message);
        if (update.Field("@type").Text() == "updateMessageSendFailed" && activeChat?.Id == chatId)
            MessengerStatus.Text = update.Field("error").Field("message").Text();
    }

    private async Task RefreshMessageAsync(long chatId, long messageId)
    {
        if (telegram is null || !histories.ContainsKey(chatId)) return;
        try
        {
            var message = await telegram.RequestAsync(new { @type = "getMessage", chat_id = chatId, message_id = messageId }, lifetime.Token);
            InsertMessage(message);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void DeleteCachedMessages(JsonElement update)
    {
        var chatId = update.Field("chat_id").Number();
        if (!histories.TryGetValue(chatId, out var history)) return;
        foreach (var id in update.Field("message_ids").Items()) history.Messages.Remove(id.Number());
        if (activeChat?.Id == chatId) RenderHistory(chatId);
    }

    private void UpdateAvatar(JsonElement file)
    {
        var id = (int)file.Field("id").Number();
        if (avatarFiles.TryGetValue(id, out var chatId) && knownChats.TryGetValue(chatId, out var chat))
            chat.SetAvatar(file.Field("local").Field("path").Text());
    }

    private async Task LoadUserStatusAsync(long userId)
    {
        if (telegram is null) return;
        try
        {
            var user = await telegram.RequestAsync(new { @type = "getUser", user_id = userId }, lifetime.Token);
            userStatuses[userId] = DescribeStatus(user.Field("status"));
            UpdateActiveStatus();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void UpdateUserStatus(JsonElement update)
    {
        var userId = update.Field("user_id").Number();
        userStatuses[userId] = DescribeStatus(update.Field("status"));
        UpdateActiveStatus();
    }

    private static string DescribeStatus(JsonElement status) => status.Field("@type").Text() switch
    {
        "userStatusOnline" => "в сети",
        "userStatusRecently" => "был(а) недавно",
        "userStatusLastWeek" => "был(а) на этой неделе",
        "userStatusLastMonth" => "был(а) в этом месяце",
        "userStatusOffline" => status.Field("was_online").LocalDate() is var date && date != DateTime.MinValue ? "был(а) " + date.ToString("dd.MM HH:mm") : "не в сети",
        _ => ""
    };

    private void UpdateActiveStatus()
    {
        if (activeChat is null) return;
        if (DateTime.UtcNow - lastTypingReceived < TimeSpan.FromSeconds(5))
        {
            ActiveChatStatus.Text = "печатает…";
            return;
        }
        ActiveChatStatus.Text = activeChat.IsSaved ? "Избранное" : activeChat.Kind switch
        {
            "chatTypePrivate" => userStatuses.GetValueOrDefault(activeChat.UserId, "личный чат"),
            "chatTypeBasicGroup" => "группа",
            "chatTypeSupergroup" => "группа или канал",
            _ => "чат"
        };
    }

    private void UpdateChatAction(JsonElement update)
    {
        if (activeChat?.Id != update.Field("chat_id").Number()) return;
        if (update.Field("sender_id").Field("user_id").Number() == savedChatId) return;
        lastTypingReceived = update.Field("action").Field("@type").Text() == "chatActionTyping" ? DateTime.UtcNow : DateTime.MinValue;
        UpdateActiveStatus();
        if (lastTypingReceived != DateTime.MinValue) _ = ClearTypingLaterAsync(lastTypingReceived);
    }

    private async Task ClearTypingLaterAsync(DateTime received)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            if (received == lastTypingReceived) { lastTypingReceived = DateTime.MinValue; UpdateActiveStatus(); }
        }
        catch (OperationCanceledException) { }
    }

    private async void MessageInputChanged(object sender, TextChangedEventArgs args)
    {
        if (telegram is null || activeChat is null || MessageInput.Text.Length == 0 || DateTime.UtcNow - lastTypingSent < TimeSpan.FromSeconds(4)) return;
        lastTypingSent = DateTime.UtcNow;
        try
        {
            await telegram.RequestAsync(new
            {
                @type = "sendChatAction", chat_id = activeChat.Id, topic_id = (object?)null,
                business_connection_id = "", action = new Dictionary<string, string> { ["@type"] = "chatActionTyping" }
            }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private async void SelectMessage(object sender, SelectionChangedEventArgs args)
    {
        selectedMessage = MessageList.SelectedItem as MessageRow;
        MessageActions.Visibility = selectedMessage is null || selectedMessage.IsDate ? Visibility.Collapsed : Visibility.Visible;
        EditButton.IsEnabled = DeleteButton.IsEnabled = ReplyButton.IsEnabled = false;
        if (selectedMessage is null || selectedMessage.IsDate || telegram is null || activeChat is null) return;
        var id = selectedMessage.Id;
        try
        {
            var properties = await telegram.RequestAsync(new { @type = "getMessageProperties", chat_id = activeChat.Id, message_id = id }, lifetime.Token);
            if (selectedMessage?.Id != id) return;
            ReplyButton.IsEnabled = properties.Field("can_be_replied").Flag();
            EditButton.IsEnabled = properties.Field("can_be_edited").Flag();
            DeleteButton.IsEnabled = properties.Field("can_be_deleted_only_for_self").Flag() || properties.Field("can_be_deleted_for_all_users").Flag();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void ReplyToMessage(object sender, RoutedEventArgs args)
    {
        if (selectedMessage is null) return;
        editMessageId = null;
        replyMessageId = selectedMessage.Id;
        ComposeContext.Visibility = Visibility.Visible;
        ComposeContextText.Text = "Ответ: " + selectedMessage.Text;
        MessageInput.Focus(FocusState.Programmatic);
    }

    private void EditSelectedMessage(object sender, RoutedEventArgs args)
    {
        if (selectedMessage is null) return;
        replyMessageId = null;
        editMessageId = selectedMessage.Id;
        ComposeContext.Visibility = Visibility.Visible;
        ComposeContextText.Text = "Редактирование сообщения";
        MessageInput.Text = selectedMessage.Text;
        MessageInput.Focus(FocusState.Programmatic);
    }

    private void CancelMessageAction(object sender, RoutedEventArgs args)
    {
        replyMessageId = editMessageId = null;
        ComposeContext.Visibility = Visibility.Collapsed;
        ComposeContextText.Text = "";
    }

    private static Dictionary<string, object?> TextContent(string text) => new()
    {
        ["@type"] = "inputMessageText",
        ["text"] = new Dictionary<string, object?> { ["@type"] = "formattedText", ["text"] = text, ["entities"] = Array.Empty<object>() },
        ["link_preview_options"] = null,
        ["clear_draft"] = true
    };

    private async void SendTelegramMessage(object sender, RoutedEventArgs args)
    {
        var text = MessageInput.Text.Trim();
        if (messageSending || text.Length == 0 || telegram is null || activeChat is null) return;
        messageSending = true;
        SendMessageButton.IsEnabled = false;
        var chatId = activeChat.Id;
        try
        {
            JsonElement result;
            if (editMessageId is long editId)
                result = await telegram.RequestAsync(new { @type = "editMessageText", chat_id = chatId, message_id = editId, reply_markup = (object?)null, input_message_content = TextContent(text) }, lifetime.Token);
            else
            {
                object? reply = null;
                if (replyMessageId is long replyId)
                {
                    var quoted = selectedMessage?.Id == replyId ? selectedMessage.Text : "";
                    reply = new Dictionary<string, object?>
                    {
                        ["@type"] = "inputMessageReplyToMessage", ["message_id"] = replyId,
                        ["quote"] = quoted.Length == 0 ? null : new Dictionary<string, object?>
                        {
                            ["@type"] = "inputTextQuote", ["text"] = new Dictionary<string, object?> { ["@type"] = "formattedText", ["text"] = quoted[..Math.Min(quoted.Length, 1000)], ["entities"] = Array.Empty<object>() }, ["position"] = 0
                        },
                        ["checklist_task_id"] = 0, ["poll_option_id"] = ""
                    };
                }
                result = await telegram.RequestAsync(new
                {
                    @type = "sendMessage", chat_id = chatId, topic_id = (object?)null, reply_to = reply,
                    options = (object?)null, reply_markup = (object?)null, input_message_content = TextContent(text)
                }, lifetime.Token);
            }
            if (activeChat?.Id == chatId)
            {
                InsertMessage(result);
                MessageInput.Text = "";
                CancelMessageAction(null!, null!);
                MessengerStatus.Text = "";
            }
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Сообщение не отправлено. " + ex.Message;
        }
        finally { messageSending = false; SendMessageButton.IsEnabled = true; }
    }

    private async void DeleteSelectedMessage(object sender, RoutedEventArgs args)
    {
        if (selectedMessage is null || activeChat is null || telegram is null) return;
        var chatId = activeChat.Id;
        var messageId = selectedMessage.Id;
        try
        {
            var properties = await telegram.RequestAsync(new { @type = "getMessageProperties", chat_id = chatId, message_id = messageId }, lifetime.Token);
            var revoke = properties.Field("can_be_deleted_for_all_users").Flag();
            if (!revoke && !properties.Field("can_be_deleted_only_for_self").Flag()) return;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Удалить сообщение?",
                Content = revoke ? "Сообщение будет удалено у всех участников." : "Сообщение будет удалено только у тебя.",
                PrimaryButtonText = "Удалить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await telegram.RequestAsync(new { @type = "deleteMessages", chat_id = chatId, message_ids = new[] { messageId }, revoke }, lifetime.Token);
            if (histories.TryGetValue(chatId, out var history)) history.Messages.Remove(messageId);
            if (activeChat?.Id == chatId) RenderHistory(chatId);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException)
        {
            MessengerStatus.Text = "Не удалось удалить сообщение. " + ex.Message;
        }
    }

    private void ShowIncomingNotifications(JsonElement update)
    {
        var chatId = update.Field("chat_id").Number();
        if (chatId == 0 || activeChat?.Id == chatId) return;
        var title = knownChats.TryGetValue(chatId, out var chat) ? chat.Title : "Telegram";
        foreach (var notification in update.Field("added_notifications").Items())
        {
            if (notification.Field("type").Field("@type").Text() != "notificationTypeNewMessage") continue;
            if (DateTime.UtcNow - notification.Field("date").LocalDate().ToUniversalTime() > TimeSpan.FromMinutes(2)) continue;
            var preview = notification.Field("type").Field("show_preview").Flag()
                ? TdJson.MessageText(notification.Field("type").Field("message")) : "Новое сообщение";
            try
            {
                var toast = new AppNotificationBuilder().AddArgument("chatId", chatId.ToString()).AddText(title).AddText(preview).BuildNotification();
                AppNotificationManager.Default.Show(toast);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
            {
                MessengerStatus.Text = "Уведомления Windows недоступны. " + ex.Message;
            }
        }
    }
}
