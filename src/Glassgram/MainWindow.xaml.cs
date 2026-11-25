using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

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
    private bool applyingGlassSettings;

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
            applyingGlassSettings = true;
            LiquidGlassChoice.IsOn = preferences.LiquidGlass;
            WallpaperChoice.SelectedIndex = preferences.Wallpaper switch { "Pacific" => 1, "Graphite" => 2, _ => 0 };
            TransparencyChoice.Value = preferences.Transparency;
            BlurChoice.Value = preferences.BlurIntensity;
            AnimationsChoice.IsOn = preferences.Animations;
            applyingGlassSettings = false;
            ApplyTheme();
            ApplyGlassSettings();
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
        var visiblePage = page switch { "chats" => (FrameworkElement)(telegramReady ? ChatsPage : AuthPage), "ai" => AiPage, "agent" => AgentPage, "settings" => SettingsPage, _ => null };
        if (visiblePage is not null) AnimatePage(visiblePage);
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
            ReadGlassSettings();
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
        ApplyGlassSettings();
    }

    private void GlassSettingChanged(object sender, RoutedEventArgs args)
    {
        if (applyingGlassSettings || Root is null) return;
        ReadGlassSettings();
        ApplyGlassSettings();
        SavePreferences();
    }

    private void ReadGlassSettings()
    {
        preferences.LiquidGlass = LiquidGlassChoice.IsOn;
        preferences.Wallpaper = (WallpaperChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Tahoe";
        preferences.Transparency = TransparencyChoice.Value;
        preferences.BlurIntensity = BlurChoice.Value;
        preferences.Animations = AnimationsChoice.IsOn;
    }

    private void ApplyGlassSettings()
    {
        if (Root is null) return;
        SystemBackdrop = preferences.LiquidGlass ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        Root.Background = WallpaperBrush(preferences.Wallpaper, lightTheme);
        var opacity = preferences.LiquidGlass ? (byte)Math.Clamp(255 - preferences.Transparency * 2.15, 45, 205) : (byte)245;
        var tint = lightTheme ? Windows.UI.Color.FromArgb(opacity, 235, 244, 250) : Windows.UI.Color.FromArgb(opacity, 24, 39, 54);
        var conversationTint = lightTheme ? Windows.UI.Color.FromArgb((byte)(opacity * .82), 225, 239, 248) : Windows.UI.Color.FromArgb((byte)(opacity * .72), 18, 31, 45);
        var panel = new SolidColorBrush(tint);
        ChatListPanel.Background = panel;
        MessagesPanel.Background = new SolidColorBrush(conversationTint);
        ConversationHeader.Background = new SolidColorBrush(tint);
        ComposerPanel.Background = new SolidColorBrush(tint);
        Navigation.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(opacity * .72), tint.R, tint.G, tint.B));
        var highlight = preferences.LiquidGlass ? (byte)Math.Clamp(25 + preferences.BlurIntensity * .7, 25, 95) : (byte)30;
        ChatListPanel.BorderBrush = MessagesPanel.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(highlight, 255, 255, 255));
    }

    private static Brush WallpaperBrush(string wallpaper, bool light)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        var colors = wallpaper switch
        {
            "Pacific" => light ? ("#FFDCEEF5", "#FF91B7C9") : ("#FF102534", "#FF31586A"),
            "Graphite" => light ? ("#FFE7E9EE", "#FFADB8C8") : ("#FF141821", "#FF354356"),
            _ => light ? ("#FFE5F2FA", "#FF93B8D5") : ("#FF0B1628", "#FF294967")
        };
        brush.GradientStops.Add(new GradientStop { Color = ColorFromHex(colors.Item1), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = ColorFromHex(colors.Item2), Offset = 1 });
        return brush;
    }

    private static Windows.UI.Color ColorFromHex(string value) => Windows.UI.Color.FromArgb(
        Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16),
        Convert.ToByte(value.Substring(5, 2), 16), Convert.ToByte(value.Substring(7, 2), 16));

    private void AnimatePage(FrameworkElement page)
    {
        if (!preferences.Animations) return;
        var visual = ElementCompositionPreview.GetElementVisual(page);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0.72f);
        animation.InsertKeyFrame(1, 1);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Opacity", animation);
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
    public bool LiquidGlass { get; set; } = true;
    public string Wallpaper { get; set; } = "Tahoe";
    public double Transparency { get; set; } = 58;
    public double BlurIntensity { get; set; } = 65;
    public bool Animations { get; set; } = true;
}
