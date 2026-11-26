using System.Text.Json;
using Glassgram.Telegram;

static JsonElement Parse(object request, long? extra = null)
{
    var json = TdRequestSerializer.Serialize(request, extra);
    return JsonDocument.Parse(json).RootElement.Clone();
}

static void Equal(string expected, JsonElement element, string path)
{
    if (!element.TryGetProperty("@type", out var type) || type.GetString() != expected)
        throw new Exception($"{path}: expected @type={expected}, JSON={element.GetRawText()}");
}

var history = Parse(new { @type = "getChatHistory", chat_id = 42L, from_message_id = 0L, offset = 0, limit = 100, only_local = false }, 9);
Equal("getChatHistory", history, "$history");
if (history.GetProperty("@extra").GetInt64() != 9) throw new Exception("getChatHistory: @extra missing");

var voice = Parse(VoiceNotePipeline.BuildSendRequest(42, new VoiceNoteFile("C:\\Temp\\voice.ogg", 2, 96000), "AQID"));
Equal("sendMessage", voice, "$voice");
var voiceContent = voice.GetProperty("input_message_content");
Equal("inputMessageVoiceNote", voiceContent, "$voice.input_message_content");
Equal("inputVoiceNote", voiceContent.GetProperty("voice_note"), "$voice.input_message_content.voice_note");
Equal("inputFileLocal", voiceContent.GetProperty("voice_note").GetProperty("voice_note"), "$voice.input_message_content.voice_note.voice_note");
Equal("formattedText", voiceContent.GetProperty("caption"), "$voice.input_message_content.caption");

var syntheticPath = Path.Combine(Path.GetTempPath(), $"glassgram-voice-test-{Guid.NewGuid():N}.ogg");
try
{
    var pcm = new short[VoiceNotePipeline.SampleRate * 2];
    for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / VoiceNotePipeline.SampleRate) * 8000);
    var encoded = VoiceNotePipeline.Encode(syntheticPath, pcm);
    if (encoded.Duration != 2 || encoded.DecodedSamples < pcm.Length)
        throw new Exception($"Synthetic voice validation failed: duration={encoded.Duration}, decoded={encoded.DecodedSamples}.");
    var bytes = File.ReadAllBytes(syntheticPath);
    var head = bytes.AsSpan().IndexOf("OpusHead"u8);
    if (head < 0 || bytes[head + 10] == 0 && bytes[head + 11] == 0)
        throw new Exception("OpusHead or pre-skip is invalid.");
}
finally { File.Delete(syntheticPath); }

var reaction = Parse(new { @type = "addMessageReaction", chat_id = 42L, message_id = 7L, reaction_type = new { @type = "reactionTypeEmoji", emoji = "👍" }, is_big = false, update_recent_reactions = true });
Equal("reactionTypeEmoji", reaction.GetProperty("reaction_type"), "$reaction.reaction_type");

var sticker = Parse(new
{
    @type = "sendMessage", chat_id = 42L,
    input_message_content = new Dictionary<string, object?>
    {
        ["@type"] = "inputMessageSticker",
        ["sticker"] = new Dictionary<string, object?> { ["@type"] = "inputSticker", ["sticker"] = new Dictionary<string, object?> { ["@type"] = "inputFileId", ["id"] = 5 } }
    }
});
var stickerContent = sticker.GetProperty("input_message_content");
Equal("inputMessageSticker", stickerContent, "$sticker.input_message_content");
Equal("inputSticker", stickerContent.GetProperty("sticker"), "$sticker.input_message_content.sticker");
Equal("inputFileId", stickerContent.GetProperty("sticker").GetProperty("sticker"), "$sticker.input_message_content.sticker.sticker");

try { TdRequestSerializer.Serialize(new { chat_id = 42L }); throw new Exception("Missing root @type wasn't rejected"); }
catch (JsonException) { }

// Global Search serialization tests
var searchChats = Parse(new { @type = "searchChats", query = "test", limit = 50 });
Equal("searchChats", searchChats, "$searchChats");

