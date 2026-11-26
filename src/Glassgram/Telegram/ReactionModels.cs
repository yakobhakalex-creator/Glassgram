// Reaction presentation adapted from Unigram (GPL-3.0), commit 9354bcb753be3b16086bd3886b8adec062ccdd91.
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Glassgram.Telegram;

internal sealed class ReactionRow
{
    public required MessageRow Owner { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Count { get; init; }
    public bool Chosen { get; init; }
    public string CountText => Count > 0 ? Count.ToString() : "";
}

internal sealed partial class MessageRow
{
    public ObservableCollection<ReactionRow> Reactions { get; } = [];
    public Visibility ReactionsVisibility => Reactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPinned { get; private set; }
    public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyReactionInfo(JsonElement reactions)
    {
        Reactions.Clear();
        foreach (var item in reactions.Field("reactions").Items())
        {
            var type = item.Field("type");
            var key = ReactionKey(type);
            if (key.Length == 0) continue;
            Reactions.Add(new ReactionRow
            {
                Owner = this,
                Key = key,
                Label = ReactionLabel(type),
                Count = (int)item.Field("total_count").Number(),
                Chosen = item.Field("is_chosen").Flag()
            });
        }
        PropertyChanged?.Invoke(this, new(nameof(ReactionsVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(Reactions)));
    }

    public void SetPinned(bool value)
    {
        if (IsPinned == value) return;
        IsPinned = value;
        PropertyChanged?.Invoke(this, new(nameof(IsPinned)));
        PropertyChanged?.Invoke(this, new(nameof(PinnedVisibility)));
    }

    public static string ReactionKey(JsonElement type) => type.Field("@type").Text() switch
    {
        "reactionTypeEmoji" => "emoji:" + type.Field("emoji").Text(),
        "reactionTypeCustomEmoji" => "custom:" + type.Field("custom_emoji_id").Number(),
        "reactionTypePaid" => "paid",
        _ => ""
    };

    public static string ReactionLabel(JsonElement type) => type.Field("@type").Text() switch
    {
        "reactionTypeEmoji" => type.Field("emoji").Text(),
        "reactionTypeCustomEmoji" => "◇",
        "reactionTypePaid" => "⭐",
        _ => ""
    };
}
