using System.Text.Json;
using System.Runtime.InteropServices;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Glassgram;

public partial class MainWindow
{
    private void HandleMessengerUpdate(JsonElement update)
    {
        var kind = update.Field("@type").Text();
        var chatId = update.Field("chat_id").Number();
        if (!messengerStarted)
        {
            if (kind is "updateNewChat" or "updateChatPosition" or "updateChatLastMessage" or "updateChatDraftMessage")
            {
                pendingChatUpdates.Add(update.Clone());
                LogChatOrder($"queued-before-start type={kind} chat_id={chatId}");
            }
            return;
        }
        switch (kind)
        {
            case "updateNewChat":
                LogPositions(update.Field("chat").Field("id").Number(), kind, update.Field("chat").Field("positions"));
                UpsertChat(update.Field("chat"));
                break;
            case "updateChatTitle":
            case "updateChatPhoto":
            case "updateChatReadInbox":
            case "updateChatReadOutbox":
            case "updateChatIsMarkedAsUnread":
            case "updateChatUnreadMentionCount":
            case "updateChatUnreadReactionCount":
            case "updateChatNotificationSettings":
            case "updateChatAvailableReactions":
            case "updateChatPermissions":
                _ = RefreshChatAsync(chatId);
                break;
            case "updateChatLastMessage":
            case "updateChatDraftMessage":
                LogPositions(chatId, kind, update.Field("positions"));
                if (knownChats.TryGetValue(chatId, out var moved))
                {
                    moved.ApplyPositions(update.Field("positions"));
                    RefreshChatList();
                }
                _ = RefreshChatAsync(chatId);
                break;
            case "updateChatPosition":
                LogPositions(chatId, kind, JsonSerializer.SerializeToElement(new[] { update.Field("position") }));
                if (knownChats.TryGetValue(chatId, out var positioned))
                {
                    var position = update.Field("position");
                    var order = position.Field("order").Number();
                    positioned.ApplyList(ChatRow.ListKey(position.Field("list")), order != 0, order, position.Field("is_pinned").Flag());
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
            case "updateMessageInteractionInfo":
            case "updateMessageReactions":
                ApplyMessageInteractionUpdate(update);
                break;
            case "updateMessageIsPinned":
                if (histories.TryGetValue(chatId, out var pinnedHistory) && pinnedHistory.Messages.TryGetValue(update.Field("message_id").Number(), out var pinnedRow))
                    pinnedRow.SetPinned(update.Field("is_pinned").Flag());
                break;
            case "updateDeleteMessages":
                DeleteCachedMessages(update);
                break;
            case "updateFile":
                UpdateAvatar(update.Field("file"));
                NotifyMediaFileUpdate(update.Field("file"));
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
        var expectedRevision = knownChats.TryGetValue(chatId, out var existing) ? existing.PositionRevision : -1;
        try
        {
            var chat = await telegram.RequestAsync(new { @type = "getChat", chat_id = chatId }, lifetime.Token);
            if (messengerStarted) UpsertChat(chat, saved: existing?.IsSaved ?? false, expectedPositionRevision: expectedRevision);
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
        historyRestores++;
        RenderHistory(row.ChatId);
        if (nearBottom) _ = FinishMessageRenderAsync(row.ChatId);
        else if (!DispatcherQueue.TryEnqueue(() => { messageScroller?.ChangeView(null, offset, null, true); historyRestores--; })) historyRestores--;
        if (nearBottom) _ = MarkReadAsync();
    }

    private async Task FinishMessageRenderAsync(long chatId)
    {
        try { await ScrollToLatestAsync(chatId); }
        catch (OperationCanceledException) { }
        finally { historyRestores--; }
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
        _ = SaveHistoryCacheAsync(chatId, history);
    }

    private void UpdateAvatar(JsonElement file)
    {
        var id = (int)file.Field("id").Number();
        if (avatarFiles.TryGetValue(id, out var chatId) && knownChats.TryGetValue(chatId, out var chat))
            chat.SetAvatar(file.Field("local").Field("path").Text());
        if (mediaFiles.TryGetValue(id, out var messages))
            foreach (var message in messages.ToArray()) message.ApplyFile(file);
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
        if (!suppressDraftSave && activeChat is not null)
        {
            if (MessageInput.Text.Length == 0) preferences.Drafts.Remove(activeChat.Id); else preferences.Drafts[activeChat.Id] = MessageInput.Text;
        }
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
        var selected = SelectedMessages();
        selectedMessage = selected.Count == 1 ? selected[0] : null;
        MessageActions.Visibility = selected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EditButton.IsEnabled = DeleteButton.IsEnabled = ReplyButton.IsEnabled = false;
        ForwardButton.IsEnabled = selected.Count > 0;
        if (selected.Count > 1) { DeleteButton.IsEnabled = true; return; }
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

    private void ContextReply(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.IsDate) return;
        selectedMessage = row;
        MessageList.SelectedItem = row;
        ReplyToMessage(sender, args);
    }

    private void MessageContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item) return;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        item.Padding = new Thickness(0);
        item.RightTapped -= ShowMessageContextMenu;
        item.RightTapped += ShowMessageContextMenu;
    }

    private async void ShowMessageContextMenu(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not ListViewItem target || target.Content is not MessageRow row || row.IsDate) return;
        JsonElement properties;
        try { properties = await telegram!.RequestAsync(new { @type = "getMessageProperties", chat_id = row.ChatId, message_id = row.Id }, lifetime.Token); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Не удалось получить доступные действия. " + ex.Message; return; }
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Копировать текст", DataContext = row };
        var copyLink = new MenuFlyoutItem { Text = "Копировать ссылку", DataContext = row };
        var forward = new MenuFlyoutItem { Text = "Переслать", DataContext = row };
        var select = new MenuFlyoutItem { Text = "Выделить", DataContext = row };
        var reply = new MenuFlyoutItem { Text = "Ответить", DataContext = row };
        var edit = new MenuFlyoutItem { Text = "Изменить", DataContext = row };
        var favorite = new MenuFlyoutItem { Text = "В избранное", DataContext = row };
        var pin = new MenuFlyoutItem { Text = row.IsPinned ? "Открепить" : "Закрепить", DataContext = row };
        var reactions = new MenuFlyoutItem { Text = "Добавить реакцию", DataContext = row };
        var reactionAuthors = new MenuFlyoutItem { Text = "Кто отреагировал", DataContext = row };
        var delete = new MenuFlyoutItem { Text = "Удалить", DataContext = row };
        reply.Click += ContextReply;
        edit.Click += ContextEdit;
        delete.Click += ContextDelete;
        copy.Click += CopyMessageText;
        copyLink.Click += CopyMessageLink;
        forward.Click += ContextForward;
        select.Click += ContextSelect;
        favorite.Click += SaveMessageToFavorites;
        pin.Click += TogglePinnedMessage;
        reactions.Click += ShowReactionPicker;
        reactionAuthors.Click += ShowReactionAuthors;
        if (properties.Field("can_be_replied").Flag()) menu.Items.Add(reply);
        if (row.Text.Length > 0) menu.Items.Add(copy);
        if (properties.Field("can_be_forwarded").Flag()) menu.Items.Add(forward);
        if (properties.Field("can_be_edited").Flag()) menu.Items.Add(edit);
        menu.Items.Add(reactions);
        if (row.Reactions.Count > 0) menu.Items.Add(reactionAuthors);
        if (properties.Field("can_be_pinned").Flag()) menu.Items.Add(pin);
        if (savedChatId != 0 && row.ChatId != savedChatId && properties.Field("can_be_forwarded").Flag()) menu.Items.Add(favorite);
        if (properties.Field("can_get_link").Flag()) menu.Items.Add(copyLink);
        if (row.MediaKind.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var open = new MenuFlyoutItem { Text = "Открыть медиа", DataContext = row };
            var save = new MenuFlyoutItem { Text = "Сохранить как…", DataContext = row };
            var copyMedia = new MenuFlyoutItem { Text = "Копировать медиа", DataContext = row };
            open.Click += ContextOpenMedia; save.Click += ContextSaveMedia; copyMedia.Click += ContextCopyMedia;
            menu.Items.Add(open); menu.Items.Add(save); menu.Items.Add(copyMedia);
        }
        if (row.PrimaryLink.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var openLink = new MenuFlyoutItem { Text = "Открыть ссылку", DataContext = row };
            var copyAddress = new MenuFlyoutItem { Text = "Копировать адрес", DataContext = row };
            openLink.Click += ContextOpenLink; copyAddress.Click += ContextCopyAddress;
            menu.Items.Add(openLink); menu.Items.Add(copyAddress);
        }
        menu.Items.Add(select);
        if (properties.Field("can_be_deleted_only_for_self").Flag() || properties.Field("can_be_deleted_for_all_users").Flag()) menu.Items.Add(delete);
        menu.ShowAt(target);
        args.Handled = true;
    }

    private async void TogglePinnedMessage(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null) return;
        try
        {
            await telegram.RequestAsync(row.IsPinned
                ? new { @type = "unpinChatMessage", chat_id = row.ChatId, message_id = row.Id }
                : new { @type = "pinChatMessage", chat_id = row.ChatId, message_id = row.Id, disable_notification = true, only_for_self = false }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Не удалось изменить закрепление. " + ex.Message; }
    }

    private async void SaveMessageToFavorites(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null || savedChatId == 0) return;
        try { await telegram.RequestAsync(new { @type = "forwardMessages", chat_id = savedChatId, topic_id = (object?)null, from_chat_id = row.ChatId, message_ids = new[] { row.Id }, options = (object?)null, send_copy = true, remove_caption = false }, lifetime.Token); MessengerStatus.Text = "Сообщение сохранено в Избранное."; }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Не удалось сохранить сообщение. " + ex.Message; }
    }

    private void ContextOpenMedia(object sender, RoutedEventArgs args) => OpenMessageMedia(sender, new TappedRoutedEventArgs());

    private async void ContextSaveMedia(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PrimaryFileId == 0) return;
        try
        {
            var path = await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            var extension = Path.GetExtension(path); if (extension.Length < 2) extension = ".bin";
            var picker = new FileSavePicker { SuggestedFileName = row.FileName.Length > 0 ? row.FileName : "telegram-media" + extension };
            picker.FileTypeChoices.Add("Файл", new[] { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync(); if (destination is null) return;
            await (await StorageFile.GetFileFromPathAsync(path)).CopyAndReplaceAsync(destination);
            MessengerStatus.Text = "Медиа сохранено.";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException) { MessengerStatus.Text = "Не удалось сохранить медиа. " + ex.Message; }
    }

    private async void ContextCopyMedia(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PrimaryFileId == 0) return;
        try { var file = await StorageFile.GetFileFromPathAsync(await DownloadMessageFileAsync(row, row.PrimaryFileId, true)); var data = new DataPackage(); data.SetStorageItems(new[] { file }); Clipboard.SetContent(data); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException) { MessengerStatus.Text = "Не удалось скопировать медиа. " + ex.Message; }
    }

    private async void ContextOpenLink(object sender, RoutedEventArgs args) { if ((sender as FrameworkElement)?.DataContext is MessageRow row && Uri.TryCreate(row.PrimaryLink, UriKind.Absolute, out var uri)) await Launcher.LaunchUriAsync(uri); }
    private void ContextCopyAddress(object sender, RoutedEventArgs args) { if ((sender as FrameworkElement)?.DataContext is not MessageRow row) return; var data = new DataPackage(); data.SetText(row.PrimaryLink); Clipboard.SetContent(data); }

    private async void ContextEdit(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.IsDate || telegram is null || activeChat is null) return;
        try
        {
            var properties = await telegram.RequestAsync(new { @type = "getMessageProperties", chat_id = activeChat.Id, message_id = row.Id }, lifetime.Token);
            if (!properties.Field("can_be_edited").Flag()) return;
            selectedMessage = row;
            MessageList.SelectedItem = row;
            EditSelectedMessage(sender, args);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void ContextDelete(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.IsDate) return;
        selectedMessage = row;
        MessageList.SelectedItem = row;
        DeleteSelectedMessage(sender, args);
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
                preferences.Drafts.Remove(chatId);
                SavePreferences();
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
        var selected = SelectedMessages();
        if (selected.Count == 0 || activeChat is null || telegram is null) return;
        var chatId = activeChat.Id;
        try
        {
            var ids = selected.Select(message => message.Id).ToArray();
            var revoke = true;
            foreach (var id in ids)
            {
                var properties = await telegram.RequestAsync(new { @type = "getMessageProperties", chat_id = chatId, message_id = id }, lifetime.Token);
                if (!properties.Field("can_be_deleted_for_all_users").Flag()) revoke = false;
                if (!properties.Field("can_be_deleted_for_all_users").Flag() && !properties.Field("can_be_deleted_only_for_self").Flag()) return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = ids.Length == 1 ? "Удалить сообщение?" : $"Удалить сообщения ({ids.Length})?",
                Content = revoke ? "Выбранные сообщения будут удалены у всех участников." : "Выбранные сообщения будут удалены только у тебя.",
                PrimaryButtonText = "Удалить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await telegram.RequestAsync(new { @type = "deleteMessages", chat_id = chatId, message_ids = ids, revoke }, lifetime.Token);
            if (histories.TryGetValue(chatId, out var history)) foreach (var id in ids) history.Messages.Remove(id);
            if (histories.TryGetValue(chatId, out history)) _ = SaveHistoryCacheAsync(chatId, history);
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
