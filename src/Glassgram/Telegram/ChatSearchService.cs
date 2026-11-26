using System.Text.Json;

namespace Glassgram.Telegram;

internal sealed record ChatSearchPage(IReadOnlyList<JsonElement> Messages, long NextFromMessageId, int? TotalCount);

internal static class ChatSearchService
{
    internal const int PageSize = 50;

    internal static object BuildRequest(long chatId, string query, long fromMessageId, object? senderId, object? filter) => new
    {
        @type = "searchChatMessages",
        chat_id = chatId,
        topic_id = (object?)null,
        query,
        sender_id = senderId,
        from_message_id = fromMessageId,
        offset = 0,
        limit = PageSize,
        filter
    };

    internal static async Task<ChatSearchPage> SearchAsync(
        TdClient client, long chatId, string query, long fromMessageId, object? senderId, object? filter, CancellationToken token)
    {
        var result = await client.RequestAsync(BuildRequest(chatId, query, fromMessageId, senderId, filter), token);
        var total = (int)result.Field("total_count").Number();
        return new ChatSearchPage(
            result.Field("messages").Items().Where(message => message.ValueKind == JsonValueKind.Object).Select(message => message.Clone()).ToArray(),
            result.Field("next_from_message_id").Number(),
            total >= 0 ? total : null);
    }
}
