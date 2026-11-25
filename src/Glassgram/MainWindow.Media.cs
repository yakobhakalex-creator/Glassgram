using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using CommunityToolkit.WinUI.Lottie;
using Glassgram.Telegram;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace Glassgram;

public partial class MainWindow
{
    private readonly List<Window> mediaWindows = [];
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> mediaDownloadWaiters = [];
    private static readonly SemaphoreSlim TgsCacheGate = new(1, 1);
    private static readonly string MediaCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glassgram", "MediaCache");

    private void InitializeMedia() => Closed += (_, _) =>
    {
        foreach (var viewer in mediaWindows.ToArray()) viewer.Close();
        mediaWindows.Clear();
    };

    private async void MessageMediaLoaded(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row) return;
        try
        {
            if (row.PreviewFileId != 0 && row.PreviewPath.Length == 0)
                await DownloadMessageFileAsync(row, row.PreviewFileId, false);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) row.SetTransferError("Ошибка загрузки: " + ex.Message);
        }
    }

    private async Task<string> DownloadMessageFileAsync(MessageRow row, int fileId, bool waitForCompletion)
    {
        if (telegram is null) throw new InvalidOperationException("TDLib не подключена.");
        if (fileId == 0) throw new InvalidOperationException("TDLib вернула пустой file_id.");
        RegisterMessageFiles(row);
        var existingPath = fileId == row.PrimaryFileId ? row.MediaPath : row.PreviewPath;
        if (existingPath.Length > 0 && File.Exists(existingPath)) return existingPath;
        var primary = fileId == row.PrimaryFileId;
        var resolved = await ResolveMessageFileAsync(row, fileId, primary);
        fileId = resolved.FileId;
        row.ApplyFile(resolved.File);
        NotifyMediaFileUpdate(resolved.File);
        var resolvedPath = resolved.File.Field("local").Field("path").Text();
        if (IsTdFileReady(resolved.File) && resolvedPath.Length > 0 && File.Exists(resolvedPath)) return resolvedPath;
        TaskCompletionSource<JsonElement>? waiter = null;
        var ownsWaiter = waitForCompletion && !mediaDownloadWaiters.TryGetValue(fileId, out waiter);
        if (ownsWaiter)
        {
            waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            mediaDownloadWaiters[fileId] = waiter;
        }
        JsonElement file;
        try
        {
            file = await telegram.RequestAsync(new
            {
                @type = "downloadFile", file_id = fileId, priority = waitForCompletion ? 32 : 2,
                offset = 0, limit = 0, synchronous = false
            }, lifetime.Token);
        }
        catch (TdException ex)
        {
            throw new IOException($"Ошибка TDLib downloadFile для file_id={fileId}: {ex.Message}", ex);
        }
        row.ApplyFile(file);
        NotifyMediaFileUpdate(file);
        if (!waitForCompletion) return "";
        MessengerStatus.Text = $"TDLib загружает файл {fileId}…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var completed = IsTdFileReady(file) ? file : await waiter!.Task.WaitAsync(timeout.Token);
            var path = completed.Field("local").Field("path").Text();
            if (!completed.Field("local").Field("is_downloading_completed").Flag())
                throw new IOException($"TDLib не завершила загрузку file_id={fileId}.");
            if (path.Length == 0) throw new IOException($"TDLib завершила загрузку file_id={fileId}, но local.path пуст.");
            if (!File.Exists(path)) throw new FileNotFoundException($"TDLib указала local.path, но файла нет на диске: {path}", path);
            row.ApplyFile(completed);
            MessengerStatus.Text = "";
            return path;
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"TDLib не завершила загрузку file_id={fileId} за 10 минут.");
        }
        finally
        {
            if (ownsWaiter && mediaDownloadWaiters.TryGetValue(fileId, out var current) && ReferenceEquals(current, waiter)) mediaDownloadWaiters.Remove(fileId);
        }
    }

    private async Task<(int FileId, JsonElement File)> ResolveMessageFileAsync(MessageRow row, int fileId, bool primary)
    {
        try
        {
            var current = await telegram!.RequestAsync(new { @type = "getFile", file_id = fileId }, lifetime.Token);
            return (fileId, current);
        }
        catch (TdException ex) when (ex.Code == 404 || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            JsonElement message;
            try
            {
                message = await telegram!.RequestAsync(new { @type = "getMessage", chat_id = row.ChatId, message_id = row.Id }, lifetime.Token);
            }
            catch (TdException refreshError)
            {
                throw new IOException($"TDLib не знает file_id={fileId}, и сообщение {row.Id} не удалось обновить: {refreshError.Message}", refreshError);
            }
            row.RefreshMedia(message.Field("content"));
            RegisterMessageFiles(row);
            var refreshedId = primary ? row.PrimaryFileId : row.PreviewFileId;
            if (refreshedId == 0)
                throw new IOException($"TDLib обновила сообщение {row.Id}, но не вернула актуальный {(primary ? "основной" : "предпросмотровый")} file_id.");
            try
            {
                var refreshed = await telegram!.RequestAsync(new { @type = "getFile", file_id = refreshedId }, lifetime.Token);
                return (refreshedId, refreshed);
            }
            catch (TdException refreshedError)
            {
                throw new IOException($"TDLib отклонила актуальный file_id={refreshedId} сообщения {row.Id}: {refreshedError.Message}", refreshedError);
            }
        }
    }

    private static bool IsTdFileReady(JsonElement file) => file.Field("local").Field("is_downloading_completed").Flag();

    private void NotifyMediaFileUpdate(JsonElement file)
    {
        var fileId = (int)file.Field("id").Number();
        if (fileId == 0 || !mediaDownloadWaiters.TryGetValue(fileId, out var waiter)) return;
        var local = file.Field("local");
        var expected = Math.Max(file.Field("expected_size").Number(), file.Field("size").Number());
        var downloaded = local.Field("downloaded_size").Number();
        if (local.Field("is_downloading_completed").Flag()) waiter.TrySetResult(file.Clone());
        else if (local.Field("is_downloading_active").Flag() && expected > 0)
            MessengerStatus.Text = $"Загрузка file_id={fileId}: {Math.Clamp(downloaded * 100d / expected, 0, 100):0}%";
        else if (!local.Field("can_be_downloaded").Flag() && !local.Field("is_downloading_active").Flag())
            waiter.TrySetException(new IOException($"TDLib не может скачать file_id={fileId}. local.path='{local.Field("path").Text()}'."));
    }

    private async void OpenMessageMedia(object sender, TappedRoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PrimaryFileId == 0) return;
        try
        {
            if (row.MediaPath.Length == 0) await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            if (row.MediaPath.Length == 0) throw new FileNotFoundException($"Файл file_id={row.PrimaryFileId} не готов.");
            if (row.TgsVisibility == Visibility.Visible) await OpenTgsViewerAsync(row);
            else if (row.MediaKind == "video" || row.InlineVideoVisibility == Visibility.Visible) await OpenVideoViewerAsync(row.MediaPath, row.FileName);
            else OpenImageViewer(row.MediaPath, row.FileName);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
        {
            MessengerStatus.Text = "Не удалось открыть медиа. " + ex.Message;
        }
    }

    private async void DownloadOrOpenDocument(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PrimaryFileId == 0) return;
        try
        {
            if (row.MediaPath.Length == 0) await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            if (row.MediaPath.Length == 0) throw new FileNotFoundException($"Файл file_id={row.PrimaryFileId} не готов.");
            var file = await StorageFile.GetFileFromPathAsync(row.MediaPath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            MessengerStatus.Text = "Не удалось открыть файл. " + ex.Message;
        }
    }

    private void OpenImageViewer(string path, string title)
    {
        var image = new Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.Uniform, Margin = new Thickness(24) };
        OpenMediaWindow(image, title.Length == 0 ? "Фотография" : title);
    }

    private async Task OpenVideoViewerAsync(string path, string title)
    {
        var source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
        var player = new MediaPlayer { Source = source, AutoPlay = true };
        var element = new MediaPlayerElement { AreTransportControlsEnabled = true, Stretch = Stretch.Uniform };
        element.SetMediaPlayer(player);
        var viewer = OpenMediaWindow(element, title.Length == 0 ? "Видео" : title);
        viewer.Closed += (_, _) => player.Dispose();
    }

    private async void PlayInlineVideo(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || sender is not Button playButton) return;
        var host = FindAncestor<Grid>(playButton);
        var playerElement = host?.Children.OfType<MediaPlayerElement>().FirstOrDefault();
        var preview = host?.Children.OfType<Image>().FirstOrDefault();
        if (playerElement is null) return;
        playButton.IsEnabled = false;
        try
        {
            var path = await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            var source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
            var player = new MediaPlayer { Source = source, AutoPlay = true };
            playerElement.SetMediaPlayer(player);
            playerElement.Visibility = Visibility.Visible;
            if (preview is not null) preview.Visibility = Visibility.Collapsed;
            playButton.Visibility = Visibility.Collapsed;
            player.MediaFailed += (_, eventArgs) => DispatcherQueue.TryEnqueue(() => MessengerStatus.Text = "Видео не воспроизводится. " + eventArgs.ErrorMessage);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Не удалось запустить видео. " + ex.Message;
        }
        finally { playButton.IsEnabled = true; }
    }

    private async void SaveOriginalVideo(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageRow row || row.PrimaryFileId == 0) return;
        try
        {
            var downloadedPath = await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            var picker = new FileSavePicker { SuggestedFileName = row.FileName.Length > 0 ? row.FileName : "video.mp4" };
            picker.FileTypeChoices.Add("Видео", new[] { Path.GetExtension(downloadedPath) is { Length: > 1 } extension ? extension : ".mp4" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            var source = await StorageFile.GetFileFromPathAsync(downloadedPath);
            await source.CopyAndReplaceAsync(destination);
            MessengerStatus.Text = "Оригинал сохранён.";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Не удалось сохранить видео. " + ex.Message;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private Window OpenMediaWindow(UIElement content, string title)
    {
        var viewer = new Window { Title = title, Content = content };
        mediaWindows.Add(viewer);
        viewer.Closed += (_, _) => mediaWindows.Remove(viewer);
        viewer.Activate();
        viewer.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        return viewer;
    }

    private async Task OpenTgsViewerAsync(MessageRow row)
    {
        var player = new AnimatedVisualPlayer { AutoPlay = true, Stretch = Stretch.Uniform };
        var viewer = OpenMediaWindow(player, "Анимированный стикер");
        await LoadTgsAsync(player, row);
        viewer.Closed += (_, _) => player.Stop();
    }

    private async Task LoadTgsAsync(AnimatedVisualPlayer player, MessageRow row)
    {
        if (row.MediaPath.Length == 0) await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
        if (row.MediaPath.Length == 0) return;
        var jsonPath = await ExpandTgsAsync(row.PrimaryFileId, row.MediaPath, lifetime.Token);
        var source = new LottieVisualSource();
        await source.SetSourceAsync(await StorageFile.GetFileFromPathAsync(jsonPath));
        player.Source = source;
        await player.PlayAsync(0, 1, true);
    }

    private static async Task<string> ExpandTgsAsync(int fileId, string sourcePath, CancellationToken token)
    {
        Directory.CreateDirectory(MediaCacheRoot);
        var destination = Path.Combine(MediaCacheRoot, fileId + ".json");
        if (File.Exists(destination)) return destination;
        await TgsCacheGate.WaitAsync(token);
        try
        {
            if (File.Exists(destination)) return destination;
            var temporary = destination + ".tmp";
            await using var source = File.OpenRead(sourcePath);
            await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            await using (var output = File.Create(temporary)) await gzip.CopyToAsync(output, token);
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { TgsCacheGate.Release(); }
    }

    private async void InlineVideoLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not MediaPlayerElement element || element.DataContext is not MessageRow row) return;
        try
        {
            if (row.MediaPath.Length == 0) await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            if (row.MediaPath.Length == 0) return;
            var file = await StorageFile.GetFileFromPathAsync(row.MediaPath);
            var player = new MediaPlayer
            {
                AutoPlay = true,
                IsMuted = true,
                IsLoopingEnabled = true
            };
            player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(element.DataContext, row)) return;
                if (FindAncestor<Grid>(element)?.Children.OfType<Image>().FirstOrDefault() is { } preview)
                    preview.Visibility = Visibility.Collapsed;
                player.Play();
            });
            player.MediaFailed += (_, eventArgs) => DispatcherQueue.TryEnqueue(() =>
                row.SetTransferError("Ошибка анимации: " + eventArgs.ErrorMessage));
            if (!ReferenceEquals(element.DataContext, row)) { player.Dispose(); return; }
            element.SetMediaPlayer(player);
            player.Source = MediaSource.CreateFromStorageFile(file);
            player.Play();
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) row.SetTransferError("Ошибка анимации: " + ex.Message);
        }
    }

    private void InlineVideoUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not MediaPlayerElement element) return;
        var player = element.MediaPlayer;
        element.SetMediaPlayer(null);
        player?.Dispose();
    }

    private async void InlineWebmLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not WebView2 web || web.DataContext is not MessageRow row) return;
        try
        {
            if (row.MediaPath.Length == 0) await DownloadMessageFileAsync(row, row.PrimaryFileId, true);
            if (row.MediaPath.Length == 0 || !ReferenceEquals(web.DataContext, row)) return;
            await web.EnsureCoreWebView2Async();
            var folder = Path.GetDirectoryName(row.MediaPath) ?? throw new IOException("Папка WEBM не найдена.");
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "glassgram.media", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            var fileName = Uri.EscapeDataString(Path.GetFileName(row.MediaPath));
            web.NavigationCompleted += async (_, completed) =>
            {
                if (!completed.IsSuccess || !ReferenceEquals(web.DataContext, row)) return;
                if (FindAncestor<Grid>(web)?.Children.OfType<Image>().FirstOrDefault() is { } preview)
                    preview.Visibility = Visibility.Collapsed;
                await web.ExecuteScriptAsync("document.querySelector('video')?.play()");
            };
            web.NavigateToString($$"""
                <!doctype html><html><head><meta charset="utf-8"><style>
                html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}
                video{width:100%;height:100%;object-fit:contain;background:transparent}
                </style></head><body><video src="https://glassgram.media/{{fileName}}"
                autoplay loop muted playsinline></video></body></html>
                """);
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!lifetime.IsCancellationRequested) row.SetTransferError("Ошибка WEBM: " + ex.Message);
        }
    }

    private void InlineWebmUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is WebView2 { CoreWebView2: { } core }) core.Navigate("about:blank");
    }

    // Viewport-driven playback follows Unigram's StickerContent/AnimatedImage lifecycle
    // (GPL-3.0, commit 9354bcb753be3b16086bd3886b8adec062ccdd91).
    private async void InlineWebmViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not WebView2 { CoreWebView2: { } } web) return;
        try
        {
            var visible = args.EffectiveViewport.Width > 0 && args.EffectiveViewport.Height > 0;
            await web.ExecuteScriptAsync(visible ? "document.querySelector('video')?.play()" : "document.querySelector('video')?.pause()");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private async void InlineTgsLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not AnimatedVisualPlayer player || player.DataContext is not MessageRow row) return;
        try
        {
            await LoadTgsAsync(player, row);
            if (!ReferenceEquals(player.DataContext, row)) { player.Stop(); return; }
            if (FindAncestor<Grid>(player)?.Children.OfType<Image>().FirstOrDefault() is { } preview)
                preview.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            if (!lifetime.IsCancellationRequested) row.SetTransferError("Ошибка анимации: " + ex.Message);
        }
    }

    private void InlineTgsUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is AnimatedVisualPlayer player) player.Stop();
    }

    private async void InlineTgsViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not AnimatedVisualPlayer player) return;
        if (args.EffectiveViewport.Width > 0 && args.EffectiveViewport.Height > 0) await player.PlayAsync(0, 1, true);
        else player.Pause();
    }

    private async void OpenMessageLink(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    private void JumpToLatest(object sender, RoutedEventArgs args) =>
        messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null, false);

    private async void PickAttachment(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        await SendFilesAsync(files);
    }

    private void ConversationDragOver(object sender, DragEventArgs args)
    {
        if (activeChat is not null && args.DataView.Contains(StandardDataFormats.StorageItems))
            args.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void ConversationDrop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var files = (await args.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
        await SendFilesAsync(files);
    }

    private async void MessageInputPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        HandleComposerShortcut(args);
        if (args.Handled) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl || args.Key != Windows.System.VirtualKey.V) return;
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return;
        args.Handled = true;
        try
        {
            Directory.CreateDirectory(MediaCacheRoot);
            var path = Path.Combine(MediaCacheRoot, "clipboard-" + Guid.NewGuid().ToString("N") + ".png");
            var bitmap = await content.GetBitmapAsync();
            using var input = (await bitmap.OpenReadAsync()).AsStreamForRead();
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, lifetime.Token);
                await output.FlushAsync(lifetime.Token);
            }
            if (new FileInfo(path).Length == 0) throw new IOException("Буфер обмена вернул пустое изображение.");
            await using (var verification = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { _ = verification.Length; }
            await SendAttachmentAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessengerStatus.Text = "Не удалось вставить изображение. " + ex.Message;
        }
    }

    private async Task SendFilesAsync(IEnumerable<StorageFile> files)
    {
        foreach (var file in files) await SendAttachmentAsync(file.Path);
    }

    private async Task SendAttachmentAsync(string path)
    {
        if (telegram is null || activeChat is null || !File.Exists(path)) return;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var isPhoto = extension is ".jpg" or ".jpeg" or ".png" or ".webp";
        var isVideo = extension is ".mp4" or ".mov" or ".mkv" or ".webm";
        if (isPhoto && !activeChat.CanSendPhotos || isVideo && !activeChat.CanSendVideos || !isPhoto && !isVideo && !activeChat.CanSendDocuments)
        {
            MessengerStatus.Text = "В этом чате нельзя отправить выбранный тип файла.";
            return;
        }

        var inputFile = new Dictionary<string, object?> { ["@type"] = "inputFileLocal", ["path"] = path };
        var formattedText = new Dictionary<string, object?> { ["@type"] = "formattedText", ["text"] = "", ["entities"] = Array.Empty<object>() };
        Dictionary<string, object?> content;
        if (isPhoto)
        {
            content = new()
            {
                ["@type"] = "inputMessagePhoto",
                ["photo"] = new Dictionary<string, object?> { ["@type"] = "inputPhoto", ["photo"] = inputFile, ["thumbnail"] = null, ["video"] = null, ["added_sticker_file_ids"] = Array.Empty<int>(), ["width"] = 0, ["height"] = 0 },
                ["caption"] = formattedText, ["show_caption_above_media"] = false, ["self_destruct_type"] = null, ["has_spoiler"] = false
            };
        }
        else if (isVideo)
        {
            content = new()
            {
                ["@type"] = "inputMessageVideo",
                ["video"] = new Dictionary<string, object?> { ["@type"] = "inputVideo", ["video"] = inputFile, ["thumbnail"] = null, ["cover"] = null, ["start_timestamp"] = 0, ["added_sticker_file_ids"] = Array.Empty<int>(), ["duration"] = 0, ["width"] = 0, ["height"] = 0, ["supports_streaming"] = true },
                ["caption"] = formattedText, ["show_caption_above_media"] = false, ["self_destruct_type"] = null, ["has_spoiler"] = false
            };
        }
        else
        {
            content = new()
            {
                ["@type"] = "inputMessageDocument",
                ["document"] = new Dictionary<string, object?> { ["@type"] = "inputDocument", ["document"] = inputFile, ["thumbnail"] = null, ["disable_content_type_detection"] = false },
                ["caption"] = formattedText
            };
        }

        MessengerStatus.Text = "Отправляем " + Path.GetFileName(path) + "…";
        try
        {
            var message = await telegram.RequestAsync(new
            {
                @type = "sendMessage", chat_id = activeChat.Id, topic_id = (object?)null, reply_to = (object?)null,
                options = (object?)null, reply_markup = (object?)null, input_message_content = content
            }, lifetime.Token);
            InsertMessage(message);
            MessengerStatus.Text = "";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) MessengerStatus.Text = "Файл не отправлен. " + ex.Message;
        }
    }
}
