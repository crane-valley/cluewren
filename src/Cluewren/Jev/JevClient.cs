using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Jev;

public sealed class JevException(string message) : Exception(message);

public interface IJevRelay
{
    Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct);
}

public sealed record JevOptions(string ApiKey)
{
    // By exact id, as upstream: play only changes when this does, and jev-latest moves when TypeSafe ships one.
    public const string DefaultModel = "jev-1.13.0";

    public Uri BaseUrl { get; init; } = new("https://api.typesafe.ai");
    public string Model { get; init; } = DefaultModel;
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan WarmEvery { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WarmTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class JevClient : IDecider, IJevRelay, IAsyncDisposable
{
    const int MaxAttempts = 2;

    readonly JevOptions _options;
    readonly HttpClient _http;
    readonly CancellationTokenSource _closing = new();
    readonly Task _warm;
    long _lastUsed;

    public JevClient(JevOptions options)
    {
        _options = options;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(120),
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = ConnectToFastestAsync,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            BaseAddress = options.BaseUrl,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _warm = KeepWarmAsync();
    }

    public async Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
    {
        var (body, latency) = await PostAsync(request.Encode(_options.Model), ct).ConfigureAwait(false);
        return JevDecision.Parse(body, latency);
    }

    public Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", _options.Model);
            foreach (var field in stateAndQuestions.EnumerateObject())
            {
                if (field.Name != "model")
                    field.WriteTo(json);
            }
            json.WriteEndObject();
        }
        return PostAsync(buffer.WrittenSpan.ToArray(), ct);
    }

    async Task<(byte[] Body, long LatencyTicks)> PostAsync(byte[] body, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(_options.TotalTimeout);
        for (var attempt = 1; ; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            limit.CancelAfter(_options.AttemptTimeout);
            string failure;
            try
            {
                // A hand-made message doesn't take the client's default version: on 1.1 it would miss the connection the warm
                // calls keep open, and the run's first decision would wait on a new one (over a second when DNS is slow).
                using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone")
                {
                    Version = _http.DefaultRequestVersion,
                    VersionPolicy = _http.DefaultVersionPolicy,
                    Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
                };
                if (attempt > 1)
                    message.Headers.Add("X-TypeSafe-Retry-Count", (attempt - 1).ToString());
                Volatile.Write(ref _lastUsed, Stopwatch.GetTimestamp());
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, limit.Token)
                    .ConfigureAwait(false);
                var answer = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (answer, Stopwatch.GetTimestamp() - started);
                failure = $"HTTP {(int)response.StatusCode}";
                if (!Retryable(response.StatusCode))
                    throw new JevException(failure);
            }
            catch (HttpRequestException e)
            {
                failure = e.Message;
            }
            catch (OperationCanceledException) when (!total.IsCancellationRequested)
            {
                failure = "the attempt timed out";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new JevException("no answer in time");
            }

            if (attempt >= MaxAttempts)
                throw new JevException(failure);
            try
            {
                await Task.Delay(_options.RetryDelay, total.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new JevException("no answer in time");
            }
        }
    }

    // Addresses are raced, not tried in order: on a network that drops IPv6 silently, the default order spends the whole
    // decision timeout on addresses that never answer, measured at about 15 s each.
    static async ValueTask<Stream> ConnectToFastestAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = addresses.Select(address => ConnectAsync(address, context.DnsEndPoint.Port, race.Token)).ToList();
        Exception? failure = null;
        Socket? winner = null;
        var left = attempts.ToList();
        while (left.Count > 0 && winner is null)
        {
            var finished = await Task.WhenAny(left).ConfigureAwait(false);
            left.Remove(finished);
            if (finished.IsCompletedSuccessfully)
                winner = finished.Result;
            else
                failure = finished.Exception?.InnerException ?? failure;
        }
        await race.CancelAsync().ConfigureAwait(false);
        foreach (var loser in attempts.Where(a => a.IsCompletedSuccessfully && a.Result != winner))
            loser.Result.Dispose();
        return winner is null
            ? throw failure ?? new SocketException((int)SocketError.HostNotFound)
            : new NetworkStream(winner, ownsSocket: true);
    }

    static async Task<Socket> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static bool Retryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    // A decision after an idle stretch, a dodge say, must not wait on a new connection or a server waking.
    async Task KeepWarmAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUsed)) < _options.WarmEvery)
                    continue;
                Volatile.Write(ref _lastUsed, Stopwatch.GetTimestamp());
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                limit.CancelAfter(_options.WarmTimeout);
                try
                {
                    using var response = await _http.GetAsync("/v1/models", limit.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
                {
                }
            } while (await timer.WaitForNextTickAsync(_closing.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        await _warm.ConfigureAwait(false);
        _closing.Dispose();
        _http.Dispose();
    }
}
