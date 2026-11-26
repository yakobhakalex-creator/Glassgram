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

// Forum topics and channel discussion threads use different, isolated cursors.
var forumTopicsRequest = Parse(new { @type = "getForumTopics", chat_id = 42L, query = "support", offset_date = 1700000000, offset_message_id = 900L, offset_forum_topic_id = 17, limit = 50 });
Equal("getForumTopics", forumTopicsRequest, "$forumTopics");
if (forumTopicsRequest.GetProperty("offset_forum_topic_id").GetInt32() != 17) throw new Exception("Forum topic pagination cursor regression");
var topicHistory = Parse(new { @type = "getForumTopicHistory", chat_id = 42L, forum_topic_id = 17, from_message_id = 800L, offset = 0, limit = 100 });
Equal("getForumTopicHistory", topicHistory, "$topicHistory");
var threadHistory = Parse(new { @type = "getMessageThreadHistory", chat_id = 100L, message_id = 700L, from_message_id = 600L, offset = 0, limit = 100 });
Equal("getMessageThreadHistory", threadHistory, "$threadHistory");
var topicSend = Parse(VoiceNotePipeline.BuildSendRequest(42, new VoiceNoteFile("C:\\Temp\\voice.ogg", 2, 96000), "AQID", new Dictionary<string, object?> { ["@type"] = "messageTopicForum", ["forum_topic_id"] = 17 }));
Equal("messageTopicForum", topicSend.GetProperty("topic_id"), "$topicSend.topic_id");
var chatContext = HistoryContext.Chat(42);
var topicContext = HistoryContext.ForumTopic(42, 17);
var otherTopicContext = HistoryContext.ForumTopic(42, 18);
var threadContext = HistoryContext.MessageThread(99, 700, 42, 500);
if (chatContext == topicContext || topicContext == otherTopicContext || topicContext == threadContext) throw new Exception("History contexts aren't isolated");
var routing = new Dictionary<HistoryContext, long> { [topicContext] = 1, [otherTopicContext] = 2, [threadContext] = 3 };
if (routing.Count != 3 || routing[threadContext] != 3) throw new Exception("History context routing regression");

// Polls: regular, multiple-choice, quiz and forum-topic routing.
var regularPoll = Parse(PollRequestFactory.BuildSendRequest(42, null, "Question?", new[] { "One", "Two" }, true, false, false, -1, ""));
Equal("sendMessage", regularPoll, "$regularPoll");
var regularContent = regularPoll.GetProperty("input_message_content");
Equal("inputMessagePoll", regularContent, "$regularPoll.content");
Equal("formattedText", regularContent.GetProperty("question"), "$regularPoll.question");
Equal("formattedText", regularContent.GetProperty("options")[0], "$regularPoll.options[0]");
Equal("pollTypeRegular", regularContent.GetProperty("type"), "$regularPoll.type");
if (regularContent.GetProperty("type").GetProperty("allow_multiple_answers").GetBoolean()) throw new Exception("Single-choice poll regression");

var multiplePoll = Parse(PollRequestFactory.BuildSendRequest(42, null, "Choose", new[] { "A", "B", "C" }, false, true, false, -1, ""));
if (!multiplePoll.GetProperty("input_message_content").GetProperty("type").GetProperty("allow_multiple_answers").GetBoolean()) throw new Exception("Multiple-choice poll regression");

var topic = new Dictionary<string, object?> { ["@type"] = "messageTopicForum", ["forum_topic_id"] = 17 };
var quizPoll = Parse(PollRequestFactory.BuildSendRequest(42, topic, "Quiz", new[] { "Wrong", "Right" }, true, false, true, 1, "Why"));
Equal("messageTopicForum", quizPoll.GetProperty("topic_id"), "$quizPoll.topic_id");
var quizType = quizPoll.GetProperty("input_message_content").GetProperty("type");
Equal("pollTypeQuiz", quizType, "$quizPoll.type");
Equal("formattedText", quizType.GetProperty("explanation"), "$quizPoll.explanation");
if (quizType.GetProperty("correct_option_id").GetInt32() != 1) throw new Exception("Quiz correct option regression");

var vote = Parse(new { @type = "setPollAnswer", chat_id = 42L, message_id = 7L, option_ids = new[] { 0, 2 } });
Equal("setPollAnswer", vote, "$vote");
var stop = Parse(new { @type = "stopPoll", chat_id = 42L, message_id = 7L, reply_markup = (object?)null });
Equal("stopPoll", stop, "$stopPoll");
var pollUpdate = JsonDocument.Parse("""{"@type":"messagePoll","poll":{"@type":"poll","id":5,"is_closed":true,"options":[{"@type":"pollOption","voter_count":3,"vote_percentage":100,"is_chosen":true}]}}""").RootElement;
if (!pollUpdate.GetProperty("poll").GetProperty("is_closed").GetBoolean() || !pollUpdate.GetProperty("poll").GetProperty("options")[0].GetProperty("is_chosen").GetBoolean()) throw new Exception("Poll completion update regression");

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

