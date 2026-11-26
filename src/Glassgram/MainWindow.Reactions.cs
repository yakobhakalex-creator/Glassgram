// TDLib reaction flow follows Unigram (GPL-3.0), commit 9354bcb753be3b16086bd3886b8adec062ccdd91.
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glassgram;

public partial class MainWindow
{
    private void ApplyMessageInteractionUpdate(JsonElement update)
    {
        var chatId = update.Field("chat_id").Number();
        var messageId = update.Field("message_id").Number();
        if (!histories.TryGetValue(chatId, out var history) || !history.Messages.TryGetValue(messageId, out var row)) return;
        var source = update.Field("interaction_info").Field("reactions");
        if (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            source = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["reactions"] = update.Field("reactions") });
        row.ApplyReactionInfo(source);
    }

    private async void ReactionClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReactionRow reaction ||
            telegram is null || reaction.Key == "paid") return;
        var message = reaction.Owner;
        var type = ReactionType(reaction.Key);
        try
        {
            await telegram.RequestAsync(reaction.Chosen
                ? new { @type = "removeMessageReaction", chat_id = message.ChatId, message_id = message.Id, reaction_type = type }
                : new { @type = "addMessageReaction", chat_id = message.ChatId, message_id = message.Id, reaction_type = type, is_big = false, update_recent_reactions = true }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Не удалось изменить реакцию. " + ex.Message; }
    }

    private async void ShowReactionPicker(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null) return;
        try
        {
            var result = await telegram.RequestAsync(new { @type = "getMessageAvailableReactions", chat_id = row.ChatId, message_id = row.Id, row_size = 8 }, lifetime.Token);
            var available = result.Field("top_reactions").Items().Concat(result.Field("recent_reactions").Items())
                .Concat(result.Field("popular_reactions").Items()).GroupBy(x => MessageRow.ReactionKey(x.Field("type"))).Select(x => x.First()).Where(x => x.Field("type").Field("@type").Text() != "reactionTypePaid").Take(48).ToArray();
            var panel = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            for (var column = 0; column < 8; column++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var index = 0;
            foreach (var item in available)
            {
                var type = item.Field("type").Clone();
                var button = new Button { Content = MessageRow.ReactionLabel(type), FontSize = 22, Margin = new Thickness(2), MinWidth = 42, Tag = type };
                button.Click += (_, _) => ToggleReaction(row, type);
                Grid.SetColumn(button, index % 8); Grid.SetRow(button, index / 8);
                if (index % 8 == 0) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                panel.Children.Add(button); index++;
            }
            var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Реакция", Content = new ScrollViewer { Content = panel, MaxHeight = 300 }, CloseButtonText = "Закрыть" };
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Реакции недоступны. " + ex.Message; }
    }

    private async void ToggleReaction(MessageRow row, JsonElement type)
    {
        if (telegram is null) return;
        var chosen = row.Reactions.FirstOrDefault(x => x.Key == MessageRow.ReactionKey(type))?.Chosen == true;
        try
        {
            await telegram.RequestAsync(chosen
                ? new { @type = "removeMessageReaction", chat_id = row.ChatId, message_id = row.Id, reaction_type = type }
                : new { @type = "addMessageReaction", chat_id = row.ChatId, message_id = row.Id, reaction_type = type, is_big = true, update_recent_reactions = true }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Не удалось изменить реакцию. " + ex.Message; }
    }

    private static object ReactionType(string key) => key.StartsWith("emoji:", StringComparison.Ordinal)
        ? new { @type = "reactionTypeEmoji", emoji = key[6..] }
        : new { @type = "reactionTypeCustomEmoji", custom_emoji_id = long.TryParse(key.AsSpan(7), out var id) ? id : 0 };

    private async void ShowReactionAuthors(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || telegram is null) return;
        try
        {
            var result = await telegram.RequestAsync(new { @type = "getMessageAddedReactions", chat_id = row.ChatId, message_id = row.Id, reaction_type = (object?)null, offset = "", limit = 50 }, lifetime.Token);
            var lines = result.Field("reactions").Items().Select(x => MessageRow.ReactionLabel(x.Field("type")) + "  " + DescribeSender(x.Field("sender_id"))).ToArray();
            await new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Реакции", Content = new TextBlock { Text = lines.Length == 0 ? "Нет доступных данных" : string.Join(Environment.NewLine, lines), TextWrapping = TextWrapping.Wrap }, CloseButtonText = "Закрыть" }.ShowAsync();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Список реакций недоступен. " + ex.Message; }
    }

    private string DescribeSender(JsonElement sender)
    {
        var id = sender.Field("user_id").Number();
        if (id != 0 && knownChats.Values.FirstOrDefault(c => c.UserId == id) is { } chat) return chat.Title;
        var chatId = sender.Field("chat_id").Number();
        return knownChats.TryGetValue(chatId, out var senderChat) ? senderChat.Title : "Пользователь Telegram";
    }
}
