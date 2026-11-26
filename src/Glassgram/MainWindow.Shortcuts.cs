using Glassgram.Telegram;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Glassgram;

public partial class MainWindow
{
    private const VirtualKey CommaKey = (VirtualKey)188;
    private static bool IsPressed(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    private static bool IsTextEditor(object? source) => source is TextBox or PasswordBox or RichEditBox;
    private List<MessageRow> SelectedMessages() => MessageList.SelectedItems.OfType<MessageRow>().Where(m => !m.IsDate).ToList();

    private void RootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsPressed(VirtualKey.Control);
        var alt = IsPressed(VirtualKey.Menu);
        if (e.Key == VirtualKey.Escape)
        {
            HandleSearchEscape(sender, e);
            if (e.Handled) return;
            CancelCurrentAction();
            e.Handled = true;
            return;
        }
        if (IsTextEditor(e.OriginalSource) && !(ctrl && (e.Key is VirtualKey.F or VirtualKey.K or VirtualKey.Tab || e.Key == CommaKey))) return;
        if (ctrl && e.Key == VirtualKey.F) { ShowMessageSearch(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.K) { Navigation.SelectedItem = Navigation.MenuItems[0]; ChatSearch.Focus(FocusState.Programmatic); ChatSearch.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.N) { _ = StartNewDialogAsync(); e.Handled = true; }
        else if (ctrl && e.Key == CommaKey) { Navigation.SelectedItem = Navigation.SettingsItem; e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.Tab) { showingArchive = !showingArchive; RefreshChatList(); e.Handled = true; }
        else if (alt && e.Key is VirtualKey.Up or VirtualKey.Down) { SwitchChat(e.Key == VirtualKey.Down ? 1 : -1); e.Handled = true; }
        else if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown && messageScroller is not null) { messageScroller.ChangeView(null, messageScroller.VerticalOffset + (e.Key == VirtualKey.PageDown ? 1 : -1) * messageScroller.ViewportHeight * .85, null); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.W && preferences.MinimizeToTray && AppWindow.Presenter is OverlappedPresenter presenter) { presenter.Minimize(); e.Handled = true; }
    }

    private void HandleComposerShortcut(KeyRoutedEventArgs e)
    {
        var ctrl = IsPressed(VirtualKey.Control);
        var shift = IsPressed(VirtualKey.Shift);
        if (e.Key == VirtualKey.Up && MessageInput.Text.Length == 0) { EditLastOutgoingMessage(); e.Handled = true; return; }
        if (e.Key != VirtualKey.Enter || shift) return;
        var send = preferences.SendMode == "CtrlEnter" ? ctrl : true;
        if (!send) return;
        e.Handled = true;
        if (!messageSending) SendTelegramMessage(MessageInput, new RoutedEventArgs());
    }

    private void EditLastOutgoingMessage()
    {
        if (activeChat is null || !histories.TryGetValue(activeChat.Id, out var history)) return;
        selectedMessage = history.Messages.Values.Where(m => m.Outgoing && !m.IsDate).OrderByDescending(m => m.Id).FirstOrDefault();
        if (selectedMessage is null) return;
        MessageList.SelectedItem = selectedMessage;
        EditSelectedMessage(MessageInput, new RoutedEventArgs());
    }

    private void CancelCurrentAction()
    {
        if (FutureSidePanel.Visibility == Visibility.Visible) { CloseSidePanel(this, new RoutedEventArgs()); return; }
        if (MessageSearch.Visibility == Visibility.Visible) { MessageSearch.Text = ""; MessageSearch.Visibility = ChatSearchTools.Visibility = Visibility.Collapsed; ResetChatSearch(true); return; }
        if (ComposeContext.Visibility == Visibility.Visible) { CancelMessageAction(this, new RoutedEventArgs()); return; }
        MessageList.SelectedItems.Clear();
    }

    private void ShowMessageSearch() { if (activeChat is null) return; MessageSearch.Visibility = ChatSearchTools.Visibility = Visibility.Visible; MessageSearch.Focus(FocusState.Programmatic); MessageSearch.SelectAll(); }
    private void SwitchChat(int offset) { if (visibleChats.Count == 0) return; var index = Math.Max(0, ChatList.SelectedIndex); ChatList.SelectedIndex = (index + offset + visibleChats.Count) % visibleChats.Count; ChatList.ScrollIntoView(ChatList.SelectedItem); }
    private void SaveCurrentDraft() { if (activeChat is null) return; if (MessageInput.Text.Length == 0) preferences.Drafts.Remove(activeChat.Id); else preferences.Drafts[activeChat.Id] = MessageInput.Text; SavePreferences(); }

    private void CopyMessageText(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is not MessageRow row) return; var data = new DataPackage(); data.SetText(row.Text); Clipboard.SetContent(data); }
    private async void CopyMessageLink(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null) return; try { var result = await telegram.RequestAsync(new { @type = "getMessageLink", chat_id = row.ChatId, message_id = row.Id, media_timestamp = 0, for_album = false, in_message_thread = false }, lifetime.Token); var link = result.Field("link").Text(); if (link.Length > 0) { var data = new DataPackage(); data.SetText(link); Clipboard.SetContent(data); } } catch (TdException ex) { MessengerStatus.Text = "Ссылка недоступна. " + ex.Message; } }
    private void ContextSelect(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is MessageRow row) MessageList.SelectedItems.Add(row); }
    private void ContextForward(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is not MessageRow row) return; MessageList.SelectedItems.Clear(); MessageList.SelectedItems.Add(row); ForwardSelectedMessages(sender, e); }
    private async void ForwardSelectedMessages(object sender, RoutedEventArgs e)
    {
        var messages = SelectedMessages(); if (messages.Count == 0 || telegram is null) return;
        var picker = new ComboBox { ItemsSource = visibleChats.Where(c => c.Id != activeChat?.Id).ToArray(), DisplayMemberPath = nameof(ChatRow.Title), MinWidth = 320 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Переслать в чат", Content = picker, PrimaryButtonText = "Переслать", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedItem is not ChatRow target) return;
        try { await telegram.RequestAsync(new { @type = "forwardMessages", chat_id = target.Id, topic_id = (object?)null, from_chat_id = messages[0].ChatId, message_ids = messages.Select(m => m.Id).ToArray(), options = (object?)null, send_copy = false, remove_caption = false }, lifetime.Token); }
        catch (TdException ex) { MessengerStatus.Text = "Не удалось переслать. " + ex.Message; }
    }

    private async Task StartNewDialogAsync()
    {
        if (telegram is null) return;
        var input = new TextBox { PlaceholderText = "@username", MinWidth = 320 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Новый диалог", Content = input, PrimaryButtonText = "Открыть", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || input.Text.Trim().Length == 0) return;
        try { var chat = await telegram.RequestAsync(new { @type = "searchPublicChat", username = input.Text.Trim().TrimStart('@') }, lifetime.Token); UpsertChat(chat); await OpenChatAsync(chat.Field("id").Number()); }
        catch (TdException ex) { MessengerStatus.Text = "Чат не найден. " + ex.Message; }
    }
}
