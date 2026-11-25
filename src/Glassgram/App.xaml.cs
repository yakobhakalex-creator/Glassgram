using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using System.Runtime.InteropServices;

namespace Glassgram;

public partial class App : Application
{
    private MainWindow? window;
    private long? notificationChatId;
    private bool notificationsRegistered;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { LaunchWindow(); }
        catch (Exception exception)
        {
            ReportStartupFailure(exception);
            throw;
        }
    }

    private void LaunchWindow()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            notificationsRegistered = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Messenger remains available if notifications are blocked by Windows.
        }
        window = new MainWindow();
        window.Closed += (_, _) =>
        {
            if (notificationsRegistered)
            {
                try { AppNotificationManager.Default.Unregister(); }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
            }
        };
        window.Activate();
        if (notificationChatId is long chatId) _ = window.OpenChatFromNotificationAsync(chatId);
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var value = args.Argument.Split('&').FirstOrDefault(part => part.StartsWith("chatId=", StringComparison.Ordinal));
        if (value is null || !long.TryParse(value[7..], out var chatId)) return;
        notificationChatId = chatId;
        window?.DispatcherQueue.TryEnqueue(() => _ = window.OpenChatFromNotificationAsync(chatId));
    }

    private static void ReportStartupFailure(Exception exception)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "startup-error.txt"), exception.GetType().FullName + Environment.NewLine + exception.Message + Environment.NewLine + exception.StackTrace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