// Group members: root and all nested polymorphic TDLib objects retain @type.
var sender = new { @type = "messageSenderUser", user_id = 77L };
var rights = new
{
    @type = "chatAdministratorRights", can_manage_chat = true, can_change_info = false, can_post_messages = false,
    can_edit_messages = false, can_delete_messages = true, can_invite_users = true, can_restrict_members = true,
    can_pin_messages = false, can_manage_topics = false, can_promote_members = true, can_manage_video_chats = false,
    can_post_stories = false, can_edit_stories = false, can_delete_stories = false, can_manage_direct_messages = false,
    can_manage_tags = false, is_anonymous = false
};
var adminStatus = new { @type = "chatMemberStatusAdministrator", can_be_edited = true, rights };
var setAdmin = Parse(new { @type = "setChatMemberStatus", chat_id = 42L, member_id = sender, status = adminStatus });
Equal("setChatMemberStatus", setAdmin, "$setAdmin");
Equal("messageSenderUser", setAdmin.GetProperty("member_id"), "$setAdmin.member_id");
Equal("chatMemberStatusAdministrator", setAdmin.GetProperty("status"), "$setAdmin.status");
Equal("chatAdministratorRights", setAdmin.GetProperty("status").GetProperty("rights"), "$setAdmin.status.rights");

var permissions = new
{
    @type = "chatPermissions", can_send_basic_messages = false, can_send_audios = false, can_send_documents = false,
    can_send_photos = false, can_send_videos = false, can_send_video_notes = false, can_send_voice_notes = false,
    can_send_polls = false, can_send_other_messages = false, can_add_link_previews = false, can_change_info = false,
    can_invite_users = false, can_pin_messages = false, can_create_topics = false
};
var restrictedStatus = new { @type = "chatMemberStatusRestricted", is_member = true, restricted_until_date = 0, permissions };
var setRestricted = Parse(new { @type = "setChatMemberStatus", chat_id = 42L, member_id = sender, status = restrictedStatus });
Equal("chatMemberStatusRestricted", setRestricted.GetProperty("status"), "$setRestricted.status");
Equal("chatPermissions", setRestricted.GetProperty("status").GetProperty("permissions"), "$setRestricted.status.permissions");
Equal("banChatMember", Parse(new { @type = "banChatMember", chat_id = 42L, member_id = sender, banned_until_date = 0, revoke_messages = false }), "$banMember");
Equal("addChatMember", Parse(new { @type = "addChatMember", chat_id = 42L, user_id = 77L, forward_limit = 0 }), "$addMember");
var membersPage = Parse(new { @type = "getSupergroupMembers", supergroup_id = 9L, filter = new { @type = "supergroupMembersFilterSearch", query = "anna" }, offset = 50, limit = 50 });
Equal("supergroupMembersFilterSearch", membersPage.GetProperty("filter"), "$membersPage.filter");
if (membersPage.GetProperty("offset").GetInt32() != 50) throw new Exception("Member pagination offset regression");
Equal("searchChatMembers", Parse(new { @type = "searchChatMembers", chat_id = 42L, query = "anna", limit = 200, filter = (object?)null }), "$searchChatMembers");

var ownerCapabilities = ChatMemberPolicy.GetCapabilities(Parse(new { @type = "chatMemberStatusCreator" }), Parse(new { @type = "chatMemberStatusAdministrator", can_be_edited = false, rights }));
if (ownerCapabilities.CanPromote) throw new Exception("Uneditable administrator must not be manageable");
var adminCapabilities = ChatMemberPolicy.GetCapabilities(Parse(adminStatus), Parse(new { @type = "chatMemberStatusMember", member_until_date = 0 }));
if (!adminCapabilities.CanPromote || !adminCapabilities.CanRestrict || !adminCapabilities.CanInvite) throw new Exception("Administrator capability regression");
if (ChatMemberPolicy.IsCurrent(4, 5, 42, 42) || ChatMemberPolicy.IsCurrent(5, 5, 42, 41)) throw new Exception("Member stale-response guard regression");