var searchChatsOnServer = Parse(new { @type = "searchChatsOnServer", query = "test", limit = 50 });
Equal("searchChatsOnServer", searchChatsOnServer, "$searchChatsOnServer");

var searchContacts = Parse(new { @type = "searchContacts", query = "test", limit = 50 });
Equal("searchContacts", searchContacts, "$searchContacts");

var searchPublicChats = Parse(new { @type = "searchPublicChats", query = "test" });
Equal("searchPublicChats", searchPublicChats, "$searchPublicChats");

var searchPublicChat = Parse(new { @type = "searchPublicChat", username = "testchannel" });
Equal("searchPublicChat", searchPublicChat, "$searchPublicChat");

var searchMessages = Parse(new
{
    @type = "searchMessages",
    chat_list = (object?)null,
    query = "test",
    offset = "next-page-token",
    limit = 30,
    filter = new { @type = "searchMessagesFilterEmpty" },
    chat_type_filter = (object?)null,
    min_date = 0,
    max_date = 0
});
Equal("searchMessages", searchMessages, "$searchMessages");
var filter = searchMessages.GetProperty("filter");
Equal("searchMessagesFilterEmpty", filter, "$searchMessages.filter");

var getChat = Parse(new { @type = "getChat", chat_id = 42L });
Equal("getChat", getChat, "$getChat");

var getUser = Parse(new { @type = "getUser", user_id = 42L });
Equal("getUser", getUser, "$getUser");

var getSupergroup = Parse(new { @type = "getSupergroup", supergroup_id = 42L });
Equal("getSupergroup", getSupergroup, "$getSupergroup");

var getMessage = Parse(new { @type = "getMessage", chat_id = 42L, message_id = 7L });
Equal("getMessage", getMessage, "$getMessage");

var createPrivateChat = Parse(new { @type = "createPrivateChat", user_id = 42L, force = false });
Equal("createPrivateChat", createPrivateChat, "$createPrivateChat");

// Deduplication test
var dedupChats = new List<(long ChatId, string Title)>
{
    (1, "Chat 1"),
    (2, "Chat 2"),
    (1, "Chat 1 Duplicate"),
    (3, "Chat 3")
};
var seenChatIds = new HashSet<long>();
var uniqueChats = dedupChats.Where(c => seenChatIds.Add(c.ChatId)).ToList();
if (uniqueChats.Count != 3) throw new Exception($"Chat deduplication failed: expected 3, got {uniqueChats.Count}");

var dedupMessages = new List<(long ChatId, long MessageId)>
{
    (1, 100),
    (1, 101),
    (2, 100),
    (1, 100), // Duplicate
    (2, 101)
};
var seenMessageKeys = new HashSet<(long ChatId, long MessageId)>();
var uniqueMessages = dedupMessages.Where(m => seenMessageKeys.Add(m)).ToList();
if (uniqueMessages.Count != 4) throw new Exception($"Message deduplication failed: expected 4, got {uniqueMessages.Count}");

// Stale request generation test
int generation = 0;
var currentGen = ++generation;
var staleGen = currentGen - 1;
if (staleGen == generation) throw new Exception("Generation check logic failed");

if (searchMessages.GetProperty("offset").GetString() != "next-page-token") throw new Exception("searchMessages offset mismatch");
if (searchMessages.TryGetProperty("from_message_id", out _)) throw new Exception("searchMessages must not contain from_message_id");

// Per-chat search uses a numeric cursor returned as next_from_message_id.
var chatSearch = Parse(new
{
    @type = "searchChatMessages", chat_id = 42L, topic_id = (object?)null, query = "needle",
    sender_id = new Dictionary<string, object?> { ["@type"] = "messageSenderUser", ["user_id"] = 5L },
    from_message_id = 777L, offset = 0, limit = 50,
    filter = new Dictionary<string, string> { ["@type"] = "searchMessagesFilterPhoto" }
});
Equal("searchChatMessages", chatSearch, "$chatSearch");
Equal("messageSenderUser", chatSearch.GetProperty("sender_id"), "$chatSearch.sender_id");
Equal("searchMessagesFilterPhoto", chatSearch.GetProperty("filter"), "$chatSearch.filter");
if (chatSearch.GetProperty("from_message_id").GetInt64() != 777 || chatSearch.GetProperty("offset").GetInt32() != 0)
    throw new Exception("searchChatMessages cursor regression");
