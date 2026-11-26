// Folder synchronization and list switching adapted from Unigram (GPL-3.0),
// commit 9354bcb753be3b16086bd3886b8adec062ccdd91. See THIRD_PARTY_NOTICES.md.
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<ChatFolderRow> chatFolders = [];
    private int activeFolderId;
    private int folderSwitchVersion;
    private int mainChatListPosition;

    private void InitializeChatFolders()
    {
        if (FolderList.ItemsSource is null) FolderList.ItemsSource = chatFolders;
        if (chatFolders.Count == 0)
        {
            activeFolderId = preferences.ActiveChatFolderId;
            chatFolders.Add(new ChatFolderRow { Id = 0, Title = "Все чаты", Icon = "\uE8A9" });
        }
        var selected = chatFolders.FirstOrDefault(folder => folder.Id == activeFolderId);
        if (selected is not null) FolderList.SelectedItem = selected;
        RefreshFolderRail();
    }

    private async Task ApplyChatFoldersAsync(JsonElement update)
    {
        InitializeChatFolders();
        mainChatListPosition = (int)update.Field("main_chat_list_position").Number();
        var incoming = new List<ChatFolderRow> { chatFolders.First(folder => folder.Id == 0) };
        foreach (var info in update.Field("chat_folders").Items())
        {
            var id = (int)info.Field("id").Number();
            if (id <= 0) continue;
            var existing = chatFolders.FirstOrDefault(folder => folder.Id == id);
            var title = ChatFolderRow.ReadTitle(info);
            var iconName = info.Field("icon").Field("name").Text();
            existing ??= new ChatFolderRow { Id = id };
            existing.Title = string.IsNullOrWhiteSpace(title) ? "Папка" : title;
            existing.Icon = ChatFolderRow.IconGlyph(iconName);
            incoming.Add(existing);
        }
        for (var index = 0; index < incoming.Count; index++)
        {
            var item = incoming[index];
            var current = chatFolders.IndexOf(item);
            if (current < 0) chatFolders.Insert(index, item);
            else if (current != index) chatFolders.Move(current, index);
        }
        for (var index = chatFolders.Count - 1; index >= 0; index--)
            if (!incoming.Contains(chatFolders[index])) chatFolders.RemoveAt(index);
        if (!chatFolders.Any(folder => folder.Id == activeFolderId))
        {
            activeFolderId = 0;
            preferences.ActiveChatFolderId = 0;
            SavePreferences();
        }
        FolderList.SelectedItem = chatFolders.First(folder => folder.Id == activeFolderId);
        RefreshFolderRail();
        RefreshFolderSettings();
        if (telegram is null) return;
        foreach (var folder in chatFolders.Where(folder => !folder.IsAll))
        {
            try
            {
                folder.Folder = await telegram.RequestAsync(new { @type = "getChatFolder", chat_folder_id = folder.Id }, lifetime.Token);
                _ = LoadFolderChatsAsync(folder.Id, ++folderSwitchVersion, false);
            }
            catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
        }
    }

    private void RefreshFolderRail()
    {
        FolderManagementButton.Visibility = Visibility.Visible;
    }

    private async void SelectFolder(object sender, SelectionChangedEventArgs args)
    {
        if (FolderList.SelectedItem is not ChatFolderRow folder || folder.Id == activeFolderId && !showingArchive) return;
        activeFolderId = folder.Id;
        preferences.ActiveChatFolderId = folder.Id;
        SavePreferences();
        showingArchive = false;
        var version = ++folderSwitchVersion;
        if (folder.Id != 0) await LoadFolderChatsAsync(folder.Id, version, true);
        if (version == folderSwitchVersion) RefreshChatList();
    }

    private async Task LoadFolderChatsAsync(int folderId, int version, bool refresh)
    {
        if (telegram is null || folderId <= 0) return;
        var chatList = new Dictionary<string, object> { ["@type"] = "chatListFolder", ["chat_folder_id"] = folderId };
        try
        {
            try { await telegram.RequestAsync(new { @type = "loadChats", chat_list = chatList, limit = 100 }, lifetime.Token); }
            catch (TdException ex) when (ex.Code == 404) { }
            var chats = await telegram.RequestAsync(new { @type = "getChats", chat_list = chatList, limit = 100000 }, lifetime.Token);
            foreach (var value in chats.Field("chat_ids").Items())
            {
                if (version != folderSwitchVersion && refresh) return;
                var id = value.Number();
                var chat = await telegram.RequestAsync(new { @type = "getChat", chat_id = id }, lifetime.Token);
                UpsertChat(chat, saved: knownChats.TryGetValue(id, out var row) && row.IsSaved, sourceList: $"chatListFolder:{folderId}");
            }
            if (refresh && version == folderSwitchVersion) RefreshChatList();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private void ApplyFolderUnread(JsonElement update)
    {
        var list = ChatRow.ListKey(update.Field("chat_list"));
        var folder = chatFolders.FirstOrDefault(item => item.ListKey == list);
        folder?.SetUnread((int)update.Field("unread_count").Number());
    }

    private void OpenFolderSettings(object sender, RoutedEventArgs args)
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        FolderSettingsPanel.Visibility = Visibility.Visible;
        RefreshFolderSettings();
    }

    private void RefreshFolderSettings()
    {
        if (FolderSettingsList is null) return;
        FolderSettingsList.ItemsSource = chatFolders.Where(folder => !folder.IsAll).ToArray();
        FolderSettingsEmpty.Visibility = chatFolders.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CreateFolder(object sender, RoutedEventArgs args) => await EditFolderDialogAsync(null);

    private async void EditFolder(object sender, RoutedEventArgs args)
    {
        if (FolderSettingsList.SelectedItem is ChatFolderRow folder) await EditFolderDialogAsync(folder);
    }

    private async Task EditFolderDialogAsync(ChatFolderRow? folder)
    {
        if (telegram is null) return;
        var source = folder?.Folder.ValueKind == JsonValueKind.Object ? JsonNode.Parse(folder.Folder.GetRawText())!.AsObject() : NewFolder();
        var title = new TextBox { Header = "Название", Text = ReadFolderName(source) ?? folder?.Title ?? "" };
        var icon = new ComboBox { Header = "Иконка", MinWidth = 220 };
        foreach (var name in new[] { "Custom", "All", "Unread", "Unmuted", "Private", "Groups", "Channels", "Bots" }) icon.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        icon.SelectedIndex = 0;
        var options = new (string Key, string Label)[] { ("include_contacts", "Контакты"), ("include_non_contacts", "Не-контакты"), ("include_groups", "Группы"), ("include_channels", "Каналы"), ("include_bots", "Боты"), ("exclude_muted", "Исключить отключённые"), ("exclude_read", "Исключить прочитанные"), ("exclude_archived", "Исключить архивированные") };
        var toggles = options.ToDictionary(option => option.Key, option => new CheckBox { Content = option.Label, IsChecked = source[option.Key]?.GetValue<bool>() ?? false });
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title); panel.Children.Add(icon);
        foreach (var toggle in toggles.Values) panel.Children.Add(toggle);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = folder is null ? "Новая папка" : "Изменить папку", Content = panel, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text)) return;
        source["@type"] = "chatFolder";
        source.Remove("title");
        source["name"] = FolderName(title.Text.Trim());
        source["icon"] = new JsonObject { ["@type"] = "chatFolderIcon", ["name"] = ((ComboBoxItem)icon.SelectedItem).Tag?.ToString() ?? "Custom" };
        foreach (var option in options) source[option.Key] = toggles[option.Key].IsChecked == true;
        source["included_chat_ids"] ??= new JsonArray(); source["excluded_chat_ids"] ??= new JsonArray();
        try
        {
            if (folder is null) await telegram.RequestAsync(new { @type = "createChatFolder", folder = source }, lifetime.Token);
            else await telegram.RequestAsync(new { @type = "editChatFolder", chat_folder_id = folder.Id, folder = source }, lifetime.Token);
        }
        catch (TdException ex) { SettingsStatus.Text = "Не удалось сохранить папку. " + ex.Message; }
    }

    private static JsonObject NewFolder() => new()
    {
        ["@type"] = "chatFolder", ["name"] = FolderName("Новая"), ["icon"] = new JsonObject { ["@type"] = "chatFolderIcon", ["name"] = "Custom" }, ["color_id"] = -1,
        ["is_shareable"] = false, ["pinned_chat_ids"] = new JsonArray(), ["included_chat_ids"] = new JsonArray(), ["excluded_chat_ids"] = new JsonArray(), ["exclude_muted"] = false,
        ["exclude_read"] = false, ["exclude_archived"] = false, ["include_contacts"] = false, ["include_non_contacts"] = false,
        ["include_bots"] = false, ["include_groups"] = false, ["include_channels"] = false
    };

    private static JsonObject FolderName(string text) => new()
    {
        ["@type"] = "chatFolderName",
        ["text"] = new JsonObject { ["@type"] = "formattedText", ["text"] = text, ["entities"] = new JsonArray() },
        ["animate_custom_emoji"] = false
    };

    private static string? ReadFolderName(JsonObject source)
    {
        try { return source["name"]?["text"]?["text"]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private async void DeleteFolder(object sender, RoutedEventArgs args)
    {
        if (telegram is null || FolderSettingsList.SelectedItem is not ChatFolderRow folder) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Удалить папку?", Content = "Переписки останутся в Telegram. Будет удалена только папка.", PrimaryButtonText = "Удалить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await telegram.RequestAsync(new { @type = "deleteChatFolder", chat_folder_id = folder.Id, leave_chat_ids = Array.Empty<long>() }, lifetime.Token); }
        catch (TdException ex) { SettingsStatus.Text = "Не удалось удалить папку. " + ex.Message; }
    }

    private async void MoveFolderUp(object sender, RoutedEventArgs args) => await MoveFolderAsync(-1);
    private async void MoveFolderDown(object sender, RoutedEventArgs args) => await MoveFolderAsync(1);

    private async Task MoveFolderAsync(int offset)
    {
        if (telegram is null || FolderSettingsList.SelectedItem is not ChatFolderRow selected) return;
        var ids = chatFolders.Where(folder => !folder.IsAll).Select(folder => folder.Id).ToList();
        var index = ids.IndexOf(selected.Id); var target = index + offset;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        try { await telegram.RequestAsync(new { @type = "reorderChatFolders", chat_folder_ids = ids, main_chat_list_position = mainChatListPosition }, lifetime.Token); }
        catch (TdException ex) { SettingsStatus.Text = "Не удалось изменить порядок. " + ex.Message; }
    }

    private async Task SetChatInFolderAsync(ChatRow chat, ChatFolderRow folder, bool include)
    {
        if (telegram is null || folder.Folder.ValueKind != JsonValueKind.Object) return;
        var json = JsonNode.Parse(folder.Folder.GetRawText())!.AsObject();
        var included = json["included_chat_ids"]?.AsArray() ?? new JsonArray();
        var excluded = json["excluded_chat_ids"]?.AsArray() ?? new JsonArray();
        RemoveChatId(included, chat.Id); RemoveChatId(excluded, chat.Id);
        if (include) included.Add(chat.Id); else excluded.Add(chat.Id);
        json["included_chat_ids"] = included; json["excluded_chat_ids"] = excluded;
        try { await telegram.RequestAsync(new { @type = "editChatFolder", chat_folder_id = folder.Id, folder = json }, lifetime.Token); }
        catch (TdException ex) { MessengerStatus.Text = "Не удалось изменить папку. " + ex.Message; }
    }

    private static void RemoveChatId(JsonArray array, long id)
    {
        for (var index = array.Count - 1; index >= 0; index--)
            if (array[index]?.GetValue<long>() == id) array.RemoveAt(index);
    }
}