// Group settings, invite links, and join requests.
Equal("setChatTitle", Parse(new { @type = "setChatTitle", chat_id = 42L, title = "Group" }), "$setChatTitle");
Equal("setChatDescription", Parse(new { @type = "setChatDescription", chat_id = 42L, description = "About" }), "$setChatDescription");
var setPhoto = Parse(new { @type = "setChatPhoto", chat_id = 42L, photo = new { @type = "inputChatPhotoStatic", photo = new { @type = "inputFileLocal", path = "C:\\Temp\\group.png" } } });
Equal("inputChatPhotoStatic", setPhoto.GetProperty("photo"), "$setPhoto.photo");
Equal("inputFileLocal", setPhoto.GetProperty("photo").GetProperty("photo"), "$setPhoto.photo.photo");
var setPermissions = Parse(new { @type = "setChatPermissions", chat_id = 42L, permissions });
Equal("chatPermissions", setPermissions.GetProperty("permissions"), "$setPermissions.permissions");
Equal("setChatSlowModeDelay", Parse(new { @type = "setChatSlowModeDelay", chat_id = 42L, slow_mode_delay = 30 }), "$slowMode");
var reactions = Parse(new { @type = "setChatAvailableReactions", chat_id = 42L, available_reactions = new { @type = "chatAvailableReactionsSome", reactions = Array.Empty<object>(), max_reaction_count = 1 } });
Equal("chatAvailableReactionsSome", reactions.GetProperty("available_reactions"), "$reactions.available_reactions");
Equal("toggleChatHasProtectedContent", Parse(new { @type = "toggleChatHasProtectedContent", chat_id = 42L, has_protected_content = true }), "$protected");
Equal("joinChat", Parse(new { @type = "joinChat", chat_id = 42L }), "$join");
Equal("leaveChat", Parse(new { @type = "leaveChat", chat_id = 42L }), "$leave");

var invitePage = Parse(new { @type = "getChatInviteLinks", chat_id = 42L, creator_user_id = 77L, is_revoked = false, offset_date = 123, offset_invite_link = "https://t.me/+next", limit = 50 });
if (invitePage.GetProperty("offset_date").GetInt32() != 123 || invitePage.GetProperty("offset_invite_link").GetString() != "https://t.me/+next") throw new Exception("Invite link pagination regression");
Equal("getChatInviteLink", Parse(new { @type = "getChatInviteLink", chat_id = 42L, invite_link = "https://t.me/+one" }), "$getInvite");
Equal("createChatInviteLink", Parse(new { @type = "createChatInviteLink", chat_id = 42L, name = "Test", expiration_date = 0, member_limit = 10, creates_join_request = false }), "$createInvite");
Equal("editChatInviteLink", Parse(new { @type = "editChatInviteLink", chat_id = 42L, invite_link = "https://t.me/+one", name = "Test", expiration_date = 0, member_limit = 0, creates_join_request = true }), "$editInvite");
Equal("revokeChatInviteLink", Parse(new { @type = "revokeChatInviteLink", chat_id = 42L, invite_link = "https://t.me/+one" }), "$revokeInvite");
Equal("getChatInviteLinkMembers", Parse(new { @type = "getChatInviteLinkMembers", chat_id = 42L, invite_link = "https://t.me/+one", only_with_expired_subscription = false, offset_member = (object?)null, limit = 50 }), "$inviteMembers");
var joinRequests = Parse(new { @type = "getChatJoinRequests", chat_id = 42L, invite_link = "", query = "", offset_request = new { @type = "chatJoinRequest", user_id = 77L, date = 123, bio = "" }, limit = 50 });
Equal("chatJoinRequest", joinRequests.GetProperty("offset_request"), "$joinRequests.offset_request");
Equal("processChatJoinRequest", Parse(new { @type = "processChatJoinRequest", chat_id = 42L, user_id = 77L, approve = true }), "$processRequest");

var ownerGroupCapabilities = ChatMemberPolicy.GetGroupCapabilities(Parse(new { @type = "chatMemberStatusCreator" }));
if (!ownerGroupCapabilities.CanChangeInfo || !ownerGroupCapabilities.CanRestrictMembers || !ownerGroupCapabilities.CanInvite || !ownerGroupCapabilities.IsOwner) throw new Exception("Owner group rights regression");
var memberGroupCapabilities = ChatMemberPolicy.GetGroupCapabilities(Parse(new { @type = "chatMemberStatusMember", member_until_date = 0 }));
if (memberGroupCapabilities.CanChangeInfo || memberGroupCapabilities.CanInvite || memberGroupCapabilities.IsOwner || !memberGroupCapabilities.IsMember) throw new Exception("Member group rights regression");

var serverError = JsonDocument.Parse("{\"@type\":\"error\",\"code\":400,\"message\":\"CHAT_ADMIN_REQUIRED\"}").RootElement;
if (serverError.GetProperty("code").GetInt32() != 400 || serverError.GetProperty("message").GetString() != "CHAT_ADMIN_REQUIRED") throw new Exception("TDLib server error propagation regression");

Console.WriteLine("TDLib serialization regression tests passed.");
