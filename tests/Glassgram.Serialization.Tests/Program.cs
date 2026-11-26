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

var voice = Parse(new
{
    @type = "sendMessage", chat_id = 42L, topic_id = (object?)null, reply_to = (object?)null, options = (object?)null, reply_markup = (object?)null,
    input_message_content = new
    {
        @type = "inputMessageVoiceNote",
        voice_note = new { @type = "inputVoiceNote", voice_note = new { @type = "inputFileLocal", path = "C:\\Temp\\voice.ogg" }, duration = 2, waveform = "AQID" },
        caption = new { @type = "formattedText", text = "", entities = Array.Empty<object>() }, self_destruct_type = (object?)null
    }
});
Equal("sendMessage", voice, "$voice");
var voiceContent = voice.GetProperty("input_message_content");
Equal("inputMessageVoiceNote", voiceContent, "$voice.input_message_content");
Equal("inputVoiceNote", voiceContent.GetProperty("voice_note"), "$voice.input_message_content.voice_note");
Equal("inputFileLocal", voiceContent.GetProperty("voice_note").GetProperty("voice_note"), "$voice.input_message_content.voice_note.voice_note");
Equal("formattedText", voiceContent.GetProperty("caption"), "$voice.input_message_content.caption");

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

Console.WriteLine("TDLib serialization regression tests passed.");
