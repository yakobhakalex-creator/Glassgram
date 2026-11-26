using System.Collections.ObjectModel;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<InviteLinkRow> profileInviteLinks = [];
    private readonly ObservableCollection<JoinRequestRow> profileJoinRequests = [];
    private GroupAdminCapabilities groupCapabilities;
    private JsonElement groupFullInfo;
    private JsonElement groupChatState;
    private long inviteCreatorUserId;
    private int inviteLoadGeneration;
    private int inviteOffsetDate;
    private string inviteOffsetLink = "";
    private JsonElement joinRequestOffset;
    private int joinRequestGeneration;

    private async Task PrepareGroupAdministrationAsync(ChatRow chat, JsonElement fullInfo)
    {
        GroupAdministrationSection.Visibility = Visibility.Visible;
        groupFullInfo = fullInfo.Clone();
        try { groupChatState = (await telegram!.RequestAsync(new { @type = "getChat", chat_id = chat.Id }, lifetime.Token)).Clone(); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { groupChatState = default; }
        groupCapabilities = ChatMemberPolicy.GetGroupCapabilities(currentProfileMember.Field("status"));
        GroupEditInfoButton.Visibility = groupCapabilities.CanChangeInfo ? Visibility.Visible : Visibility.Collapsed;
        GroupPhotoButton.Visibility = groupCapabilities.CanChangeInfo ? Visibility.Visible : Visibility.Collapsed;
        GroupPermissionsButton.Visibility = groupCapabilities.CanRestrictMembers ? Visibility.Visible : Visibility.Collapsed;
        GroupSlowModeButton.Visibility = groupCapabilities.CanRestrictMembers && chat.SupergroupId != 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupProtectedButton.Visibility = groupCapabilities.IsOwner ? Visibility.Visible : Visibility.Collapsed;
        GroupReactionsButton.Visibility = groupCapabilities.CanChangeInfo ? Visibility.Visible : Visibility.Collapsed;
        GroupUsernameButton.Visibility = groupCapabilities.CanChangeInfo && chat.SupergroupId != 0 && fullInfo.Field("can_set_username").Flag() ? Visibility.Visible : Visibility.Collapsed;
        GroupJoinButton.Visibility = groupCapabilities.IsMember ? Visibility.Collapsed : Visibility.Visible;
        GroupLeaveButton.Visibility = groupCapabilities.IsMember ? Visibility.Visible : Visibility.Collapsed;
        GroupInviteLinksSection.Visibility = groupCapabilities.CanInvite ? Visibility.Visible : Visibility.Collapsed;
        GroupJoinRequestsSection.Visibility = groupCapabilities.CanInvite ? Visibility.Visible : Visibility.Collapsed;
        ProfileInviteLinks.ItemsSource ??= profileInviteLinks;
        ProfileJoinRequests.ItemsSource ??= profileJoinRequests;
        if (!groupCapabilities.CanInvite) return;
        try
        {
            var me = await telegram!.RequestAsync(new { @type = "getMe" }, lifetime.Token);
            inviteCreatorUserId = me.Field("id").Number();
            await Task.WhenAll(LoadInviteLinksAsync(true), LoadJoinRequestsAsync(true));
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            GroupAdministrationStatus.Text = "Ошибка Telegram: " + ex.Message;
        }
    }

    private async void EditGroupInfoClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var title = new TextBox { Header = "Название", Text = chat.Title, MaxLength = 128 };
        var description = new TextBox { Header = "Описание", Text = ProfileDescription.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 255 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Название и описание", PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close, Content = new StackPanel { Spacing = 8, Children = { title, description } } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmGroupActionAsync("Сохранить изменения группы?")) return;
        await ExecuteGroupActionAsync(async () =>
        {
            if (title.Text.Trim() != chat.Title) await telegram!.RequestAsync(new { @type = "setChatTitle", chat_id = chat.Id, title = title.Text.Trim() }, lifetime.Token);
            if (description.Text.Trim() != ProfileDescription.Text) await telegram!.RequestAsync(new { @type = "setChatDescription", chat_id = chat.Id, description = description.Text.Trim() }, lifetime.Token);
        });
    }

    private async void ChangeGroupPhotoClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null || !await ConfirmGroupActionAsync("Установить выбранный аватар?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new
        {
            @type = "setChatPhoto", chat_id = chat.Id,
            photo = new { @type = "inputChatPhotoStatic", photo = new { @type = "inputFileLocal", path = file.Path } }
        }, lifetime.Token));
    }

    private async void EditGroupUsernameClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { SupergroupId: not 0 } chat) return;
        var input = new TextBox { Header = "Публичный username (пусто — частная группа)", Text = ProfileUsername.Text.TrimStart('@'), MaxLength = 32 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Тип и username", Content = input, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmGroupActionAsync("Изменить публичный адрес группы?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "setSupergroupUsername", supergroup_id = chat.SupergroupId, username = input.Text.Trim().TrimStart('@') }, lifetime.Token));
    }

    private async void EditGroupPermissionsClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var current = groupChatState.Field("permissions");
        var messages = new CheckBox { Content = "Отправка сообщений", IsChecked = current.Field("can_send_basic_messages").Flag() };
        var media = new CheckBox { Content = "Отправка медиа", IsChecked = current.Field("can_send_photos").Flag() };
        var invite = new CheckBox { Content = "Приглашение участников", IsChecked = current.Field("can_invite_users").Flag() };
        var pin = new CheckBox { Content = "Закрепление сообщений", IsChecked = current.Field("can_pin_messages").Flag() };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Разрешения участников", PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close, Content = new StackPanel { Spacing = 6, Children = { messages, media, invite, pin } } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmGroupActionAsync("Изменить разрешения участников?")) return;
        var permissions = BuildChatPermissions(messages.IsChecked == true, media.IsChecked == true, invite.IsChecked == true, pin.IsChecked == true);
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "setChatPermissions", chat_id = chat.Id, permissions }, lifetime.Token));
    }

    internal static Dictionary<string, object?> BuildChatPermissions(bool messages, bool media, bool invite, bool pin) => new()
    {
        ["@type"] = "chatPermissions", ["can_send_basic_messages"] = messages, ["can_send_audios"] = media,
        ["can_send_documents"] = media, ["can_send_photos"] = media, ["can_send_videos"] = media,
        ["can_send_video_notes"] = media, ["can_send_voice_notes"] = media, ["can_send_polls"] = messages,
        ["can_send_other_messages"] = media, ["can_add_link_previews"] = messages, ["can_change_info"] = false,
        ["can_invite_users"] = invite, ["can_pin_messages"] = pin, ["can_create_topics"] = false
    };

    private async void EditSlowModeClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var choice = new ComboBox { Header = "Интервал", ItemsSource = new[] { "Выключен", "10 секунд", "30 секунд", "1 минута", "5 минут", "15 минут", "1 час" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var values = new[] { 0, 10, 30, 60, 300, 900, 3600 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Медленный режим", Content = choice, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmGroupActionAsync("Изменить медленный режим?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "setChatSlowModeDelay", chat_id = chat.Id, slow_mode_delay = values[choice.SelectedIndex] }, lifetime.Token));
    }

    private async void ToggleProtectedContentClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var current = groupChatState.Field("has_protected_content").Flag();
        if (!await ConfirmGroupActionAsync(current ? "Отключить защиту контента?" : "Запретить копирование и пересылку?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "toggleChatHasProtectedContent", chat_id = chat.Id, has_protected_content = !current }, lifetime.Token));
    }

    private async void EditGroupReactionsClick(object sender, RoutedEventArgs args)
    {
        if (activeChat is not { } chat) return;
        var choice = new ComboBox { Header = "Доступные реакции", ItemsSource = new[] { "Все реакции", "Отключить реакции" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Реакции", Content = choice, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmGroupActionAsync("Изменить доступные реакции?")) return;
        object available = choice.SelectedIndex == 0 ? new { @type = "chatAvailableReactionsAll", max_reaction_count = 11 } : new { @type = "chatAvailableReactionsSome", reactions = Array.Empty<object>(), max_reaction_count = 1 };
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "setChatAvailableReactions", chat_id = chat.Id, available_reactions = available }, lifetime.Token));
    }

    private async void JoinGroupClick(object sender, RoutedEventArgs args) => await ChangeGroupMembershipAsync(true);
    private async void LeaveGroupClick(object sender, RoutedEventArgs args) => await ChangeGroupMembershipAsync(false);

    private async Task ChangeGroupMembershipAsync(bool join)
    {
        if (activeChat is not { } chat || !await ConfirmGroupActionAsync(join ? "Вступить в чат?" : "Покинуть чат?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = join ? "joinChat" : "leaveChat", chat_id = chat.Id }, lifetime.Token));
    }

    private async Task LoadInviteLinksAsync(bool reset)
    {
        if (telegram is null || activeChat is not { } chat || inviteCreatorUserId == 0) return;
        var generation = ++inviteLoadGeneration;
        if (reset) { inviteOffsetDate = 0; inviteOffsetLink = ""; }
        try
        {
            var result = await telegram.RequestAsync(new { @type = "getChatInviteLinks", chat_id = chat.Id, creator_user_id = inviteCreatorUserId, is_revoked = false, offset_date = inviteOffsetDate, offset_invite_link = inviteOffsetLink, limit = 50 }, lifetime.Token);
            if (generation != inviteLoadGeneration || activeChat?.Id != chat.Id) return;
            var rows = result.Field("invite_links").Items().Select(InviteLinkRow.From).ToList();
            ApplyStable(profileInviteLinks, rows, x => x.Link, reset);
            if (rows.Count > 0) { inviteOffsetDate = rows[^1].Date; inviteOffsetLink = rows[^1].Link; }
            InviteLinksMoreButton.Visibility = rows.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
            InviteLinksStatus.Text = profileInviteLinks.Count == 0 ? "Ссылок нет" : $"Ссылок: {profileInviteLinks.Count}";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { if (generation == inviteLoadGeneration) InviteLinksStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private async Task LoadJoinRequestsAsync(bool reset)
    {
        if (telegram is null || activeChat is not { } chat) return;
        var generation = ++joinRequestGeneration;
        if (reset) joinRequestOffset = default;
        try
        {
            var result = await telegram.RequestAsync(new { @type = "getChatJoinRequests", chat_id = chat.Id, invite_link = "", query = "", offset_request = joinRequestOffset.ValueKind == JsonValueKind.Undefined ? null : (object)joinRequestOffset, limit = 50 }, lifetime.Token);
            if (generation != joinRequestGeneration || activeChat?.Id != chat.Id) return;
            var requests = result.Field("requests").Items().Select(x => x.Clone()).ToList();
            var rows = new List<JoinRequestRow>();
            foreach (var request in requests)
            {
                var user = await telegram.RequestAsync(new { @type = "getUser", user_id = request.Field("user_id").Number() }, lifetime.Token);
                rows.Add(JoinRequestRow.From(request, user));
            }
            if (generation != joinRequestGeneration) return;
            ApplyStable(profileJoinRequests, rows, x => x.UserId.ToString(), reset);
            if (requests.Count > 0) joinRequestOffset = requests[^1];
            JoinRequestsMoreButton.Visibility = requests.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
            JoinRequestsStatus.Text = profileJoinRequests.Count == 0 ? "Ожидающих заявок нет" : $"Ожидают: {profileJoinRequests.Count}";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { if (generation == joinRequestGeneration) JoinRequestsStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private static void ApplyStable<T>(ObservableCollection<T> target, IReadOnlyList<T> rows, Func<T, string> key, bool reset)
    {
        if (!reset) { foreach (var row in rows.Where(row => target.All(x => key(x) != key(row)))) target.Add(row); return; }
        for (var i = 0; i < rows.Count; i++)
        {
            var existingIndex = -1;
            for (var j = 0; j < target.Count; j++) if (key(target[j]) == key(rows[i])) { existingIndex = j; break; }
            if (existingIndex < 0) target.Insert(i, rows[i]);
            else
            {
                if (existingIndex != i) target.Move(existingIndex, i);
                target[i] = rows[i];
            }
        }
        while (target.Count > rows.Count) target.RemoveAt(target.Count - 1);
    }

    private async void LoadMoreInviteLinks(object sender, RoutedEventArgs args) => await LoadInviteLinksAsync(false);
    private async void LoadMoreJoinRequests(object sender, RoutedEventArgs args) => await LoadJoinRequestsAsync(false);
    private async void CreateInviteLinkClick(object sender, RoutedEventArgs args) => await EditInviteLinkAsync(null);

    private async void OpenInviteLink(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not InviteLinkRow row || activeChat is not { } chat || telegram is null) return;
        var memberCount = row.MemberCount;
        try
        {
            var details = await telegram.RequestAsync(new { @type = "getChatInviteLink", chat_id = chat.Id, invite_link = row.Link }, lifetime.Token);
            memberCount = (int)details.Field("member_count").Number();
            await telegram.RequestAsync(new { @type = "getChatInviteLinkMembers", chat_id = chat.Id, invite_link = row.Link, only_with_expired_subscription = false, offset_member = (object?)null, limit = 50 }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { InviteLinksStatus.Text = "Ошибка Telegram: " + ex.Message; }
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = row.Name, Content = $"Вступили: {memberCount}\nЗаявок: {row.PendingCount}\n{row.Link}", PrimaryButtonText = "Копировать", SecondaryButtonText = row.IsPrimary ? "" : "Изменить", CloseButtonText = "Закрыть" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) { var package = new DataPackage(); package.SetText(row.Link); Clipboard.SetContent(package); }
        else if (result == ContentDialogResult.Secondary) await EditInviteLinkAsync(row);
    }

    private async Task EditInviteLinkAsync(InviteLinkRow? row)
    {
        if (activeChat is not { } chat) return;
        var name = new TextBox { Header = "Название", Text = row?.Name ?? "", MaxLength = 32 };
        var expiration = new ComboBox { Header = "Срок", ItemsSource = new[] { "Без срока", "1 день", "7 дней", "30 дней" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var limit = new NumberBox { Header = "Лимит участников (0 — без лимита)", Minimum = 0, Maximum = 99999, Value = row?.MemberLimit ?? 0 };
        var approval = new CheckBox { Content = "Вступление по заявке", IsChecked = row?.CreatesJoinRequest ?? false };
        var panel = new StackPanel { Spacing = 8, Children = { name, expiration, limit, approval } };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = row is null ? "Новая ссылка" : "Изменить ссылку", Content = panel, PrimaryButtonText = "Сохранить", SecondaryButtonText = row is null ? "" : "Отозвать", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && row is not null)
        {
            if (!await ConfirmGroupActionAsync("Отозвать пригласительную ссылку?")) return;
            await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "revokeChatInviteLink", chat_id = chat.Id, invite_link = row.Link }, lifetime.Token));
        }
        else if (result == ContentDialogResult.Primary)
        {
            if (!await ConfirmGroupActionAsync(row is null ? "Создать пригласительную ссылку?" : "Сохранить изменения ссылки?")) return;
            var days = new[] { 0, 1, 7, 30 }[expiration.SelectedIndex];
            var expirationDate = days == 0 ? 0 : (int)DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
            var memberLimit = approval.IsChecked == true ? 0 : (int)limit.Value;
            if (row is null) await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "createChatInviteLink", chat_id = chat.Id, name = name.Text.Trim(), expiration_date = expirationDate, member_limit = memberLimit, creates_join_request = approval.IsChecked == true }, lifetime.Token));
            else await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "editChatInviteLink", chat_id = chat.Id, invite_link = row.Link, name = name.Text.Trim(), expiration_date = expirationDate, member_limit = memberLimit, creates_join_request = approval.IsChecked == true }, lifetime.Token));
        }
        await LoadInviteLinksAsync(true);
    }

    private void JoinRequestContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not JoinRequestRow row) return;
        var menu = new MenuFlyout();
        foreach (var (text, approve) in new[] { ("Одобрить", true), ("Отклонить", false) })
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await ProcessJoinRequestAsync(row, approve);
            menu.Items.Add(item);
        }
        if (sender is FrameworkElement target) menu.ShowAt(target);
    }

    private async Task ProcessJoinRequestAsync(JoinRequestRow row, bool approve)
    {
        if (activeChat is not { } chat || !await ConfirmGroupActionAsync(approve ? "Одобрить заявку?" : "Отклонить заявку?")) return;
        await ExecuteGroupActionAsync(async () => await telegram!.RequestAsync(new { @type = "processChatJoinRequest", chat_id = chat.Id, user_id = row.UserId, approve }, lifetime.Token));
        await LoadJoinRequestsAsync(true);
    }

    private async Task<bool> ConfirmGroupActionAsync(string title)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = "Изменение будет отправлено в Telegram.", PrimaryButtonText = "Подтвердить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ExecuteGroupActionAsync(Func<Task> action)
    {
        try { await action(); GroupAdministrationStatus.Text = "Изменения применены сервером Telegram."; }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or InvalidOperationException) { GroupAdministrationStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private void RefreshGroupAdministration(JsonElement update)
    {
        if (activeChat?.Id != update.Field("chat_id").Number()) return;
        if (update.Field("@type").Text() == "updateNewChatJoinRequest" || update.Field("@type").Text() == "updateChatPendingJoinRequests") _ = LoadJoinRequestsAsync(true);
        else _ = RefreshChatAsync(activeChat.Id);
    }
}

internal sealed record InviteLinkRow(string Link, string Name, int Date, int MemberLimit, int MemberCount, int PendingCount, bool CreatesJoinRequest, bool IsPrimary)
{
    public string Summary => $"Вступили: {MemberCount} · заявок: {PendingCount}";
    public static InviteLinkRow From(JsonElement value) => new(value.Field("invite_link").Text(), value.Field("name").Text() is { Length: > 0 } name ? name : "Основная ссылка", (int)value.Field("date").Number(), (int)value.Field("member_limit").Number(), (int)value.Field("member_count").Number(), (int)value.Field("pending_join_request_count").Number(), value.Field("creates_join_request").Flag(), value.Field("is_primary").Flag());
}

internal sealed record JoinRequestRow(long UserId, string Name, string Username, string Bio)
{
    public static JoinRequestRow From(JsonElement request, JsonElement user)
    {
        var name = string.Join(' ', new[] { user.Field("first_name").Text(), user.Field("last_name").Text() }.Where(x => x.Length > 0));
        var username = user.Field("usernames").Field("active_usernames").Items().Select(x => x.Text()).FirstOrDefault();
        return new(request.Field("user_id").Number(), name, username is null ? "" : "@" + username, request.Field("bio").Text());
    }
}
