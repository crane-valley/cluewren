using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Bridge;

namespace Cluewren.Mcp;

// Codex starts a `cluewren mcp` of its own for a subagent, and the frame describer is one: its `frames` call reaches a
// process with no run. The process that has the run answers it over a pipe only this user can open, named after the
// folder both were started in. Frames still never touch the disk.
public sealed class FramesRelay : IAsyncDisposable
{
    const int MaxRequestBytes = 64 * 1024;
    static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(2);
    static readonly TimeSpan AnswerPatience = TimeSpan.FromSeconds(60);

    readonly CancellationTokenSource _closing = new();
    readonly Task _serving;

    FramesRelay(string name, Func<JsonObject, Task<ToolResult>> frames) => _serving = ServeAsync(name, frames, _closing.Token);

    // By what the folder is, not how it is spelled: two processes may spell one folder differently, and two folders
    // may differ only in case.
    public static string NameFor(string workingFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder));
        var named = ProjectScope.KeyOf(folder) ?? folder.ToLowerInvariant();
        return "cluewren-frames-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(named)))[..32];
    }

    public static FramesRelay Serve(string workingFolder, Func<JsonObject, Task<ToolResult>> frames) =>
        new(NameFor(workingFolder), frames);

    static async Task ServeAsync(string name, Func<JsonObject, Task<ToolResult>> frames, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(AnswerPatience);
                var line = await ReadLineAsync(pipe, MaxRequestBytes, limit.Token).ConfigureAwait(false);
                var args = line is null ? null : JsonNode.Parse(line) as JsonObject;
                // The run may be closing while this waits for it, and closing waits for this loop: the wait ends with it.
                var result = args is null ? ToolResult.Say("Not a frames request.", true)
                    : await frames(args).WaitAsync(limit.Token).ConfigureAwait(false);
                var answer = new JsonObject
                {
                    ["text"] = result.Text, ["error"] = result.Error,
                    ["images"] = new JsonArray(result.Images.Select(jpeg => (JsonNode)Convert.ToBase64String(jpeg)).ToArray()),
                };
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n"), limit.Token).ConfigureAwait(false);
                await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            // Another session's run in the same folder holds the name: this run's frames stay reachable in its own process.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception e) when (e is JsonException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    public static async Task<ToolResult?> AskAsync(string workingFolder, JsonObject args, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", NameFor(workingFolder), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(ConnectPatience, ct).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(AnswerPatience);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(args.ToJsonString() + "\n"), limit.Token).ConfigureAwait(false);
            await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, int.MaxValue, limit.Token).ConfigureAwait(false);
            if (line is null || JsonNode.Parse(line) is not JsonObject answer)
                return null;
            var images = (answer["images"] as JsonArray ?? []).Select(image => Convert.FromBase64String(image!.GetValue<string>())).ToList();
            return new ToolResult(answer["text"]?.GetValue<string>() ?? "", images, answer["error"]?.GetValue<bool>() ?? false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException
                                      or FormatException or OperationCanceledException)
        {
            return null;
        }
    }

    static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var kept = new MemoryStream();
        var chunk = new byte[1 << 16];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                return kept.Length == 0 ? null : Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
            var end = Array.IndexOf(chunk, (byte)'\n', 0, read);
            kept.Write(chunk, 0, end < 0 ? read : end);
            if (end >= 0)
                return Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
            if (kept.Length > maxBytes)
                return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        await _serving.ConfigureAwait(false);
        _closing.Dispose();
    }
}
