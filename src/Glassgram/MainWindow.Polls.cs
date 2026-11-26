using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace Glassgram;

public partial class MainWindow
{
    private sealed class PollDraftOption(string value) : INotifyPropertyChanged
    {
        private string text = value;
        public string Text { get => text; set { if (text == value) return; text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private async void CreatePoll(object sender, RoutedEventArgs args)
    {
        if (telegram is null || activeChat is null || !activeChat.CanSendPolls) { MessengerStatus.Text = "В этом чате нельзя отправлять опросы."; return; }
        var maxOptions = 10;
        try { var value = await telegram.RequestAsync(new { @type = "getOption", name = "poll_answer_count_max" }, lifetime.Token); maxOptions = Math.Clamp((int)value.Field("value").Number(), 2, 20); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }

        var question = new TextBox { Header = "Вопрос", MaxLength = 255, TextWrapping = TextWrapping.Wrap };
        var options = new ObservableCollection<PollDraftOption> { new(""), new("") };
        var list = new ListView { ItemsSource = options, Height = 190, SelectionMode = ListViewSelectionMode.Single };
        var template = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBox Text='{Binding Text, Mode=TwoWay}' MaxLength='100' PlaceholderText='Вариант ответа' Margin='0,2'/></DataTemplate>");
        list.ItemTemplate = template;
        var add = new Button { Content = "Добавить" }; var remove = new Button { Content = "Удалить" }; var up = new Button { Content = "↑" }; var down = new Button { Content = "↓" };
        add.Click += (_, _) => { if (options.Count < maxOptions) options.Add(new("")); };
        remove.Click += (_, _) => { if (list.SelectedItem is PollDraftOption item && options.Count > 2) options.Remove(item); };
        up.Click += (_, _) => MovePollOption(options, list, -1); down.Click += (_, _) => MovePollOption(options, list, 1);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; foreach (var button in new[] { add, remove, up, down }) controls.Children.Add(button);
        var anonymous = new ToggleSwitch { Header = "Анонимное голосование", IsOn = true };
        var multiple = new ToggleSwitch { Header = "Несколько ответов" };
        var quiz = new ToggleSwitch { Header = "Режим викторины" };
        var correct = new ComboBox { Header = "Правильный ответ", ItemsSource = options, DisplayMemberPath = nameof(PollDraftOption.Text), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
        var explanation = new TextBox { Header = "Пояснение (необязательно)", MaxLength = 200, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        quiz.Toggled += (_, _) => { correct.Visibility = explanation.Visibility = quiz.IsOn ? Visibility.Visible : Visibility.Collapsed; if (quiz.IsOn) multiple.IsOn = false; };
        multiple.Toggled += (_, _) => { if (multiple.IsOn) quiz.IsOn = false; };
        var panel = new StackPanel { Spacing = 8, MinWidth = 430 }; foreach (var control in new UIElement[] { question, list, controls, anonymous, multiple, quiz, correct, explanation }) panel.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Создать опрос", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, PrimaryButtonText = "Создать", CloseButtonText = "Отмена" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var values = options.Select(option => option.Text.Trim()).ToArray();
        if (question.Text.Trim().Length is < 1 or > 255 || values.Length < 2 || values.Any(value => value.Length is < 1 or > 100) || values.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != values.Length) { MessengerStatus.Text = $"Нужно 2–{maxOptions} уникальных вариантов до 100 символов."; return; }
        var correctIndex = quiz.IsOn && correct.SelectedItem is PollDraftOption selected ? options.IndexOf(selected) : -1;
        if (quiz.IsOn && correctIndex < 0) { MessengerStatus.Text = "Выбери правильный ответ викторины."; return; }
        try
        {
            var result = await telegram.RequestAsync(PollRequestFactory.BuildSendRequest(ActiveSendChatId, ActiveTopicId, question.Text.Trim(), values, anonymous.IsOn, multiple.IsOn, quiz.IsOn, correctIndex, explanation.Text.Trim()), lifetime.Token);
            var row = MessageRow.FromJson(result); if (activeHistoryContext is not null) InsertContextMessage(row); else InsertMessage(result); MessengerStatus.Text = "";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or JsonException) { MessengerStatus.Text = "Опрос не отправлен. " + ex.Message; }
    }

    private static void MovePollOption(ObservableCollection<PollDraftOption> options, ListView list, int delta)
    {
        if (list.SelectedItem is not PollDraftOption item) return; var oldIndex = options.IndexOf(item); var newIndex = oldIndex + delta;
        if (newIndex < 0 || newIndex >= options.Count) return; options.Move(oldIndex, newIndex); list.SelectedItem = item;
    }

    private async void PollOptionClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not PollOptionRow option || telegram is null) return;
        var poll = option.Owner; if (poll.PollIsClosed || option.IsBeingChosen || poll.IsQuiz && poll.HasPollAnswer) return;
        var selected = poll.PollOptions.Where(item => item.IsChosen).Select(item => item.Index).ToList();
        if (poll.PollAllowsMultipleAnswers) { if (!selected.Remove(option.Index)) selected.Add(option.Index); } else { selected.Clear(); if (!option.IsChosen) selected.Add(option.Index); }
        try { await telegram.RequestAsync(new { @type = "setPollAnswer", chat_id = poll.ChatId, message_id = poll.Id, option_ids = selected.ToArray() }, lifetime.Token); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Голос не отправлен. " + ex.Message; }
    }

    private async void OpenPollVoters(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not PollOptionRow option || option.Owner.PollIsAnonymous || telegram is null) return;
        var voters = new ObservableCollection<string>(); var offset = 0; var more = true; var list = new ListView { ItemsSource = voters, Height = 330 }; var load = new Button { Content = "Показать ещё", HorizontalAlignment = HorizontalAlignment.Stretch };
        async Task LoadAsync() { if (!more) return; try { var result = await telegram.RequestAsync(new { @type = "getPollVoters", chat_id = option.Owner.ChatId, message_id = option.Owner.Id, option_id = option.Index, offset, limit = 50 }, lifetime.Token); var page = result.Field("voters").Items().ToArray(); foreach (var voter in page) voters.Add(await DescribePollVoterAsync(voter.Field("voter_id"))); offset += page.Length; more = page.Length > 0 && offset < result.Field("total_count").Number(); load.Visibility = more ? Visibility.Visible : Visibility.Collapsed; } catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Список голосов недоступен. " + ex.Message; more = false; load.Visibility = Visibility.Collapsed; } }
        load.Click += async (_, _) => await LoadAsync(); await LoadAsync(); var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(list); panel.Children.Add(load);
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = option.Text, Content = panel, CloseButtonText = "Закрыть" }.ShowAsync();
    }

    private async Task<string> DescribePollVoterAsync(JsonElement sender)
    {
        try { if (sender.Field("@type").Text() == "messageSenderUser") { var user = await telegram!.RequestAsync(new { @type = "getUser", user_id = sender.Field("user_id").Number() }, lifetime.Token); return (user.Field("first_name").Text() + " " + user.Field("last_name").Text()).Trim(); } var chat = await telegram!.RequestAsync(new { @type = "getChat", chat_id = sender.Field("chat_id").Number() }, lifetime.Token); return chat.Field("title").Text(); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { return "Участник"; }
    }

    private async void PollContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PollIsClosed || telegram is null) return;
        try { var properties = await telegram.RequestAsync(new { @type = "getMessageProperties", chat_id = row.ChatId, message_id = row.Id }, lifetime.Token); if (!properties.Field("can_be_edited").Flag() || sender is not FrameworkElement anchor) return; var flyout = new MenuFlyout(); var stop = new MenuFlyoutItem { Text = "Завершить опрос", Tag = row }; stop.Click += StopPoll; flyout.Items.Add(stop); flyout.ShowAt(anchor); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException) { }
    }

    private async void StopPoll(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageRow row || telegram is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Завершить опрос?", Content = "После завершения новые голоса приниматься не будут.", PrimaryButtonText = "Завершить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await telegram.RequestAsync(new { @type = "stopPoll", chat_id = row.ChatId, message_id = row.Id, reply_markup = (object?)null }, lifetime.Token); } catch (Exception ex) when (ex is TdException or OperationCanceledException) { MessengerStatus.Text = "Опрос не завершён. " + ex.Message; }
    }
}
