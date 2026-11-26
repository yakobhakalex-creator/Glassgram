namespace Glassgram.Telegram;

internal static class PollRequestFactory
{
    private static object Formatted(string text) => new { @type = "formattedText", text, entities = Array.Empty<object>() };

    public static object BuildSendRequest(long chatId, object? topicId, string question, IReadOnlyList<string> options, bool anonymous, bool multiple, bool quiz, int correctOptionId, string explanation) => new
    {
        @type = "sendMessage", chat_id = chatId, topic_id = topicId, reply_to = (object?)null,
        options = (object?)null, reply_markup = (object?)null,
        input_message_content = new Dictionary<string, object?>
        {
            ["@type"] = "inputMessagePoll", ["question"] = Formatted(question), ["options"] = options.Select(Formatted).ToArray(), ["is_anonymous"] = anonymous,
            ["type"] = quiz ? new Dictionary<string, object?> { ["@type"] = "pollTypeQuiz", ["correct_option_id"] = correctOptionId, ["explanation"] = Formatted(explanation) } : new Dictionary<string, object?> { ["@type"] = "pollTypeRegular", ["allow_multiple_answers"] = multiple },
            ["open_period"] = 0, ["close_date"] = 0, ["is_closed"] = false
        }
    };
}
