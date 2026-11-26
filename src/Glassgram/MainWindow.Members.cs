using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glassgram;

public partial class MainWindow
{
    private readonly ObservableCollection<ChatMemberRow> profileMembers = [];
    private JsonElement currentProfileMember;
    private JsonElement basicGroupMembers;
    private bool profileCanGetMembers;
    private int memberLoadGeneration;
    private int memberOffset;
    private int memberTotalCount;
    private string memberCategory = "members";
    private string memberQuery = "";

    private async Task PrepareMembersAsync(ChatRow chat, JsonElement fullInfo)
    {
        var isGroup = chat.BasicGroupId != 0 || chat.SupergroupId != 0;
        ProfileMembersSection.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        if (!isGroup || telegram is null) return;

        ProfileMembersList.ItemsSource ??= profileMembers;
        basicGroupMembers = chat.BasicGroupId != 0 ? fullInfo.Field("members").Clone() : default;
        profileCanGetMembers = chat.BasicGroupId != 0 || fullInfo.Field("can_get_members").Flag();
        try
        {
            var me = await telegram.RequestAsync(new { @type = "getMe" }, lifetime.Token);
            currentProfileMember = await telegram.RequestAsync(new
            {
                @type = "getChatMember", chat_id = chat.Id,
                member_id = new { @type = "messageSenderUser", user_id = me.Field("id").Number() }
            }, lifetime.Token);
            var capabilities = ChatMemberPolicy.GetCapabilities(currentProfileMember.Field("status"), default);
            ProfileInviteMemberButton.Visibility = capabilities.CanInvite ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            ProfileInviteMemberButton.Visibility = Visibility.Collapsed;
        }

        await LoadProfileMembersAsync(true);
    }

