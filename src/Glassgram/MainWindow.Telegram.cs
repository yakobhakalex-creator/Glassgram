using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace Glassgram;

public partial class MainWindow
{
    private TdClient? telegram;
    private bool telegramReady;
    private bool telegramBusy;
    private bool parametersSubmitted;
    private TelegramCredentials? telegramCredentials;
    private static readonly string CredentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "telegram-credentials.dat");

    private void InitializeTelegram()
    {
        Closed += (_, _) => telegram?.Dispose();
        AuthPage.Visibility = Visibility.Visible;
        ChatsPage.Visibility = Visibility.Collapsed;
        try
        {
            if (File.Exists(CredentialsPath))
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CredentialsPath), null, DataProtectionScope.CurrentUser);
                telegramCredentials = JsonSerializer.Deserialize<TelegramCredentials>(bytes);
                if (telegramCredentials is null) throw new JsonException("Пустые параметры Telegram.");
                StartTelegram();
            }
            else ShowCredentials();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or DllNotFoundException or BadImageFormatException)
        {
            ShowCredentials();
            AuthError.Text = "Не удалось открыть сохранённую сессию. " + ex.Message;
        }
    }

    private void ShowCredentials()
    {
        HideAuthSteps();
        CredentialsPanel.Visibility = Visibility.Visible;
        AuthDescription.Text = "Для подключения нужны API ID и API hash, полученные в личном кабинете Telegram.";
    }

    private void StartTelegram()
    {
        HideAuthSteps();
        AuthDescription.Text = "Подключаем Telegram и проверяем сохранённую сессию…";
        parametersSubmitted = false;
        telegram = new TdClient();
        telegram.Update += HandleTelegramUpdate;
        _ = LoadAuthorizationStateAsync(telegram);
    }

    private async Task LoadAuthorizationStateAsync(TdClient client)
    {
        try
        {
            var state = await client.RequestAsync(new { @type = "getAuthorizationState" }, lifetime.Token);
            if (ReferenceEquals(client, telegram)) await ShowAuthorizationStateAsync(state);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) AuthError.Text = ex.Message;
        }
    }

    private void HandleTelegramUpdate(JsonElement update)
    {
        if (update.TryGetProperty("@type", out var type) && type.GetString() == "updateAuthorizationState"
            && update.TryGetProperty("authorization_state", out var state))
        {
            var copy = state.Clone();
            DispatcherQueue.TryEnqueue(() => _ = ShowAuthorizationStateAsync(copy));
        }
        else
        {
            var copy = update.Clone();
            DispatcherQueue.TryEnqueue(() =>
            {
                try { HandleMessengerUpdate(copy); }
                catch (Exception ex)
                {
                    App.ReportRuntimeFailure("TDLib update " + copy.Field("@type").Text(), ex);
                    if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Ошибка обработки обновления TDLib: " + ex.Message;
                }
            });
        }
    }

    private async Task ShowAuthorizationStateAsync(JsonElement state)
    {
        var kind = state.GetProperty("@type").GetString();
        HideAuthSteps();
        AuthError.Text = "";
        telegramReady = kind == "authorizationStateReady";
        LogoutButton.Visibility = telegramReady ? Visibility.Visible : Visibility.Collapsed;
        if (Navigation.SelectedItem == Navigation.MenuItems[0])
        {
            AuthPage.Visibility = telegramReady ? Visibility.Collapsed : Visibility.Visible;
            ChatsPage.Visibility = telegramReady ? Visibility.Visible : Visibility.Collapsed;
        }

        switch (kind)
        {
            case "authorizationStateWaitTdlibParameters":
                if (parametersSubmitted) break;
                parametersSubmitted = true;
                AuthDescription.Text = "Подготавливаем защищённую сессию…";
                try
                {
                    var credentials = telegramCredentials ?? throw new InvalidOperationException("Не указаны ключи Telegram.");
                    await telegram!.RequestAsync(new Dictionary<string, object>
                    {
                        ["@type"] = "setTdlibParameters",
                        ["database_directory"] = SessionStorage.DatabasePath,
                        ["files_directory"] = SessionStorage.FilesPath,
                        ["database_encryption_key"] = SessionStorage.GetEncryptionKey(),
                        ["use_file_database"] = true,
                        ["use_chat_info_database"] = true,
                        ["use_message_database"] = true,
                        ["use_secret_chats"] = false,
                        ["api_id"] = credentials.ApiId,
                        ["api_hash"] = credentials.ApiHash,
                        ["system_language_code"] = "ru",
                        ["device_model"] = "Windows PC",
                        ["system_version"] = Environment.OSVersion.VersionString,
                        ["application_version"] = "Glassgram 0.3.1"
                    }, lifetime.Token);
                }
                catch (Exception ex)
                {
                    parametersSubmitted = false;
                    ShowCredentials();
                    AuthError.Text = ex.Message;
                }
                break;
            case "authorizationStateWaitPhoneNumber":
                PhonePanel.Visibility = Visibility.Visible;
                AuthDescription.Text = "Войди по номеру телефона или QR-коду.";
                break;
            case "authorizationStateWaitOtherDeviceConfirmation":
                QrPanel.Visibility = Visibility.Visible;
                AuthDescription.Text = "Отсканируй QR-код в Telegram на телефоне.";
                if (state.TryGetProperty("link", out var link)) await RenderQrAsync(link.GetString() ?? "");
                break;
            case "authorizationStateWaitCode":
                CodePanel.Visibility = Visibility.Visible;
                AuthDescription.Text = "Введи код, который Telegram отправил для входа.";
                break;
            case "authorizationStateWaitEmailAddress":
                EmailPanel.Visibility = Visibility.Visible;
                AuthDescription.Text = "Telegram запросил адрес электронной почты для подтверждения входа.";
                break;
            case "authorizationStateWaitEmailCode":
                EmailCodePanel.Visibility = Visibility.Visible;
                AuthDescription.Text = "Введи код, полученный по электронной почте.";
                break;
            case "authorizationStateWaitPassword":
                PasswordPanel.Visibility = Visibility.Visible;
                AuthDescription.Text = state.TryGetProperty("password_hint", out var hint) && !string.IsNullOrEmpty(hint.GetString())
                    ? "Введи облачный пароль. Подсказка: " + hint.GetString()
                    : "Введи облачный пароль Telegram.";
                break;
            case "authorizationStateReady":
                StartMessenger();
                break;
            case "authorizationStateLoggingOut":
            case "authorizationStateClosing":
                StopMessenger();
                AuthDescription.Text = "Завершаем сеанс…";
                break;
            case "authorizationStateClosed":
                StopMessenger();
                telegram?.Dispose();
                telegram = null;
                if (!lifetime.IsCancellationRequested) StartTelegram();
                break;
            default:
                AuthDescription.Text = "Telegram запросил дополнительный шаг авторизации: " + kind;
                break;
        }
    }

    private void HideAuthSteps()
    {
        CredentialsPanel.Visibility = PhonePanel.Visibility = QrPanel.Visibility = CodePanel.Visibility = EmailPanel.Visibility = EmailCodePanel.Visibility = PasswordPanel.Visibility = Visibility.Collapsed;
    }

    private async Task RenderQrAsync(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var bytes = code.GetGraphic(8);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        QrImage.Source = image;
    }

    private void SaveTelegramCredentials(object sender, RoutedEventArgs args)
    {
        if (!int.TryParse(ApiIdInput.Text.Trim(), out var id) || id <= 0 || string.IsNullOrWhiteSpace(ApiHashInput.Password))
        {
            AuthError.Text = "Укажи корректные API ID и API hash.";
            return;
        }
        try
        {
            telegramCredentials = new TelegramCredentials(id, ApiHashInput.Password.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(CredentialsPath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(telegramCredentials);
            File.WriteAllBytes(CredentialsPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            ApiHashInput.Password = "";
            telegram?.Dispose();
            StartTelegram();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            AuthError.Text = ex.Message;
        }
    }

    private async Task SubmitTelegramAsync(object request, Button button)
    {
        if (telegramBusy || telegram is null) return;
        telegramBusy = true;
        button.IsEnabled = false;
        AuthError.Text = "";
        try { await telegram.RequestAsync(request, lifetime.Token); }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) AuthError.Text = ex.Message;
        }
        finally { telegramBusy = false; button.IsEnabled = true; }
    }

    private async void SubmitPhone(object sender, RoutedEventArgs args)
    {
        var phone = PhoneInput.Text.Trim();
        if (phone.Length == 0) { AuthError.Text = "Введи номер телефона."; return; }
        await SubmitTelegramAsync(new { @type = "setAuthenticationPhoneNumber", phone_number = phone }, PhoneButton);
    }

    private async void StartQr(object sender, RoutedEventArgs args) =>
        await SubmitTelegramAsync(new { @type = "requestQrCodeAuthentication", other_user_ids = Array.Empty<long>() }, QrButton);

    private async void ShowPhone(object sender, RoutedEventArgs args)
    {
        if (telegram is null) return;
        try
        {
            var state = await telegram.RequestAsync(new { @type = "getAuthorizationState" }, lifetime.Token);
            if (state.GetProperty("@type").GetString() != "authorizationStateWaitOtherDeviceConfirmation") return;
            AuthDescription.Text = "Возвращаемся к входу по номеру…";
            await telegram.RequestAsync(new { @type = "destroy" }, lifetime.Token);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) AuthError.Text = ex.Message;
        }
    }

    private async void SubmitCode(object sender, RoutedEventArgs args)
    {
        var code = CodeInput.Text.Trim();
        if (code.Length == 0) { AuthError.Text = "Введи код подтверждения."; return; }
        await SubmitTelegramAsync(new { @type = "checkAuthenticationCode", code }, CodeButton);
        CodeInput.Text = "";
    }

    private async void SubmitEmail(object sender, RoutedEventArgs args)
    {
        var email = EmailInput.Text.Trim();
        if (email.Length == 0) { AuthError.Text = "Введи адрес электронной почты."; return; }
        await SubmitTelegramAsync(new { @type = "setAuthenticationEmailAddress", email_address = email }, EmailButton);
    }

    private async void SubmitEmailCode(object sender, RoutedEventArgs args)
    {
        var code = EmailCodeInput.Text.Trim();
        if (code.Length == 0) { AuthError.Text = "Введи код из письма."; return; }
        await SubmitTelegramAsync(new
        {
            @type = "checkAuthenticationEmailCode",
            code = new Dictionary<string, string> { ["@type"] = "emailAddressAuthenticationCode", ["code"] = code }
        }, EmailCodeButton);
    }

    private async void SubmitPassword(object sender, RoutedEventArgs args)
    {
        var password = PasswordInput.Password;
        if (password.Length == 0) { AuthError.Text = "Введи облачный пароль."; return; }
        await SubmitTelegramAsync(new { @type = "checkAuthenticationPassword", password }, PasswordButton);
        PasswordInput.Password = "";
    }

    private async void LogoutTelegram(object sender, RoutedEventArgs args)
    {
        if (telegram is null || !telegramReady) return;
        LogoutButton.IsEnabled = false;
        try
        {
            await telegram.RequestAsync(new { @type = "logOut" }, lifetime.Token);
            telegramReady = false;
            LogoutButton.Visibility = Visibility.Collapsed;
            Navigation.SelectedItem = Navigation.MenuItems[0];
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException)
        {
            SettingsStatus.Text = "Не удалось выйти из Telegram. " + ex.Message;
        }
        finally { LogoutButton.IsEnabled = true; }
    }
}

public sealed record TelegramCredentials(int ApiId, string ApiHash);
