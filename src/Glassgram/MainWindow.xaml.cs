using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glassgram;

public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(4) };
    private readonly List<ChatMessage> messages = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "settings.json");
    private Preferences preferences = new();
    private bool busy;
    private bool agentBusy;
    private bool lightTheme;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 780));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Glassgram.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        try
        {
            if (File.Exists(settingsPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsPath)) ?? new();
            Endpoint.Text = preferences.Endpoint;
            ChatColumn.Width = new GridLength(Math.Clamp(preferences.ChatPaneWidth, 240, 540));
            ThemeChoice.SelectedIndex = preferences.Theme == "Light" ? 1 : 0;
            SendModeChoice.SelectedIndex = preferences.SendMode == "CtrlEnter" ? 1 : 0;
            MinimizeToTrayChoice.IsOn = preferences.MinimizeToTray;
            ApplyTheme();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            SettingsStatus.Text = "Не удалось прочитать настройки. " + ex.Message;
        }
        Closed += (_, _) => { SaveCurrentDraft(); lifetime.Cancel(); http.Dispose(); };
        InitializeMedia();
        InitializeTelegram();
    }

    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ChatsPage is null) return;
        var page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ChatsPage.Visibility = page == "chats" && telegramReady ? Visibility.Visible : Visibility.Collapsed;
        AuthPage.Visibility = page == "chats" && !telegramReady ? Visibility.Visible : Visibility.Collapsed;
        AiPage.Visibility = page == "ai" ? Visibility.Visible : Visibility.Collapsed;
        AgentPage.Visibility = page == "agent" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAi(object sender, RoutedEventArgs args) => Navigation.SelectedItem = Navigation.MenuItems[1];

    private Uri ServerUri()
    {
        if (!Uri.TryCreate(preferences.Endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new InvalidOperationException("Укажи локальный HTTP-адрес Ollama, например http://127.0.0.1:11434.");
        return uri;
    }

    private async void RefreshModels(object sender, RoutedEventArgs args)
    {
        RefreshButton.IsEnabled = false;
        AiStatus.Text = "Проверяю доступные модели…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(new Uri(ServerUri(), "/api/tags"), timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var names = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();
            Models.ItemsSource = names;
            AgentModels.ItemsSource = names;
            Models.SelectedItem = names.Contains(preferences.Model) ? preferences.Model : names.FirstOrDefault();
            AgentModels.SelectedItem = Models.SelectedItem;
            AiStatus.Text = names.Count == 0 ? "Ollama работает. Установи модель, затем повтори проверку." : "Ollama подключена. Можно отправить сообщение.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (!lifetime.IsCancellationRequested) AiStatus.Text = "Не удалось получить модели. Проверь запуск Ollama и адрес в настройках. " + ex.Message;
        }
        finally { RefreshButton.IsEnabled = !busy; }
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Models.SelectedItem is string name) preferences.Model = name;
    }

    private async void Send(object sender, RoutedEventArgs args)
    {
        var text = Prompt.Text.Trim();
        if (busy || text.Length == 0) return;
        if (Models.SelectedItem is not string model) { AiStatus.Text = "Сначала проверь Ollama и выбери модель."; return; }
        busy = true;
        SendButton.IsEnabled = RefreshButton.IsEnabled = Models.IsEnabled = false;
        AiStatus.Text = "Модель готовит ответ…";
        var outgoing = new ChatMessage("user", text);
        try
        {
            var history = messages.Append(outgoing).ToArray();
            using var response = await http.PostAsJsonAsync(new Uri(ServerUri(), "/api/chat"), new { model, messages = history, stream = false }, lifetime.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(lifetime.Token), cancellationToken: lifetime.Token);
            var reply = json.RootElement.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(reply)) throw new InvalidOperationException("Модель вернула пустой ответ.");
            messages.Add(outgoing);
            messages.Add(new("assistant", reply));
            AddMessage("Ты", text);
            AddMessage(model, reply);
            if (Prompt.Text.Trim() == text) Prompt.Text = "";
            AiStatus.Text = "Ответ получен · переписка хранится до закрытия приложения";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (!lifetime.IsCancellationRequested) AiStatus.Text = "Ответ не получен. Сообщение можно отправить повторно. " + ex.Message;
        }
        finally { busy = false; SendButton.IsEnabled = RefreshButton.IsEnabled = Models.IsEnabled = true; }
    }

    private void AddMessage(string author, string text)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Text = author, Opacity = 0.6, FontSize = 12 });
        block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 });
        Conversation.Children.Add(block);
        ConversationScroll.UpdateLayout();
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null);
    }

    private async void RunAgent(object sender, RoutedEventArgs args)
    {
        var goal = AgentPrompt.Text.Trim();
        if (agentBusy || goal.Length == 0) return;
        if (activeChat is null || !histories.TryGetValue(activeChat.Id, out var history)) { AgentStatus.Text = "Сначала открой Telegram-чат."; return; }
        if (AgentModels.SelectedItem is not string model) { AgentStatus.Text = "Обнови список и выбери модель Ollama."; return; }
        agentBusy = true;
        AgentSendButton.IsEnabled = AgentModels.IsEnabled = false;
        AgentStatus.Text = "Агент анализирует текущий чат…";
        try
        {
            var context = string.Join("\n", history.Messages.Values.OrderByDescending(m => m.Date).Take(50).OrderBy(m => m.Date)
                .Select(m => $"{(m.Outgoing ? "Я" : "Собеседник")}: {m.Text}"));
            var requestMessages = new[]
            {
                new ChatMessage("system", "Ты локальный помощник в Telegram-клиенте Glassgram. Анализируй контекст, отвечай кратко и безопасно. Не утверждай, что отправил сообщение: ты только предлагаешь текст пользователю."),
                new ChatMessage("user", $"Текущий чат: {activeChat.Title}\nПоследние сообщения:\n{context}\n\nЗадача пользователя: {goal}")
            };
            using var response = await http.PostAsJsonAsync(new Uri(ServerUri(), "/api/chat"), new { model, messages = requestMessages, stream = false }, lifetime.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(lifetime.Token), cancellationToken: lifetime.Token);
            var reply = json.RootElement.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(reply)) throw new InvalidOperationException("Модель вернула пустой ответ.");
            AddAgentMessage("Задача", goal);
            AddAgentMessage(model, reply);
            AgentPrompt.Text = "";
            AgentStatus.Text = "Готово · результат не отправлен в Telegram";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (!lifetime.IsCancellationRequested) AgentStatus.Text = "Агент не ответил. " + ex.Message;
        }
        finally { agentBusy = false; AgentSendButton.IsEnabled = AgentModels.IsEnabled = true; }
    }

    private void AddAgentMessage(string author, string text)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Text = author, Opacity = 0.6, FontSize = 12 });
        block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 });
        AgentConversation.Children.Add(block);
        AgentScroll.UpdateLayout();
        AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null);
    }

    private void SaveSettings(object sender, RoutedEventArgs args)
    {
        var previous = preferences.Endpoint;
        try
        {
            preferences.Endpoint = Endpoint.Text.Trim();
            preferences.SendMode = (SendModeChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "CtrlEnter" ? "CtrlEnter" : "Enter";
            preferences.MinimizeToTray = MinimizeToTrayChoice.IsOn;
            ServerUri();
            SavePreferences();
            SettingsStatus.Text = "Настройки сохранены.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            preferences.Endpoint = previous;
            SettingsStatus.Text = ex.Message;
        }
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Root is null || ThemeChoice?.SelectedItem is not ComboBoxItem item) return;
        preferences.Theme = item.Tag?.ToString() == "Light" ? "Light" : "Dark";
        ApplyTheme();
        SavePreferences();
    }

    private void ApplyTheme()
    {
        lightTheme = preferences.Theme == "Light";
        Root.RequestedTheme = lightTheme ? ElementTheme.Light : ElementTheme.Dark;
        RefreshReadMarks();
    }

    private void SavePreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
        File.Move(temporaryPath, settingsPath, overwrite: true);
    }
}

public record ChatMessage(string role, string content);
public sealed class Preferences
{
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public double ChatPaneWidth { get; set; } = 320;
    public string SendMode { get; set; } = "Enter";
    public bool MinimizeToTray { get; set; }
    public Dictionary<long, string> Drafts { get; set; } = [];
}
