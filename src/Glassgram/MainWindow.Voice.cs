// Voice-note interaction and waveform behavior adapted from Unigram (GPL-3.0),
// commit 9354bcb753be3b16086bd3886b8adec062ccdd91. Ogg/Opus encoding uses Concentus.
using Glassgram.Telegram;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Media.Playback;
using Windows.Storage;
using NAudio.Wave;

namespace Glassgram;

public partial class MainWindow
{
    private WaveInEvent? voiceCapture;
    private MemoryStream? voicePcm;
    private DateTime voiceStarted;
    private long voiceChatId;
    private object? voiceTopicId;
    private bool voiceRecording;
    private readonly DispatcherTimer voiceRecordTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<ContentControl, VoicePlayback> voiceControls = [];
    private VoicePlayback? activeVoice;

    private async void ToggleVoiceRecording(object sender, RoutedEventArgs args)
    {
        if (voiceRecording) { await FinishVoiceRecordingAsync(true); return; }
        if (telegram is null || activeChat is null) return;
        try
        {
            voicePcm = new MemoryStream();
            voiceCapture = new WaveInEvent { WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 40, NumberOfBuffers = 3 };
            voiceCapture.DataAvailable += (_, data) => voicePcm?.Write(data.Buffer, 0, data.BytesRecorded);
            voiceCapture.StartRecording();
            voiceRecording = true;
            voiceChatId = ActiveSendChatId;
            voiceTopicId = ActiveTopicId;
            voiceStarted = DateTime.UtcNow;
            VoiceRecordingPanel.Visibility = Visibility.Visible;
            MessengerStatus.Visibility = Visibility.Collapsed;
            VoiceRecordIcon.Glyph = "\uE71A";
            voiceRecordTimer.Tick -= VoiceRecordTimerTick;
            voiceRecordTimer.Tick += VoiceRecordTimerTick;
            voiceRecordTimer.Start();
            VoiceRecordTimerTick(null, null!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or COMException)
        {
            ResetVoiceRecordingUi();
            MessengerStatus.Text = "Не удалось начать запись. Проверьте доступ к микрофону. " + ex.Message;
        }
    }

    private void VoiceRecordTimerTick(object? sender, object args)
    {
        var elapsed = DateTime.UtcNow - voiceStarted;
        VoiceRecordTime.Text = "● " + FormatDuration(elapsed);
    }

    private async void CancelVoiceRecording(object sender, RoutedEventArgs args) => await FinishVoiceRecordingAsync(false);

    private async Task FinishVoiceRecordingAsync(bool send)
    {
        if (!voiceRecording || voiceCapture is null || voicePcm is null) return;
        var capture = voiceCapture;
        var pcm = voicePcm;
        var chatId = voiceChatId;
        voiceRecording = false;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RecordingStopped += (_, error) => { if (error.Exception is null) stopped.TrySetResult(); else stopped.TrySetException(error.Exception); };
        try { capture.StopRecording(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException) { send = false; MessengerStatus.Text = "Запись не завершена. " + ex.Message; }
        capture.Dispose();
        voiceCapture = null;
        voicePcm = null;
        ResetVoiceRecordingUi();
        if (!send) { pcm.Dispose(); return; }
        try
        {
            var bytes = pcm.ToArray(); pcm.Dispose();
            var samples = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            var voiceFolder = Path.Combine(Path.GetTempPath(), "Glassgram", "Voice"); Directory.CreateDirectory(voiceFolder);
            var opusPath = Path.Combine(voiceFolder, $"voice-{Guid.NewGuid():N}.ogg");
            var voiceFile = VoiceNotePipeline.Encode(opusPath, samples);
            var waveform = BuildTelegramWaveform(samples);
            var result = await telegram!.RequestAsync(VoiceNotePipeline.BuildSendRequest(chatId, voiceFile, waveform, voiceTopicId), lifetime.Token);
            var sent = MessageRow.FromJson(result);
            if (activeHistoryContext is not null) InsertContextMessage(sent); else InsertMessage(result);
            MessengerStatus.Text = "";
        }
        catch (Exception ex) when (ex is TdException or OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessengerStatus.Text = "Голосовое сообщение не отправлено. " + ex.Message;
        }
    }

    private void ResetVoiceRecordingUi()
    {
        voiceRecordTimer.Stop();
        VoiceRecordingPanel.Visibility = Visibility.Collapsed;
        MessengerStatus.Visibility = Visibility.Visible;
        VoiceRecordIcon.Glyph = "\uE720";
    }

    private async void VoiceControlLoaded(object sender, RoutedEventArgs args) => await ConfigureVoiceControlAsync(sender as ContentControl);
    private async void VoiceControlContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => await ConfigureVoiceControlAsync(sender as ContentControl);
    private void VoiceControlUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ContentControl control && voiceControls.Remove(control, out var playback)) playback.Dispose();
    }

