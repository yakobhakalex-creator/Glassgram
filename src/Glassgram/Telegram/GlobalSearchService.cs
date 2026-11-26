using System.Text.Json;
using Glassgram.Telegram;

namespace Glassgram.Telegram;

internal static class GlobalSearchService
{
    public const int ChatLimit = 50;
    public const int MessageLimit = 30;
    public const int DebounceMs = 350;

    public static async Task<(List<SearchChatResult> Chats, List<SearchContactResult> Contacts)> SearchLocalChatsAndContactsAsync(TdClient client, string query, CancellationToken token)
    {
        var chats = new List<SearchChatResult>();
        var contacts = new List<SearchContactResult>();
        var seenChatIds = new HashSet<long>();
        var seenUserIds = new HashSet<long>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var chatResults = await client.RequestAsync(new { @type = "searchChats", query, limit = ChatLimit }, token);
            foreach (var id in chatResults.Field("chat_ids").Items())
            {
                var chatId = id.Number();
                if (!seenChatIds.Add(chatId)) continue;
                var chat = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, token);
                chats.Add(await BuildChatResultAsync(client, chat, token));
            }

            var contactResults = await client.RequestAsync(new { @type = "searchContacts", query, limit = ChatLimit }, token);
            foreach (var id in contactResults.Field("user_ids").Items())
            {
                var userId = id.Number();
                if (!seenUserIds.Add(userId)) continue;
                var user = await client.RequestAsync(new { @type = "getUser", user_id = userId }, token);
                contacts.Add(BuildContactResult(user));
            }
        }

        return (chats, contacts);
    }

    public static async Task<(List<SearchChatResult> Chats, List<SearchPublicChatResult> PublicChats)> SearchServerChatsAndPublicAsync(TdClient client, string query, CancellationToken token)
    {
        var chats = new List<SearchChatResult>();
        var publicChats = new List<SearchPublicChatResult>();
        var seenChatIds = new HashSet<long>();

        if (query.Length >= 3)
        {
            var publicQuery = query.Trim().TrimStart('@');
            if (query.StartsWith("@") && publicQuery.Length > 0)
            {
                try
                {
                    var chat = await client.RequestAsync(new { @type = "searchPublicChat", username = publicQuery }, token);
                    var chatId = chat.Field("id").Number();
                    if (seenChatIds.Add(chatId))
                        publicChats.Add(await BuildPublicChatResultAsync(client, chat, token));
                }
                catch (TdException ex) when (ex.Code == 404) { }
            }

            var serverChats = await client.RequestAsync(new { @type = "searchChatsOnServer", query, limit = ChatLimit }, token);
            foreach (var id in serverChats.Field("chat_ids").Items())
            {
                var chatId = id.Number();
                if (!seenChatIds.Add(chatId)) continue;
                var chat = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, token);
                chats.Add(await BuildChatResultAsync(client, chat, token));
            }

            var publicResults = await client.RequestAsync(new { @type = "searchPublicChats", query = publicQuery }, token);
            foreach (var id in publicResults.Field("chat_ids").Items())
            {
                var chatId = id.Number();
                if (!seenChatIds.Add(chatId)) continue;
                var chat = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, token);
                publicChats.Add(await BuildPublicChatResultAsync(client, chat, token));
            }
        }

        return (chats, publicChats);
    }

    public static async Task<List<SearchMessageResult>> SearchMessagesAsync(TdClient client, string query, long fromMessageId, long filterChatId, int limit, CancellationToken token)
    {
        var messages = new List<SearchMessageResult>();
        var seenMessageKeys = new HashSet<(long ChatId, long MessageId)>();

        if (string.IsNullOrWhiteSpace(query)) return messages;

        var result = await client.RequestAsync(new
        {
            @type = "searchMessages",
            chat_id = filterChatId,
            query,
            filter = new { @type = "searchMessagesFilterEmpty" },
            min_date = 0,
            max_date = 0,
            offset = 0,
            limit,
            from_message_id = fromMessageId
        }, token);

        foreach (var msg in result.Field("messages").Items())
        {
            var chatId = msg.Field("chat_id").Number();
            var messageId = msg.Field("id").Number();
            if (!seenMessageKeys.Add((chatId, messageId))) continue;

            var row = MessageRow.FromJson(msg);
            var sender = msg.Field("sender_id");
            var senderName = sender.Field("@type").GetString() == "messageSenderChat"
                ? msg.Field("chat_id").Number() == chatId ? "Channel" : ""
                : "";

            messages.Add(new SearchMessageResult
            {
                ChatId = chatId,
                MessageId = messageId,
                ChatTitle = "", // Will be filled by caller
                Preview = TdJson.ContentText(msg.Field("content")),
                Date = msg.Field("date").LocalDate(),
                Outgoing = msg.Field("is_outgoing").Flag(),
                SenderName = senderName
            });
        }

        return messages;
    }

    public static async Task<(List<SearchMessageResult> Messages, string NextOffset)> SearchMessagesGlobalAsync(TdClient client, string query, string offset, int limit, CancellationToken token)
    {
        var messages = new List<SearchMessageResult>();
        var seenMessageKeys = new HashSet<(long ChatId, long MessageId)>();

        if (string.IsNullOrWhiteSpace(query)) return (messages, "");

        var result = await client.RequestAsync(new
        {
            @type = "searchMessages",
            chat_list = (object?)null,
            query,
            offset,
            limit,
            filter = new { @type = "searchMessagesFilterEmpty" },
            chat_type_filter = (object?)null,
            min_date = 0,
            max_date = 0
        }, token);

        foreach (var msg in result.Field("messages").Items())
        {
            var chatId = msg.Field("chat_id").Number();
            var messageId = msg.Field("id").Number();
            if (!seenMessageKeys.Add((chatId, messageId))) continue;

            var sender = msg.Field("sender_id");
            var senderName = sender.Field("@type").GetString() == "messageSenderChat"
                ? "Channel" : "";

            messages.Add(new SearchMessageResult
            {
                ChatId = chatId,
                MessageId = messageId,
                ChatTitle = "",
                Preview = TdJson.ContentText(msg.Field("content")),
                Date = msg.Field("date").LocalDate(),
                Outgoing = msg.Field("is_outgoing").Flag(),
                SenderName = senderName
            });
        }

        return (messages, result.Field("next_offset").Text());
    }

    public static async Task<JsonElement?> GetChatAsync(TdClient client, long chatId, CancellationToken token)
    {
        try
        {
            var result = await client.RequestAsync(new { @type = "getChat", chat_id = chatId }, token);
            return result;
        }
        catch (TdException) { return null; }
    }

    public static async Task<JsonElement?> GetUserAsync(TdClient client, long userId, CancellationToken token)
    {
        try
        {
            var result = await client.RequestAsync(new { @type = "getUser", user_id = userId }, token);
            return result;
        }
        catch (TdException) { return null; }
    }

    public static async Task<JsonElement?> GetSupergroupAsync(TdClient client, long supergroupId, CancellationToken token)
    {
        try
        {
            var result = await client.RequestAsync(new { @type = "getSupergroup", supergroup_id = supergroupId }, token);
            return result;
        }
        catch (TdException) { return null; }
    }

    private static async Task<SearchChatResult> BuildChatResultAsync(TdClient client, JsonElement chat, CancellationToken token)
    {
        var type = chat.Field("type").Field("@type").GetString() ?? "";
        var isPrivate = type == "chatTypePrivate";
        var isGroup = type == "chatTypeBasicGroup";
        var isChannel = type == "chatTypeSupergroup";

        var (username, kind) = await GetChatMetadataAsync(client, chat, token);
        var result = new SearchChatResult
        {
            ChatId = chat.Field("id").Number(),
            Title = chat.Field("title").Text(),
            Username = username.Length == 0 ? "" : "@" + username,
            Type = kind,
            IsVerified = chat.Field("is_verified").Flag(),
            IsScam = chat.Field("is_scam").Flag(),
            IsFake = chat.Field("is_fake").Flag(),
            PhotoFileId = (int)chat.Field("photo").Field("small").Field("id").Number()
        };
        result.SetAvatar(chat.Field("photo").Field("small").Field("local").Field("path").Text());
        return result;
    }

    private static SearchContactResult BuildContactResult(JsonElement user)
    {
        var result = new SearchContactResult
        {
            UserId = user.Field("id").Number(),
            FirstName = user.Field("first_name").Text(),
            LastName = user.Field("last_name").Text(),
            Username = FormatUsername(user.Field("usernames")),
            PhoneNumber = user.Field("phone_number").Text(),
            IsVerified = user.Field("is_verified").Flag(),
            PhotoFileId = (int)user.Field("profile_photo").Field("small").Field("id").Number()
        };
        result.SetAvatar(user.Field("profile_photo").Field("small").Field("local").Field("path").Text());
        return result;
    }

    private static async Task<SearchPublicChatResult> BuildPublicChatResultAsync(TdClient client, JsonElement chat, CancellationToken token)
    {
        var type = chat.Field("type").Field("@type").GetString() ?? "";
        var isGroup = type == "chatTypeBasicGroup";
        var isChannel = type == "chatTypeSupergroup";

        var (username, kind) = await GetChatMetadataAsync(client, chat, token);
        var result = new SearchPublicChatResult
        {
            ChatId = chat.Field("id").Number(),
            Title = chat.Field("title").Text(),
            Username = username.Length == 0 ? "" : "@" + username,
            Type = kind,
            MembersCount = (int)chat.Field("member_count").Number(),
            IsVerified = chat.Field("is_verified").Flag(),
            IsScam = chat.Field("is_scam").Flag(),
            IsFake = chat.Field("is_fake").Flag(),
            PhotoFileId = (int)chat.Field("photo").Field("small").Field("id").Number()
        };
        result.SetAvatar(chat.Field("photo").Field("small").Field("local").Field("path").Text());
        return result;
    }

    private static async Task<(string Username, string Kind)> GetChatMetadataAsync(TdClient client, JsonElement chat, CancellationToken token)
    {
        var type = chat.Field("type");
        switch (type.Field("@type").GetString())
        {
            case "chatTypePrivate":
                var user = await client.RequestAsync(new { @type = "getUser", user_id = type.Field("user_id").Number() }, token);
                return (FormatUsername(user.Field("usernames")).TrimStart('@'), "Пользователь");
            case "chatTypeBasicGroup":
                return ("", "Группа");
            case "chatTypeSupergroup":
                var supergroup = await client.RequestAsync(new { @type = "getSupergroup", supergroup_id = type.Field("supergroup_id").Number() }, token);
                return (FormatUsername(supergroup.Field("usernames")).TrimStart('@'), supergroup.Field("is_channel").Flag() ? "Канал" : "Группа");
            default:
                return ("", "Чат");
        }
    }

    private static string FormatUsername(JsonElement usernames)
    {
        var active = usernames.Field("active_usernames").Items().FirstOrDefault().Text();
        return active.Length == 0 ? "" : "@" + active;
    }
}
