using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Glassgram.Telegram;

internal sealed class PollOptionRow : INotifyPropertyChanged
{
    public required MessageRow Owner { get; init; }
    public int Index { get; init; }
    public string Text { get; private set; } = "";
    public int VoterCount { get; private set; }
    public int Percentage { get; private set; }
    public bool IsChosen { get; private set; }
    public bool IsBeingChosen { get; private set; }
    public string StateGlyph => Owner.IsQuiz && Owner.CorrectOptionId >= 0 ? Index == Owner.CorrectOptionId ? "✓" : IsChosen ? "✕" : "" : IsChosen ? "✓" : "";
    public string ResultText => Owner.ShowPollResults ? $"{Percentage}% · {VoterCount}" : "";
    public Visibility ResultVisibility => Owner.ShowPollResults ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VotersVisibility => !Owner.PollIsAnonymous && Owner.ShowPollResults && VoterCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(JsonElement option)
    {
        Text = option.Field("text").Field("text").Text();
        VoterCount = (int)option.Field("voter_count").Number();
        Percentage = (int)option.Field("vote_percentage").Number();
        IsChosen = option.Field("is_chosen").Flag();
        IsBeingChosen = option.Field("is_being_chosen").Flag();
        RaiseAll();
    }

    public void RaiseAll()
    {
        foreach (var name in new[] { nameof(Text), nameof(VoterCount), nameof(Percentage), nameof(IsChosen), nameof(IsBeingChosen), nameof(StateGlyph), nameof(ResultText), nameof(ResultVisibility), nameof(VotersVisibility) })
            PropertyChanged?.Invoke(this, new(name));
    }
}

internal sealed partial class MessageRow
{
    public ObservableCollection<PollOptionRow> PollOptions { get; } = [];
    public long PollId { get; private set; }
    public string PollQuestion { get; private set; } = "";
    public string PollKind { get; private set; } = "";
    public string PollSummary { get; private set; } = "";
    public string PollExplanation { get; private set; } = "";
    public int PollTotalVoters { get; private set; }
    public bool PollIsAnonymous { get; private set; }
    public bool PollAllowsMultipleAnswers { get; private set; }
    public bool PollIsClosed { get; private set; }
    public bool IsQuiz { get; private set; }
    public int CorrectOptionId { get; private set; } = -1;
    public bool HasPollAnswer => PollOptions.Any(option => option.IsChosen);
    public bool ShowPollResults => PollIsClosed || HasPollAnswer;
    public Visibility PollVisibility => PollId != 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PollExplanationVisibility => PollExplanation.Length > 0 && (PollIsClosed || HasPollAnswer) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StopPollVisibility => Outgoing && !PollIsClosed ? Visibility.Visible : Visibility.Collapsed;

    public void UpdatePoll(JsonElement poll)
    {
        PollId = poll.Field("id").Number();
        PollQuestion = poll.Field("question").Field("text").Text();
        PollTotalVoters = (int)poll.Field("total_voter_count").Number();
        PollIsAnonymous = poll.Field("is_anonymous").Flag();
        PollIsClosed = poll.Field("is_closed").Flag();
        var type = poll.Field("type");
        IsQuiz = type.Field("@type").Text() == "pollTypeQuiz";
        PollAllowsMultipleAnswers = !IsQuiz && type.Field("allow_multiple_answers").Flag();
        CorrectOptionId = IsQuiz ? (int)type.Field("correct_option_id").Number() : -1;
        PollExplanation = IsQuiz ? type.Field("explanation").Field("text").Text() : "";
        PollKind = IsQuiz ? "Викторина" : PollIsAnonymous ? "Анонимный опрос" : "Публичный опрос";
        PollSummary = PollIsClosed ? $"Завершён · голосов: {PollTotalVoters}" : $"Голосов: {PollTotalVoters}";
        var options = poll.Field("options").Items().ToArray();
        while (PollOptions.Count > options.Length) PollOptions.RemoveAt(PollOptions.Count - 1);
        for (var index = 0; index < options.Length; index++)
        {
            if (index == PollOptions.Count) PollOptions.Add(new PollOptionRow { Owner = this, Index = index });
            PollOptions[index].Update(options[index]);
        }
        foreach (var option in PollOptions) option.RaiseAll();
        foreach (var name in new[] { nameof(PollQuestion), nameof(PollKind), nameof(PollSummary), nameof(PollExplanation), nameof(PollVisibility), nameof(PollExplanationVisibility), nameof(StopPollVisibility) }) NotifyPropertyChanged(name);
    }

    private void ApplyPoll(JsonElement poll) { if (poll.ValueKind == JsonValueKind.Object) UpdatePoll(poll); }
}
