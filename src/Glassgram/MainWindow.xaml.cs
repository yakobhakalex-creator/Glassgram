using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Glassgram.Controls;

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
    private bool applyingScaleSettings;
    private readonly DispatcherTimer wallpaperTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string currentWallpaperPath = "";
    private int glassApplyVersion;

    public MainWindow()
    {
        InitializeComponent();
        var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = assemblyVersion is null
            ? "Glassgram · C#, WinUI 3 и TDLib"
            : $"Glassgram {assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build} · C#, WinUI 3 и TDLib";
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
            applyingScaleSettings = true;
            preferences.InterfaceScale = NormalizeInterfaceScale(preferences.InterfaceScale);
            InterfaceScaleChoice.SelectedItem = InterfaceScaleChoice.Items.OfType<ComboBoxItem>()
                .First(item => item.Tag?.ToString() == preferences.InterfaceScale.ToString());
            applyingScaleSettings = false;
            applyingGlassSettings = true;
            LiquidGlassChoice.IsOn = preferences.LiquidGlass;
            if (preferences.Wallpaper is not ("Custom" or "Solid")) preferences.Wallpaper = "Windows";
            AdaptiveWallpaperChoice.IsOn = preferences.Wallpaper == "Windows";
            WallpaperChoice.SelectedIndex = preferences.Wallpaper switch { "Custom" => 1, "Solid" => 2, _ => 0 };
            TransparencyChoice.Value = preferences.Transparency;
            BlurChoice.Value = preferences.BlurIntensity;
            SaturationChoice.Value = preferences.GlassSaturation;
            AnimationsChoice.IsOn = preferences.Animations;
            ComplexEffectsChoice.IsOn = preferences.ComplexEffects;
            applyingGlassSettings = false;
            ApplyTheme();
            ApplyGlassSettings();
            ApplyInterfaceScale();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            SettingsStatus.Text = "Не удалось прочитать настройки. " + ex.Message;
        }
        wallpaperTimer.Tick += WallpaperTimerTick;
        wallpaperTimer.Start();
        Closed += async (_, _) => { wallpaperTimer.Stop(); if (voiceRecording) await FinishVoiceRecordingAsync(false); activeVoice?.Dispose(); SaveCurrentDraft(); lifetime.Cancel(); http.Dispose(); };
        InitializeMedia();
        InitializeTelegram();
        InitializeSearch();
        Root.SizeChanged += (_, _) => ApplyInterfaceScale();
    }

    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ChatsPage is null) return;
        var page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (page is "summarize" or "draft" or "translate")
        {
            AgentPrompt.Text = page switch
            {
                "summarize" => "Суммаризируй последние сообщения открытого чата и выдели ключевые решения.",
                "draft" => "Подготовь короткий естественный черновик ответа на последние сообщения.",
                _ => "Переведи последние сообщения открытого чата на русский язык, сохранив смысл и тон."
            };
            page = "agent";
        }
        ChatsPage.Visibility = page == "chats" && telegramReady ? Visibility.Visible : Visibility.Collapsed;
        AuthPage.Visibility = page == "chats" && !telegramReady ? Visibility.Visible : Visibility.Collapsed;
        AiPage.Visibility = page == "ai" ? Visibility.Visible : Visibility.Collapsed;
        AgentPage.Visibility = page == "agent" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        var visiblePage = page switch { "chats" => (FrameworkElement)(telegramReady ? ChatsPage : AuthPage), "ai" => AiPage, "agent" => AgentPage, "settings" => SettingsPage, _ => null };
        if (visiblePage is not null) AnimatePage(visiblePage);
    }

    private void OpenAi(object sender, RoutedEventArgs args) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag?.ToString() == "ai");

    private void ShowMainChats(object sender, RoutedEventArgs args) { showingArchive = false; activeFolderId = 0; preferences.ActiveChatFolderId = 0; SavePreferences(); if (chatFolders.Count > 0) FolderList.SelectedItem = chatFolders[0]; RefreshChatList(); }
    private void ShowArchiveChats(object sender, RoutedEventArgs args) { showingArchive = true; RefreshChatList(); }

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

    private void InterfaceScaleChanged(object sender, SelectionChangedEventArgs args)
    {
        if (applyingScaleSettings || Root is null || InterfaceScaleChoice.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var scale)) return;
        preferences.InterfaceScale = NormalizeInterfaceScale(scale);
        ApplyInterfaceScale();
        SavePreferences();
    }

    private void ResetInterfaceScale(object sender, RoutedEventArgs args)
    {
        InterfaceScaleChoice.SelectedItem = InterfaceScaleChoice.Items.OfType<ComboBoxItem>()
            .First(item => item.Tag?.ToString() == "100");
    }

    private void ApplyInterfaceScale()
    {
        if (Root is null || ScaledRoot is null || InterfaceScaleTransform is null || Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        var factor = NormalizeInterfaceScale(preferences.InterfaceScale) / 100d;
        InterfaceScaleTransform.ScaleX = factor;
        InterfaceScaleTransform.ScaleY = factor;
        ScaledRoot.Width = Root.ActualWidth / factor;
        ScaledRoot.Height = Root.ActualHeight / factor;
    }

    private static int NormalizeInterfaceScale(int scale) => scale is 75 or 90 or 100 or 110 or 125 or 150 or 175 or 200 ? scale : 100;

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
        preferences.Wallpaper = (WallpaperChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Windows";
        preferences.AdaptiveWallpaper = preferences.Wallpaper == "Windows";
        preferences.Transparency = TransparencyChoice.Value;
        preferences.BlurIntensity = BlurChoice.Value;
        preferences.GlassSaturation = SaturationChoice.Value;
        preferences.Animations = AnimationsChoice.IsOn;
        preferences.ComplexEffects = ComplexEffectsChoice.IsOn;
    }

    private void ApplyGlassSettings()
    {
        if (Root is null) return;
        _ = ApplyGlassSettingsAsync(++glassApplyVersion);
    }

    private async Task ApplyGlassSettingsAsync(int version)
    {
        SystemBackdrop = preferences.LiquidGlass && preferences.ComplexEffects && preferences.BlurIntensity > 10 ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        var wallpaperPath = preferences.Wallpaper == "Windows" ? GetWindowsWallpaperPath()
            : preferences.Wallpaper == "Custom" ? preferences.CustomWallpaperPath : "";
        Windows.UI.Color? sampled = null;
        if (wallpaperPath.Length > 0 && File.Exists(wallpaperPath))
        {
            try
            {
                WallpaperLayer.Source = new BitmapImage(new Uri(wallpaperPath));
                WallpaperPreview.Source = new BitmapImage(new Uri(wallpaperPath));
                sampled = await SampleWallpaperColorAsync(wallpaperPath);
                currentWallpaperPath = wallpaperPath;
                WallpaperStatus.Text = $"{(preferences.Wallpaper == "Windows" ? "System Wallpaper" : "Custom Wallpaper")}\n{Path.GetFileName(wallpaperPath)}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            {
                WallpaperLayer.Source = null;
                WallpaperPreview.Source = null;
                currentWallpaperPath = "";
                WallpaperStatus.Text = "Не удалось открыть фон: " + ex.Message;
            }
        }
        else
        {
            WallpaperLayer.Source = null;
            WallpaperPreview.Source = null;
            Root.Background = WallpaperBrush("Solid", lightTheme);
            currentWallpaperPath = "";
            WallpaperStatus.Text = preferences.Wallpaper == "Solid" ? "Solid Background" : preferences.Wallpaper == "Custom"
                ? "Custom Wallpaper: выбранный файл недоступен. Выберите изображение заново."
                : "System Wallpaper: Windows не вернула доступный файл.";
        }
        if (version != glassApplyVersion || lifetime.IsCancellationRequested) return;
        var opacity = preferences.LiquidGlass ? (byte)Math.Clamp(255 - preferences.Transparency * 2.15, 45, 205) : (byte)245;
        var tint = AdaptiveTint(sampled, opacity, lightTheme, preferences.GlassSaturation);
        var conversationTint = lightTheme ? Windows.UI.Color.FromArgb((byte)(opacity * .82), 225, 239, 248) : Windows.UI.Color.FromArgb((byte)(opacity * .72), 18, 31, 45);
        if (sampled is Windows.UI.Color wall)
            conversationTint = Windows.UI.Color.FromArgb((byte)(opacity * .72), (byte)((wall.R + tint.R) / 2), (byte)((wall.G + tint.G) / 2), (byte)((wall.B + tint.B) / 2));
        Root.Background = WallpaperLayer.Source is null ? WallpaperBrush(preferences.Wallpaper, lightTheme) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 18, 28));
        var glassOpacity = Math.Clamp(0.48 + (100 - preferences.Transparency) / 240d, 0.48, 0.88);
        foreach (var glass in new GlassPanel[] { ChatListPanel, MessagesPanel, ConversationHeader, SettingsGlassPanel }) glass.GlassOpacity = glassOpacity;
        WallpaperVeil.Opacity = preferences.LiquidGlass ? Math.Clamp(0.10 + (100 - preferences.GlassSaturation) / 300d, 0.10, 0.36) : 0.42;
        Navigation.Background = preferences.LiquidGlass
            ? new AcrylicBrush { TintColor = tint, TintOpacity = Math.Clamp(glassOpacity, .55, .88), TintLuminosityOpacity = Math.Clamp(.16 + preferences.BlurIntensity / 250d, .16, .56), FallbackColor = tint }
            : new SolidColorBrush(tint);
        ComposerPanel.Background = preferences.LiquidGlass
            ? new AcrylicBrush { TintColor = tint, TintOpacity = Math.Clamp(glassOpacity, .58, .9), TintLuminosityOpacity = .32, FallbackColor = tint }
            : new SolidColorBrush(tint);
        BrandPanel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Min(170, (int)opacity), 255, 255, 255));
        var highlight = preferences.LiquidGlass ? (byte)Math.Clamp(25 + preferences.BlurIntensity * .7, 25, 95) : (byte)30;
        ChatListPanel.BorderBrush = MessagesPanel.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(highlight, 255, 255, 255));
    }

    private async void ChooseCustomWallpaper(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        preferences.CustomWallpaperPath = file.Path;
        preferences.AdaptiveWallpaper = false;
        preferences.Wallpaper = "Custom";
        applyingGlassSettings = true;
        AdaptiveWallpaperChoice.IsOn = false;
        WallpaperChoice.SelectedIndex = 1;
        applyingGlassSettings = false;
        ApplyGlassSettings();
        SavePreferences();
    }

    private void WallpaperTimerTick(object? sender, object args)
    {
        if (preferences.Wallpaper != "Windows") return;
        var path = GetWindowsWallpaperPath();
        if (!string.Equals(path, currentWallpaperPath, StringComparison.OrdinalIgnoreCase)) ApplyGlassSettings();
    }

    private static string GetWindowsWallpaperPath()
    {
        var value = new StringBuilder(1024);
        return SystemParametersInfo(0x0073, (uint)value.Capacity, value, 0) && File.Exists(value.ToString()) ? value.ToString() : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, StringBuilder value, uint flags);

    private static async Task<Windows.UI.Color?> SampleWallpaperColorAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
        return pixels.Length >= 3 ? Windows.UI.Color.FromArgb(255, pixels[2], pixels[1], pixels[0]) : null;
    }

    private static Windows.UI.Color AdaptiveTint(Windows.UI.Color? source, byte opacity, bool light, double saturation)
    {
        if (source is not Windows.UI.Color color)
            return light ? Windows.UI.Color.FromArgb(opacity, 235, 244, 250) : Windows.UI.Color.FromArgb(opacity, 24, 39, 54);
        var gray = (color.R + color.G + color.B) / 3d;
        var factor = saturation / 50d;
        byte Saturate(byte channel) => (byte)Math.Clamp(gray + (channel - gray) * factor, 0, 255);
        var r = Saturate(color.R); var g = Saturate(color.G); var b = Saturate(color.B);
        var baseValue = light ? 244 : 22;
        return Windows.UI.Color.FromArgb(opacity, (byte)((r + baseValue * 2) / 3), (byte)((g + baseValue * 2) / 3), (byte)((b + baseValue * 2) / 3));
    }

    private static Brush WallpaperBrush(string wallpaper, bool light)
    {
        if (wallpaper == "Solid")
            return new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 222, 232, 240) : Windows.UI.Color.FromArgb(255, 17, 27, 38));
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
    public List<string> RecentEmoji { get; set; } = [];
    public bool LiquidGlass { get; set; } = true;
    public string Wallpaper { get; set; } = "Windows";
    public bool AdaptiveWallpaper { get; set; } = true;
    public string CustomWallpaperPath { get; set; } = "";
    public double Transparency { get; set; } = 58;
    public double BlurIntensity { get; set; } = 65;
    public double GlassSaturation { get; set; } = 65;
    public bool Animations { get; set; } = true;
    public bool ComplexEffects { get; set; } = true;
    public int InterfaceScale { get; set; } = 100;
    public int ActiveChatFolderId { get; set; }
}