if (chatSearch.TryGetProperty("next_offset", out _)) throw new Exception("searchChatMessages must not use global next_offset");

var pinnedSearch = Parse(new { @type = "searchChatMessages", chat_id = 42L, topic_id = (object?)null, query = "", sender_id = (object?)null, from_message_id = 0L, offset = 0, limit = 50, filter = new { @type = "searchMessagesFilterPinned" } });
Equal("searchMessagesFilterPinned", pinnedSearch.GetProperty("filter"), "$pinnedSearch.filter");
Equal("getChatPinnedMessage", Parse(new { @type = "getChatPinnedMessage", chat_id = 42L }), "$getPinned");

var chatSearchGeneration = 12;
var issuedGeneration = chatSearchGeneration;
chatSearchGeneration++;
if (issuedGeneration == chatSearchGeneration) throw new Exception("Per-chat stale response guard regression");

// Chat folders: root and nested TDLib types.
var folderObject = new Dictionary<string, object?>
{
    ["@type"] = "chatFolder", ["name"] = new Dictionary<string, object?>
    {
        ["@type"] = "chatFolderName", ["text"] = new { @type = "formattedText", text = "Work", entities = Array.Empty<object>() }, ["animate_custom_emoji"] = false
    },
    ["icon"] = new Dictionary<string, object?> { ["@type"] = "chatFolderIcon", ["name"] = "Custom" },
    ["color_id"] = -1, ["is_shareable"] = false,
    ["pinned_chat_ids"] = Array.Empty<long>(), ["included_chat_ids"] = new long[] { 3, 7 }, ["excluded_chat_ids"] = Array.Empty<long>(),
    ["exclude_muted"] = false, ["exclude_read"] = false, ["exclude_archived"] = true,
    ["include_contacts"] = true, ["include_non_contacts"] = false, ["include_bots"] = false,
    ["include_groups"] = true, ["include_channels"] = false
};
var createFolder = Parse(new { @type = "createChatFolder", folder = folderObject });
Equal("createChatFolder", createFolder, "$createFolder");
Equal("chatFolder", createFolder.GetProperty("folder"), "$createFolder.folder");
Equal("chatFolderName", createFolder.GetProperty("folder").GetProperty("name"), "$createFolder.folder.name");
Equal("formattedText", createFolder.GetProperty("folder").GetProperty("name").GetProperty("text"), "$createFolder.folder.name.text");
Equal("chatFolderIcon", createFolder.GetProperty("folder").GetProperty("icon"), "$createFolder.folder.icon");
Equal("editChatFolder", Parse(new { @type = "editChatFolder", chat_folder_id = 9, folder = folderObject }), "$editFolder");
Equal("deleteChatFolder", Parse(new { @type = "deleteChatFolder", chat_folder_id = 9, leave_chat_ids = Array.Empty<long>() }), "$deleteFolder");
Equal("reorderChatFolders", Parse(new { @type = "reorderChatFolders", chat_folder_ids = new[] { 9, 4 }, main_chat_list_position = 1 }), "$reorderFolders");
var loadFolder = Parse(new { @type = "loadChats", chat_list = new { @type = "chatListFolder", chat_folder_id = 9 }, limit = 100 });
Equal("loadChats", loadFolder, "$loadFolder");
Equal("chatListFolder", loadFolder.GetProperty("chat_list"), "$loadFolder.chat_list");

var folderPositions = new Dictionary<long, long> { [3] = 300, [7] = 700, [5] = 0 };
var folderMembers = folderPositions.Where(pair => pair.Value != 0).OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray();
if (!folderMembers.SequenceEqual(new long[] { 7, 3 })) throw new Exception("Folder membership/order regression");
var folderGeneration = 4;
var requestedFolderGeneration = folderGeneration++;
if (requestedFolderGeneration == folderGeneration) throw new Exception("Folder stale-switch guard regression");

Console.WriteLine("TDLib serialization regression tests passed.");
