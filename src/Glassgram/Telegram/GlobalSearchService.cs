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
            foreach (var chat in chatResults.Field("chats").Items())
            {
                var chatId = chat.Field("id").Number();
                if (!seenChatIds.Add(chatId)) continue;
                chats.Add(BuildChatResult(chat));
            }

            var contactResults = await client.RequestAsync(new { @type = "searchContacts", query, limit = ChatLimit }, token);
            foreach (var user in contactResults.Field("users").Items())
            {
                var userId = user.Field("id").Number();
                if (!seenUserIds.Add(userId)) continue;
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
            if (query.StartsWith("@"))
            {
                var username = query[1..];
                var chat = await client.RequestAsync(new { @type = "searchPublicChat", username }, token);
                if (chat.Field("@type").GetString() is "chat" or "supergroup" or "basicGroup" or "user")
                {
                    var chatId = chat.Field("id").Number();
                    if (seenChatIds.Add(chatId))
                    {
                        var result = BuildChatResult(chat);
                        result.Type = chat.Field("@type").GetString() switch
                        {
                            "user" => "User",
                            "chatTypePrivate" => "User",
                            "chatTypeBasicGroup" => "Group",
                            "chatTypeSupergroup" => "Channel",
                            _ => "Chat"
                        };
                        chats.Add(result);
                    }
                }
            }
            else
            {
                var serverChats = await client.RequestAsync(new { @type = "searchChatsOnServer", query, limit = ChatLimit }, token);
                foreach (var chat in serverChats.Field("chats").Items())
                {
                    var chatId = chat.Field("id").Number();
                    if (!seenChatIds.Add(chatId)) continue;
                    chats.Add(BuildChatResult(chat));
                }

                var publicResults = await client.RequestAsync(new { @type = "searchPublicChats", username = query }, token);
                foreach (var chat in publicResults.Field("chats").Items())
                {
                    var chatId = chat.Field("id").Number();
                    if (!seenChatIds.Add(chatId)) continue;
                    publicChats.Add(BuildPublicChatResult(chat));
                }
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

    public static async Task<List<SearchMessageResult>> SearchMessagesGlobalAsync(TdClient client, string query, long fromMessageId, int limit, CancellationToken token)
    {
        var messages = new List<SearchMessageResult>();
        var seenMessageKeys = new HashSet<(long ChatId, long MessageId)>();

        if (string.IsNullOrWhiteSpace(query)) return messages;

        var result = await client.RequestAsync(new
        {
            @type = "searchMessages",
            chat_id = 0,
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

        return messages;
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

    private static SearchChatResult BuildChatResult(JsonElement chat)
    {
        var type = chat.Field("type").Field("@type").GetString() ?? "";
        var isPrivate = type == "chatTypePrivate";
        var isGroup = type == "chatTypeBasicGroup";
        var isChannel = type == "chatTypeSupergroup";

        var result = new SearchChatResult
        {
            ChatId = chat.Field("id").Number(),
            Title = chat.Field("title").Text(),
            Username = chat.Field("usernames").ValueKind == JsonValueKind.Array ? chat.Field("usernames").EnumerateArray().FirstOrDefault().Field("username").Text() : "",
            Type = isPrivate ? "User" : isGroup ? "Group" : isChannel ? "Channel" : "Chat",
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
            Username = user.Field("usernames").ValueKind == JsonValueKind.Array ? user.Field("usernames").EnumerateArray().FirstOrDefault().Field("username").Text() : "",
            PhoneNumber = user.Field("phone_number").Text(),
            IsVerified = user.Field("is_verified").Flag(),
            PhotoFileId = (int)user.Field("profile_photo").Field("small").Field("id").Number()
        };
        result.SetAvatar(user.Field("profile_photo").Field("small").Field("local").Field("path").Text());
        return result;
    }

    private static SearchPublicChatResult BuildPublicChatResult(JsonElement chat)
    {
        var type = chat.Field("type").Field("@type").GetString() ?? "";
        var isGroup = type == "chatTypeBasicGroup";
        var isChannel = type == "chatTypeSupergroup";

        var result = new SearchPublicChatResult
        {
            ChatId = chat.Field("id").Number(),
            Title = chat.Field("title").Text(),
            Username = chat.Field("usernames").ValueKind == JsonValueKind.Array ? chat.Field("usernames").EnumerateArray().FirstOrDefault().Field("username").Text() : "",
            Type = isGroup ? "Group" : isChannel ? "Channel" : "Chat",
            MembersCount = (int)chat.Field("member_count").Number(),
            IsVerified = chat.Field("is_verified").Flag(),
            IsScam = chat.Field("is_scam").Flag(),
            IsFake = chat.Field("is_fake").Flag(),
            PhotoFileId = (int)chat.Field("photo").Field("small").Field("id").Number()
        };
        result.SetAvatar(chat.Field("photo").Field("small").Field("local").Field("path").Text());
        return result;
    }
}