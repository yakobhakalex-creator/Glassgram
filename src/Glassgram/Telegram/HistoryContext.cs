namespace Glassgram.Telegram;

internal enum HistoryContextKind { Chat, ForumTopic, MessageThread }

internal readonly record struct HistoryContext(
    HistoryContextKind Kind,
    long ChatId,
    long ContextId,
    long SourceChatId = 0,
    long SourceMessageId = 0)
{
    public static HistoryContext Chat(long chatId) => new(HistoryContextKind.Chat, chatId, 0);
    public static HistoryContext ForumTopic(long chatId, int forumTopicId) => new(HistoryContextKind.ForumTopic, chatId, forumTopicId);
    public static HistoryContext MessageThread(long threadChatId, long messageThreadId, long sourceChatId, long sourceMessageId) =>
        new(HistoryContextKind.MessageThread, threadChatId, messageThreadId, sourceChatId, sourceMessageId);

    public object? TopicId => Kind == HistoryContextKind.ForumTopic ? (int)ContextId : null;
}