    private async void ProfileMemberCategoryClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string category } || category == memberCategory) return;
        memberCategory = category;
        MemberMembersTab.Opacity = category == "members" ? 1 : 0.58;
        MemberAdminsTab.Opacity = category == "administrators" ? 1 : 0.58;
        MemberRestrictedTab.Opacity = category == "restricted" ? 1 : 0.58;
        await LoadProfileMembersAsync(true);
    }

    private async void ProfileMemberSearchChanged(object sender, TextChangedEventArgs args)
    {
        var query = ProfileMemberSearch.Text.Trim();
        var generation = ++memberLoadGeneration;
        await Task.Delay(350);
        if (generation != memberLoadGeneration || query != ProfileMemberSearch.Text.Trim()) return;
        memberQuery = query;
        await LoadProfileMembersAsync(true);
    }

    private async void LoadMoreProfileMembers(object sender, RoutedEventArgs args) => await LoadProfileMembersAsync(false);

    private async Task LoadProfileMembersAsync(bool reset)
    {
        if (telegram is null || activeChat is not { } chat || ProfileMembersSection.Visibility != Visibility.Visible) return;
        var generation = ++memberLoadGeneration;
        var offset = reset ? 0 : memberOffset;
        var category = memberCategory;
        var query = memberQuery;
        var chatId = chat.Id;
        ProfileMembersMoreButton.IsEnabled = false;
        ProfileMembersStatus.Text = "Загрузка…";
        try
        {
            List<JsonElement> members;
            var isBasicGroup = chat.BasicGroupId != 0;
            if (chat.BasicGroupId != 0)
            {
                if (query.Length > 0)
                {
                    var result = await telegram.RequestAsync(new
                    {
                        @type = "searchChatMembers", chat_id = chat.Id, query, limit = 200, filter = (object?)null
                    }, lifetime.Token);
                    members = result.Field("members").Items().Where(member => MemberMatches(member, category, query)).Select(x => x.Clone()).ToList();
                }
                else members = basicGroupMembers.Items().Where(member => MemberMatches(member, category, query)).ToList();
            }
            else
            {
                if (!profileCanGetMembers)
                {
                    profileMembers.Clear();
                    ProfileMembersStatus.Text = "Telegram ограничивает просмотр участников этого чата.";
                    ProfileMembersMoreButton.Visibility = Visibility.Collapsed;
                    return;
                }
                var filter = MemberFilter(category, query);
                var result = await telegram.RequestAsync(new
                {
                    @type = "getSupergroupMembers", supergroup_id = chat.SupergroupId,
                    filter, offset, limit = 50
                }, lifetime.Token);
                members = result.Field("members").Items().Select(x => x.Clone()).ToList();
                memberTotalCount = (int)result.Field("total_count").Number();
            }
            if (!ChatMemberPolicy.IsCurrent(generation, memberLoadGeneration, chatId, activeChat?.Id ?? 0)) return;
            var rows = new List<ChatMemberRow>();
            foreach (var member in members)
            {
                var sender = member.Field("member_id");
                if (sender.Field("@type").Text() != "messageSenderUser") continue;
                var userId = sender.Field("user_id").Number();
                var user = await telegram.RequestAsync(new { @type = "getUser", user_id = userId }, lifetime.Token);
                if (!ChatMemberPolicy.IsCurrent(generation, memberLoadGeneration, chatId, activeChat?.Id ?? 0)) return;
                var row = ChatMemberRow.From(member, user, ChatMemberPolicy.GetCapabilities(currentProfileMember.Field("status"), member.Field("status")));
                if (isBasicGroup && query.Length > 0 && !row.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) && !row.Username.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(row);
            }
            if (isBasicGroup)
            {
                memberTotalCount = rows.Count;
                rows = rows.Skip(offset).Take(50).ToList();
                members = rows.Select(_ => default(JsonElement)).ToList();
            }
            ApplyMemberRows(rows, reset);
            memberOffset = offset + members.Count;
            ProfileMembersMoreButton.Visibility = memberOffset < memberTotalCount && members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProfileMembersStatus.Text = profileMembers.Count == 0 ? "Участники не найдены" : $"Показано: {profileMembers.Count} из {memberTotalCount}";
            foreach (var row in rows.Where(x => x.PhotoFileId != 0 && x.Avatar is null)) _ = LoadMemberAvatarAsync(row, generation, chatId);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException)
        {
            if (ChatMemberPolicy.IsCurrent(generation, memberLoadGeneration, chatId, activeChat?.Id ?? 0) && !lifetime.IsCancellationRequested)
                ProfileMembersStatus.Text = "Не удалось загрузить участников: " + ex.Message;
        }
        finally
        {
            if (generation == memberLoadGeneration) ProfileMembersMoreButton.IsEnabled = true;
        }
    }

    private static object MemberFilter(string category, string query) => category switch
    {
        "administrators" => new { @type = "supergroupMembersFilterAdministrators" },
        "restricted" => new { @type = "supergroupMembersFilterRestricted", query },
        _ when query.Length > 0 => new { @type = "supergroupMembersFilterSearch", query },
        _ => new { @type = "supergroupMembersFilterRecent" }
    };

    private static bool MemberMatches(JsonElement member, string category, string query)
    {
        var type = member.Field("status").Field("@type").Text();
        if (category == "administrators" && type is not ("chatMemberStatusCreator" or "chatMemberStatusAdministrator")) return false;
        if (category == "restricted" && type != "chatMemberStatusRestricted") return false;
        return true;
    }

    private void ApplyMemberRows(IReadOnlyList<ChatMemberRow> rows, bool reset)
    {
        if (!reset)
        {
            foreach (var row in rows.Where(row => profileMembers.All(existing => existing.UserId != row.UserId))) profileMembers.Add(row);
            return;
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var existingIndex = profileMembers.Select((item, i) => (item, i)).FirstOrDefault(x => x.item.UserId == row.UserId).i;
            if (profileMembers.Count == 0 || existingIndex < 0 || existingIndex >= profileMembers.Count || profileMembers[existingIndex].UserId != row.UserId)
                profileMembers.Insert(index, row);
            else
            {
                profileMembers[existingIndex].UpdateFrom(row);
                if (existingIndex != index) profileMembers.Move(existingIndex, index);
            }
        }
        while (profileMembers.Count > rows.Count) profileMembers.RemoveAt(profileMembers.Count - 1);
    }

    private async Task LoadMemberAvatarAsync(ChatMemberRow row, int generation, long chatId)
    {
        try
        {
            var file = await telegram!.RequestAsync(new { @type = "downloadFile", file_id = row.PhotoFileId, priority = 1, offset = 0, limit = 0, synchronous = true }, lifetime.Token);
            if (ChatMemberPolicy.IsCurrent(generation, memberLoadGeneration, chatId, activeChat?.Id ?? 0)) row.SetAvatar(file.Field("local").Field("path").Text());
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { }
    }

    private async void OpenMemberProfile(object sender, TappedRoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMemberRow row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = row.DisplayName, CloseButtonText = "Закрыть",
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = row.Username }, new TextBlock { Text = row.RoleText }, new TextBlock { Text = row.StatusText, TextWrapping = TextWrapping.Wrap } }
            }
        };
        await dialog.ShowAsync();
    }

    private void MemberContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMemberRow row) return;
        var menu = new MenuFlyout();
        if (row.CanPromote)
        {
            AddMemberMenu(menu, row.IsAdministrator ? "Изменить права администратора" : "Назначить администратором", async () => await EditAdministratorAsync(row));
            if (row.IsAdministrator) AddMemberMenu(menu, "Снять администратора", async () => await SetMemberStatusAsync(row, new { @type = "chatMemberStatusMember", member_until_date = 0 }, "Снять права администратора?"));
        }
        if (row.CanRestrict)
        {
            AddMemberMenu(menu, "Ограничить…", async () => await RestrictMemberAsync(row));
            AddMemberMenu(menu, "Удалить из группы", async () => await SetMemberStatusAsync(row, new { @type = "chatMemberStatusLeft" }, "Удалить участника из группы?"));
            if (row.IsBanned) AddMemberMenu(menu, "Разблокировать", async () => await SetMemberStatusAsync(row, new { @type = "chatMemberStatusLeft" }, "Разблокировать участника?"));
            else AddMemberMenu(menu, "Заблокировать", async () => await BanMemberAsync(row));
        }
        if (menu.Items.Count > 0 && sender is FrameworkElement target) menu.ShowAt(target);
    }

    private static void AddMemberMenu(MenuFlyout menu, string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private async Task EditAdministratorAsync(ChatMemberRow row)
    {
        var invite = new CheckBox { Content = "Приглашать участников", IsChecked = true };
        var delete = new CheckBox { Content = "Удалять сообщения", IsChecked = true };
        var restrict = new CheckBox { Content = "Ограничивать участников", IsChecked = true };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = $"Права: {row.DisplayName}", PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close, Content = new StackPanel { Spacing = 6, Children = { invite, delete, restrict } } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var rights = new Dictionary<string, object?>
        {
            ["@type"] = "chatAdministratorRights", ["can_manage_chat"] = true, ["can_change_info"] = false,
            ["can_post_messages"] = false, ["can_edit_messages"] = false, ["can_delete_messages"] = delete.IsChecked == true,
            ["can_invite_users"] = invite.IsChecked == true, ["can_restrict_members"] = restrict.IsChecked == true,
            ["can_pin_messages"] = false, ["can_manage_topics"] = false, ["can_promote_members"] = false,
            ["can_manage_video_chats"] = false, ["can_post_stories"] = false, ["can_edit_stories"] = false,
            ["can_delete_stories"] = false, ["can_manage_direct_messages"] = false, ["can_manage_tags"] = false, ["is_anonymous"] = false
        };
        await SetMemberStatusAsync(row, new Dictionary<string, object?> { ["@type"] = "chatMemberStatusAdministrator", ["can_be_edited"] = true, ["rights"] = rights }, "Сохранить права администратора?", confirmed: true);
    }

    private async Task RestrictMemberAsync(ChatMemberRow row)
    {
        var messages = new CheckBox { Content = "Разрешить сообщения", IsChecked = false };
        var media = new CheckBox { Content = "Разрешить медиа", IsChecked = false };
        var period = new ComboBox { ItemsSource = new[] { "1 день", "7 дней", "Бессрочно" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = $"Ограничить: {row.DisplayName}", PrimaryButtonText = "Применить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close, Content = new StackPanel { Spacing = 8, Children = { messages, media, period } } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var until = period.SelectedIndex == 2 ? 0 : DateTimeOffset.UtcNow.AddDays(period.SelectedIndex == 0 ? 1 : 7).ToUnixTimeSeconds();
        var permissions = Permissions(messages.IsChecked == true, media.IsChecked == true);
        await SetMemberStatusAsync(row, new Dictionary<string, object?> { ["@type"] = "chatMemberStatusRestricted", ["is_member"] = true, ["restricted_until_date"] = until, ["permissions"] = permissions }, "Применить ограничение?", confirmed: true);
    }

    private static Dictionary<string, object?> Permissions(bool messages, bool media) => new()
    {
        ["@type"] = "chatPermissions", ["can_send_basic_messages"] = messages, ["can_send_audios"] = media,
        ["can_send_documents"] = media, ["can_send_photos"] = media, ["can_send_videos"] = media,
        ["can_send_video_notes"] = media, ["can_send_voice_notes"] = media, ["can_send_polls"] = false,
        ["can_send_other_messages"] = media, ["can_add_link_previews"] = messages, ["can_change_info"] = false,
        ["can_invite_users"] = false, ["can_pin_messages"] = false, ["can_create_topics"] = false
    };

    private async Task SetMemberStatusAsync(ChatMemberRow row, object status, string confirmation, bool confirmed = false)
    {
        if (telegram is null || activeChat is not { } chat) return;
        if (!confirmed && !await ConfirmMemberActionAsync(confirmation, row.DisplayName)) return;
        try
        {
            await telegram.RequestAsync(new { @type = "setChatMemberStatus", chat_id = chat.Id, member_id = new { @type = "messageSenderUser", user_id = row.UserId }, status }, lifetime.Token);
            await LoadProfileMembersAsync(true);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { ProfileMembersStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private async Task BanMemberAsync(ChatMemberRow row)
    {
        if (telegram is null || activeChat is not { } chat || !await ConfirmMemberActionAsync("Заблокировать участника?", row.DisplayName)) return;
        try
        {
            await telegram.RequestAsync(new { @type = "banChatMember", chat_id = chat.Id, member_id = new { @type = "messageSenderUser", user_id = row.UserId }, banned_until_date = 0, revoke_messages = false }, lifetime.Token);
            await LoadProfileMembersAsync(true);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { ProfileMembersStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private async Task<bool> ConfirmMemberActionAsync(string title, string name)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = name, PrimaryButtonText = "Подтвердить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void InviteMemberClick(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { PlaceholderText = "@username" };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Пригласить участника", Content = input, PrimaryButtonText = "Найти и пригласить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || telegram is null || activeChat is not { } chat) return;
        var username = input.Text.Trim().TrimStart('@');
        if (username.Length == 0 || !await ConfirmMemberActionAsync("Пригласить участника?", "@" + username)) return;
        try
        {
            var publicChat = await telegram.RequestAsync(new { @type = "searchPublicChat", username }, lifetime.Token);
            var userId = publicChat.Field("type").Field("user_id").Number();
            if (userId == 0) throw new InvalidOperationException("Найден не пользователь.");
            await telegram.RequestAsync(new { @type = "addChatMember", chat_id = chat.Id, user_id = userId, forward_limit = 0 }, lifetime.Token);
            await LoadProfileMembersAsync(true);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException or InvalidOperationException) { ProfileMembersStatus.Text = "Ошибка Telegram: " + ex.Message; }
    }

    private void RefreshProfileMembers(JsonElement update)
    {
        if (ProfileMembersSection.Visibility == Visibility.Visible && activeChat?.Id == update.Field("chat_id").Number()) _ = LoadProfileMembersAsync(true);
    }
}

internal sealed class ChatMemberRow : INotifyPropertyChanged
{
    public long UserId { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string Username { get; private set; } = "";
    public string AvatarLetter { get; private set; } = "?";
    public string RoleText { get; private set; } = "Участник";
    public string StatusText { get; private set; } = "";
    public bool IsBot { get; private set; }
    public bool IsAdministrator { get; private set; }
    public bool IsBanned { get; private set; }
    public bool CanPromote { get; private set; }
    public bool CanRestrict { get; private set; }
    public int PhotoFileId { get; private set; }
    public ImageSource? Avatar { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public static ChatMemberRow From(JsonElement member, JsonElement user, MemberCapabilities capabilities)
    {
        var first = user.Field("first_name").Text();
        var last = user.Field("last_name").Text();
        var name = string.Join(' ', new[] { first, last }.Where(x => x.Length > 0));
        var statusType = member.Field("status").Field("@type").Text();
        var role = statusType switch { "chatMemberStatusCreator" => "Владелец", "chatMemberStatusAdministrator" => "Администратор", "chatMemberStatusRestricted" => "Ограничен", "chatMemberStatusBanned" => "Заблокирован", _ => "Участник" };
        var bot = user.Field("type").Field("@type").Text() == "userTypeBot";
        if (bot) role += " · бот";
        var username = user.Field("usernames").Field("active_usernames").Items().Select(x => x.Text()).FirstOrDefault(x => x.Length > 0);
        var row = new ChatMemberRow
        {
            UserId = user.Field("id").Number(), DisplayName = name.Length > 0 ? name : "Без имени",
            Username = username is null ? "" : "@" + username, AvatarLetter = (name.Length > 0 ? name : "?")[..1].ToUpperInvariant(),
            RoleText = role, StatusText = DescribeUserStatus(user.Field("status")), IsBot = bot,
            IsAdministrator = capabilities.IsAdministrator, IsBanned = capabilities.IsBanned,
            CanPromote = capabilities.CanPromote, CanRestrict = capabilities.CanRestrict,
            PhotoFileId = (int)user.Field("profile_photo").Field("small").Field("id").Number()
        };
        row.SetAvatar(user.Field("profile_photo").Field("small").Field("local").Field("path").Text());
        return row;
    }

    public void UpdateFrom(ChatMemberRow other)
    {
        DisplayName = other.DisplayName; Username = other.Username; AvatarLetter = other.AvatarLetter; RoleText = other.RoleText;
        StatusText = other.StatusText; IsBot = other.IsBot; IsAdministrator = other.IsAdministrator; IsBanned = other.IsBanned;
        CanPromote = other.CanPromote; CanRestrict = other.CanRestrict; PhotoFileId = other.PhotoFileId;
        if (other.Avatar is not null) Avatar = other.Avatar;
        RaiseAll();
    }

    public void SetAvatar(string path)
    {
        if (path.Length == 0) return;
        try { Avatar = new BitmapImage(new Uri(path)); Raise(nameof(Avatar)); } catch (Exception ex) when (ex is ArgumentException or UriFormatException) { }
    }

    private static string DescribeUserStatus(JsonElement status) => status.Field("@type").Text() switch
    {
        "userStatusOnline" => "в сети", "userStatusRecently" => "был(а) недавно", "userStatusLastWeek" => "был(а) на этой неделе",
        "userStatusLastMonth" => "был(а) в этом месяце", _ => "не в сети"
    };

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(DisplayName), nameof(Username), nameof(AvatarLetter), nameof(RoleText), nameof(StatusText), nameof(Avatar), nameof(CanPromote), nameof(CanRestrict) }) Raise(name);
    }

    private void Raise(string name, [CallerMemberName] string? caller = null) => PropertyChanged?.Invoke(this, new(name));
}
