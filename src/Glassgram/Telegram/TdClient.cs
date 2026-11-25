using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Glassgram.Telegram;

internal sealed class TdClient : IDisposable
{
    private readonly IntPtr client = Native.td_json_client_create();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task receiver;
    private long requestId;
    private bool disposed;

    public event Action<JsonElement>? Update;

    public TdClient()
    {
        if (client == IntPtr.Zero) throw new InvalidOperationException("TDLib не смогла создать клиент.");
        receiver = Task.Run(ReceiveLoop);
    }

    public async Task<JsonElement> RequestAsync(object request, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Interlocked.Increment(ref requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(45));
            Send(request, id);
            var result = await completion.Task.WaitAsync(linked.Token);
            if (result.GetProperty("@type").GetString() == "error")
                throw new TdException((int)result.Field("code").Number(), result.GetProperty("message").GetString() ?? "Ошибка TDLib");
            return result;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private void Send(object request, long? id = null)
    {
        var json = JsonSerializer.Serialize(request);
        using var doc = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>();
        foreach (var property in doc.RootElement.EnumerateObject())
            values[property.Name == "type" ? "@type" : property.Name] = property.Value.Clone();
        if (id.HasValue) values["@extra"] = JsonSerializer.SerializeToElement(id.Value);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values) + "\0");
        Native.td_json_client_send(client, bytes);
    }

    private void ReceiveLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            var pointer = Native.td_json_client_receive(client, 0.25);
            if (pointer == IntPtr.Zero) continue;
            try
            {
                var json = Marshal.PtrToStringUTF8(pointer);
                if (json is null) continue;
                using var document = JsonDocument.Parse(json);
                var result = document.RootElement.Clone();
                if (result.TryGetProperty("@extra", out var extra) && extra.TryGetInt64(out var id))
                {
                    if (pending.TryRemove(id, out var completion)) completion.TrySetResult(result);
                }
                else Update?.Invoke(result);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A malformed update should not terminate the receiver.
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        receiver.GetAwaiter().GetResult();
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        pending.Clear();
        Native.td_json_client_destroy(client);
        cancellation.Dispose();
    }

    private static class Native
    {
        [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr td_json_client_create();

        [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void td_json_client_send(IntPtr client, byte[] request);

        [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void td_json_client_destroy(IntPtr client);
    }
}

internal sealed class TdException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
