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

Console.WriteLine("TDLib serialization regression tests passed.");
