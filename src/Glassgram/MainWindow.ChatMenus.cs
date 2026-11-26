using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glassgram;

public partial class MainWindow
{
    private void ChatContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item) return;
        item.RightTapped -= ShowChatContextMenu;
        item.RightTapped += ShowChatContextMenu;
    }

    private void ShowChatContextMenu(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not ListViewItem target || target.Content is not ChatRow chat || telegram is null) return;
        var menu = new MenuFlyout();
        AddChatMenuItem(menu, chat.IsPinned ? "Открепить" : "Закрепить", chat, ToggleChatPinned);
        AddChatMenuItem(menu, chat.UnreadCount > 0 || chat.IsMarkedUnread ? "Отметить прочитанным" : "Отметить непрочитанным", chat, ToggleChatUnread);
        AddChatMenuItem(menu, chat.IsMuted ? "Включить уведомления" : "Отключить уведомления", chat, ToggleChatMuted);
        AddChatMenuItem(menu, chat.IsArchive ? "Вернуть из архива" : "Архивировать", chat, ToggleChatArchived);
        if (chatFolders.Count > 1)
        {
            var folders = new MenuFlyoutSubItem { Text = "Папки" };
            foreach (var folder in chatFolders.Where(folder => !folder.IsAll))
            {
                var included = ChatFolderList.Contains(chat, folder.Id);
                var item = new MenuFlyoutItem { Text = included ? $"Удалить из «{folder.Title}»" : $"Добавить в «{folder.Title}»", DataContext = (chat, folder, !included) };
                item.Click += ChangeChatFolder;
                folders.Items.Add(item);
            }
            menu.Items.Add(folders);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        AddChatMenuItem(menu, "Открыть профиль", chat, OpenChatProfile);
        if (!chat.IsSaved) AddChatMenuItem(menu, "Удалить чат", chat, DeleteChat, true);
        menu.ShowAt(target);
        args.Handled = true;
    }

    private static void AddChatMenuItem(MenuFlyout menu, string text, ChatRow chat, RoutedEventHandler handler, bool destructive = false)
    {
        var item = new MenuFlyoutItem { Text = text, DataContext = chat };
        if (destructive) item.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 105, 105));
        item.Click += handler;
        menu.Items.Add(item);
    }

    private async void ToggleChatPinned(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        await RunChatAction(chat, new { @type = "toggleChatIsPinned", chat_list = ChatListObject(chat.IsArchive), chat_id = chat.Id, is_pinned = !chat.IsPinned }, "Не удалось изменить закрепление.");
    }

    private async void ToggleChatUnread(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        var markUnread = chat.UnreadCount == 0 && !chat.IsMarkedUnread;
        await RunChatAction(chat, new { @type = "toggleChatIsMarkedAsUnread", chat_id = chat.Id, is_marked_as_unread = markUnread }, "Не удалось изменить отметку прочтения.");
    }

    private async void ToggleChatMuted(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        var settings = new Dictionary<string, object?>
        {
            ["@type"] = "chatNotificationSettings", ["use_default_mute_for"] = false, ["mute_for"] = chat.IsMuted ? 0 : int.MaxValue,
            ["use_default_sound"] = true, ["sound_id"] = 0L, ["use_default_show_preview"] = true, ["show_preview"] = true,
            ["use_default_mute_stories"] = true, ["mute_stories"] = false, ["use_default_story_sound"] = true, ["story_sound_id"] = 0L,
            ["use_default_show_story_sender"] = true, ["show_story_sender"] = true,
            ["use_default_disable_pinned_message_notifications"] = true, ["disable_pinned_message_notifications"] = false,
            ["use_default_disable_mention_notifications"] = true, ["disable_mention_notifications"] = false
        };
        await RunChatAction(chat, new { @type = "setChatNotificationSettings", chat_id = chat.Id, notification_settings = settings }, "Не удалось изменить уведомления.");
    }

    private async void ToggleChatArchived(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        await RunChatAction(chat, new { @type = "addChatToList", chat_id = chat.Id, chat_list = ChatListObject(!chat.IsArchive) }, "Не удалось изменить архив.");
    }

    private async void ChangeChatFolder(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is ValueTuple<ChatRow, ChatFolderRow, bool> data)
            await SetChatInFolderAsync(data.Item1, data.Item2, data.Item3);
    }

    private async void OpenChatProfile(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        var type = chat.Kind switch { "chatTypePrivate" => "Личный чат", "chatTypeBasicGroup" => "Группа", "chatTypeSupergroup" => "Группа или канал", _ => "Telegram-чат" };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = chat.Title, Content = $"{type}\nID: {chat.Id}", CloseButtonText = "Закрыть" };
        await dialog.ShowAsync();
    }

    private async void DeleteChat(object sender, RoutedEventArgs args)
    {
        if (!TryGetChat(sender, out var chat)) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Удалить чат?", Content = $"История «{chat.Title}» будет удалена из списка чатов.", PrimaryButtonText = "Удалить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunChatAction(chat, new { @type = "deleteChatHistory", chat_id = chat.Id, remove_from_chat_list = true, revoke = false }, "Не удалось удалить чат.");
    }

    private bool TryGetChat(object sender, out ChatRow chat)
    {
        chat = (sender as FrameworkElement)?.DataContext as ChatRow ?? null!;
        return chat is not null && telegram is not null;
    }

    private static Dictionary<string, string> ChatListObject(bool archive) => new() { ["@type"] = archive ? "chatListArchive" : "chatListMain" };

    private async Task RunChatAction(ChatRow chat, object request, string error)
    {
        try
        {
            await telegram!.RequestAsync(request, lifetime.Token);
            var updated = await telegram.RequestAsync(new { @type = "getChat", chat_id = chat.Id }, lifetime.Token);
            UpsertChat(updated, saved: chat.IsSaved);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) MessengerStatus.Text = error + " " + ex.Message; }
    }
}