    private async Task ConfigureVoiceControlAsync(ContentControl? host)
    {
        if (host?.DataContext is not MessageRow row || row.MediaKind != "voice") return;
        if (voiceControls.Remove(host, out var old)) old.Dispose();
        var play = new Button { Content = "▶", Width = 38, Height = 38, Padding = new Thickness(0), CornerRadius = new CornerRadius(19) };
        var slider = new Slider { Minimum = 0, Maximum = Math.Max(1, row.VoiceDuration), VerticalAlignment = VerticalAlignment.Center };
        var duration = new TextBlock { Text = FormatDuration(TimeSpan.FromSeconds(row.VoiceDuration)), VerticalAlignment = VerticalAlignment.Center, MinWidth = 42 };
        var waveform = new Grid { Height = 30, MinWidth = 180 };
        waveform.Children.Add(BuildWaveformCanvas(row.VoiceWaveform));
        waveform.Children.Add(slider);
        var grid = new Grid { ColumnSpacing = 8, MinWidth = 240 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(play); Grid.SetColumn(waveform, 1); grid.Children.Add(waveform); Grid.SetColumn(duration, 2); grid.Children.Add(duration);
        host.Content = grid;
        var state = new VoicePlayback(this, row, host, play, slider, duration);
        voiceControls[host] = state;
        play.Click += async (_, _) => await state.ToggleAsync();
        slider.ValueChanged += (_, _) => state.SeekFromUi();
        if (row.PreviewFileId != 0 && row.PreviewPath.Length == 0) await DownloadMessageFileAsync(row, row.PreviewFileId, false);
    }

    private sealed class VoicePlayback : IDisposable
    {
        private readonly MainWindow owner; private readonly MessageRow row; private readonly Button play; private readonly Slider slider; private readonly TextBlock duration;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) }; private MediaPlayer? player; private bool updating;
        public VoicePlayback(MainWindow owner, MessageRow row, ContentControl host, Button play, Slider slider, TextBlock duration) { this.owner = owner; this.row = row; this.play = play; this.slider = slider; this.duration = duration; timer.Tick += Tick; }
        public async Task ToggleAsync()
        {
            if (player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) { player.Pause(); play.Content = "▶"; timer.Stop(); return; }
            if (owner.activeVoice != this) { owner.activeVoice?.Stop(); owner.activeVoice = this; }
            if (player is null)
            {
                var path = await owner.DownloadMessageFileAsync(row, row.PrimaryFileId, true);
                player = new MediaPlayer { Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path)) };
                player.MediaEnded += (_, _) => owner.DispatcherQueue.TryEnqueue(Stop);
            }
            player.Play(); play.Content = "❚❚"; timer.Start();
        }
        public void SeekFromUi() { if (updating || player is null) return; player.PlaybackSession.Position = TimeSpan.FromSeconds(slider.Value); }
        private void Tick(object? sender, object args) { if (player is null) return; updating = true; slider.Maximum = Math.Max(1, player.PlaybackSession.NaturalDuration.TotalSeconds); slider.Value = player.PlaybackSession.Position.TotalSeconds; duration.Text = FormatDuration(player.PlaybackSession.Position) + " / " + FormatDuration(player.PlaybackSession.NaturalDuration); updating = false; }
        public void Stop() { timer.Stop(); player?.Pause(); if (player is not null) player.PlaybackSession.Position = TimeSpan.Zero; play.Content = "▶"; slider.Value = 0; }
        public void Dispose() { Stop(); player?.Dispose(); player = null; }
    }

    private static string BuildTelegramWaveform(short[] samples)
    {
        const int count = 100; var peaks = new byte[count]; var block = Math.Max(1, samples.Length / count); double max = 1;
        var values = new double[count]; for (var i = 0; i < count; i++) { double sum = 0; var end = Math.Min(samples.Length, (i + 1) * block); for (var j = i * block; j < end; j++) sum += (double)samples[j] * samples[j]; values[i] = Math.Sqrt(sum / Math.Max(1, end - i * block)); max = Math.Max(max, values[i]); }
        for (var i = 0; i < count; i++) peaks[i] = (byte)Math.Clamp((int)Math.Round(values[i] / max * 31), 0, 31);
        var packed = new byte[(count * 5 + 7) / 8]; var bit = 0; foreach (var peak in peaks) for (var b = 0; b < 5; b++, bit++) if ((peak & (1 << b)) != 0) packed[bit / 8] |= (byte)(1 << (bit % 8));
        return Convert.ToBase64String(packed);
    }

    private static Canvas BuildWaveformCanvas(string encoded)
    {
        var canvas = new Canvas { Width = 180, Height = 30, IsHitTestVisible = false, Opacity = .72 };
        byte[] packed;
        try { packed = encoded.Length > 0 ? Convert.FromBase64String(encoded) : []; }
        catch (FormatException) { packed = []; }
        var count = packed.Length > 0 ? Math.Min(100, packed.Length * 8 / 5) : 64;
        for (var i = 0; i < count; i++)
        {
            var bit = i * 5; var value = 0;
            for (var b = 0; b < 5 && bit + b < packed.Length * 8; b++)
                if ((packed[(bit + b) / 8] & (1 << ((bit + b) % 8))) != 0) value |= 1 << b;
            if (packed.Length == 0) value = 8 + i % 5;
            var height = Math.Max(3, value / 31d * 26);
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = Math.Max(1, 180d / count - 1), Height = height, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 130, 220, 255)) };
            Canvas.SetLeft(bar, i * 180d / count); Canvas.SetTop(bar, (30 - height) / 2); canvas.Children.Add(bar);
        }
        return canvas;
    }
    private static string FormatDuration(TimeSpan value) => $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";
}
