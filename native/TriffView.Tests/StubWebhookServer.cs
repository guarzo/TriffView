using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TriffView.Tests;

/// <summary>
/// Loopback HTTP endpoint standing in for a Discord webhook. Captures the
/// last request's content type and body and replies with a caller-set status,
/// so both the webhook-test path and the upload path can be exercised
/// end-to-end without reaching a real Discord channel.
/// </summary>
internal sealed class StubWebhookServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private volatile bool _stopped;

    public Uri Uri { get; }
    public int StatusCode { get; set; } = 204;
    public string ResponseBody { get; set; } = "";
    public int RequestCount { get; private set; }
    public string? LastContentType { get; private set; }
    public string? LastRequestBody { get; private set; }

    /// <summary>
    /// The raw bytes of the last request body. This is the authoritative
    /// capture; <see cref="LastRequestBody"/> is a UTF-8 decoding of it kept
    /// only as a convenience for the JSON-only paths (webhook config, test
    /// pings) -- a zip upload's body will not survive a round trip through
    /// UTF-8 string decoding, so anything comparing bytes byte-for-byte must
    /// read this property instead.
    /// </summary>
    public byte[]? LastRequestBytes { get; private set; }

    public StubWebhookServer()
    {
        var port = GetFreeTcpPort();
        // Path shape matches the real allowlist so a webhook URL built from
        // this Uri would also pass DiscordWebhook.TryParse if it ever needed to.
        Uri = new Uri($"http://127.0.0.1:{port}/api/webhooks/1/token");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopped)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stopped)
            {
                return;
            }

            // The input stream can only be read once, so capture the raw
            // bytes here and derive the string form from them rather than
            // taking a separate StreamReader pass over the same stream.
            using (var buffer = new MemoryStream())
            {
                await context.Request.InputStream.CopyToAsync(buffer);
                LastRequestBytes = buffer.ToArray();
            }
            LastRequestBody = Encoding.UTF8.GetString(LastRequestBytes);
            LastContentType = context.Request.ContentType;
            RequestCount++;

            context.Response.StatusCode = StatusCode;
            var bytes = Encoding.UTF8.GetBytes(ResponseBody);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        _listener.Close();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* best-effort shutdown of a test double */ }
    }
}
