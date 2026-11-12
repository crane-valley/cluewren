using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cluewren.Jev;
using Cluewren.Kit;

namespace Cluewren.Tests;

public sealed class FakeJev : IAsyncDisposable
{
    readonly WebApplication _app;
    public readonly List<string> Bodies = [];
    public readonly List<string?> Authorizations = [];
    public int ModelCalls;
    public Func<int, (int Status, string Body, TimeSpan Delay)> Answer = _ => (200, Good, TimeSpan.Zero);

    public const string Good = """
        {"model":"jev-1.13.0","answers":{"pick":{"type":"choice","choice":"a","confidence":0.9,
        "probabilities":{"a":0.9,"b":0.1}}},"usage":{"input_tokens":1000,"output_tokens":3}}
        """;

    FakeJev(WebApplication app) => _app = app;

    public Uri Url { get; private set; } = null!;

    public static async Task<FakeJev> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new FakeJev(app);
        app.MapPost("/v1/systemone", fake.HandleAsync);
        app.MapGet("/v1/models", () =>
        {
            Interlocked.Increment(ref fake.ModelCalls);
            return "{}";
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Url = new Uri(address);
        return fake;
    }

    async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        int call;
        lock (Bodies)
        {
            Bodies.Add(body);
            Authorizations.Add(context.Request.Headers.Authorization);
            call = Bodies.Count;
        }
        var (status, answer, delay) = Answer(call);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, context.RequestAborted);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(answer);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class JevClientTests
{
    static readonly JevRequest Request = new("Open the gate", JevRequest.DefaultInstructions,
        new Dictionary<string, object?> { ["health"] = 80 },
        [
            new OfferedOption("a", "pull the lever", new Dictionary<string, object?> { ["distance"] = 3.5 }),
            new OfferedOption("b", "run away", new Dictionary<string, object?>()),
        ],
        new NowDoing("b", 1.26), [new RecentOption("a", 2.04, false, "stuck")]);

    static JevClient Client(FakeJev fake, Func<JevOptions, JevOptions>? change = null)
    {
        var options = new JevOptions("test-key") { BaseUrl = fake.Url };
        return new JevClient(change?.Invoke(options) ?? options);
    }

    [Fact]
    public async Task Sends_upstreams_request_shape_with_bearer()
    {
        await using var fake = await FakeJev.StartAsync();
        await using var client = Client(fake);
        await client.DecideAsync(Request, default);

        Assert.Equal("Bearer test-key", fake.Authorizations[0]);
        using var body = JsonDocument.Parse(fake.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal(JevOptions.DefaultModel, root.GetProperty("model").GetString());
        var state = root.GetProperty("state");
        Assert.Equal("Open the gate", state.GetProperty("goal").GetString());
        Assert.Equal(80, state.GetProperty("situation").GetProperty("health").GetInt32());
        Assert.Equal(3.5, state.GetProperty("options").GetProperty("a").GetProperty("distance").GetDouble());
        Assert.Equal("b", state.GetProperty("now_doing").GetProperty("option").GetString());
        Assert.Equal(1.3, state.GetProperty("now_doing").GetProperty("for_seconds").GetDouble());
        Assert.False(state.GetProperty("recent")[0].GetProperty("worked").GetBoolean());
        var pick = root.GetProperty("questions").GetProperty("pick");
        Assert.Equal("choice", pick.GetProperty("type").GetString());
        Assert.Equal("pull the lever", pick.GetProperty("criteria").GetProperty("a").GetString());
    }

    [Fact]
    public async Task Parses_probabilities_and_usage()
    {
        await using var fake = await FakeJev.StartAsync();
        await using var client = Client(fake);
        var decision = await client.DecideAsync(Request, default);
        Assert.Equal(0.9, decision.Probabilities["a"]);
        Assert.Equal(1000, decision.InputTokens);
        Assert.True(decision.LatencyTicks > 0);
    }

    [Fact]
    public async Task A_choice_without_probabilities_counts_as_certain()
    {
        await using var fake = await FakeJev.StartAsync();
        fake.Answer = _ => (200, """{"answers":{"pick":{"type":"choice","choice":"b"}},"usage":{}}""", TimeSpan.Zero);
        await using var client = Client(fake);
        var decision = await client.DecideAsync(Request, default);
        Assert.Equal(1, decision.Probabilities["b"]);
    }

    [Fact]
    public async Task Reaches_a_host_when_one_of_its_addresses_does_not_answer()
    {
        await using var fake = await FakeJev.StartAsync();
        var dualStackName = new UriBuilder(fake.Url) { Host = "localhost" }.Uri;
        // Not the decision deadline: a runner busy starting the other tests has stalled this first call past it.
        var patience = TimeSpan.FromMinutes(1);
        await using var client = Client(fake, options => options with
        {
            BaseUrl = dualStackName, AttemptTimeout = patience, TotalTimeout = patience,
        });
        var decision = await client.DecideAsync(Request, default);
        Assert.Equal(0.9, decision.Probabilities["a"]);
    }

    [Fact]
    public async Task Retries_a_server_error_once()
    {
        await using var fake = await FakeJev.StartAsync();
        fake.Answer = call => call == 1 ? (503, "{}", TimeSpan.Zero) : (200, FakeJev.Good, TimeSpan.Zero);
        await using var client = Client(fake);
        await client.DecideAsync(Request, default);
        Assert.Equal(2, fake.Bodies.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_bad_request()
    {
        await using var fake = await FakeJev.StartAsync();
        fake.Answer = _ => (401, "{}", TimeSpan.Zero);
        await using var client = Client(fake);
        await Assert.ThrowsAsync<JevException>(() => client.DecideAsync(Request, default));
        Assert.Single(fake.Bodies);
    }

    [Fact]
    public async Task Gives_up_within_the_total_timeout()
    {
        await using var fake = await FakeJev.StartAsync();
        fake.Answer = _ => (200, FakeJev.Good, TimeSpan.FromSeconds(5));
        await using var client = Client(fake, options => options with
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(100),
            TotalTimeout = TimeSpan.FromMilliseconds(250),
        });
        await Assert.ThrowsAsync<JevException>(() => client.DecideAsync(Request, default));
    }

    [Fact]
    public async Task Keeps_warm_while_idle()
    {
        await using var fake = await FakeJev.StartAsync();
        await using var client = Client(fake, options => options with { WarmEvery = TimeSpan.Zero });
        for (var i = 0; i < 300 && Volatile.Read(ref fake.ModelCalls) == 0; i++)
            await Task.Delay(10);
        Assert.True(fake.ModelCalls > 0);
    }

    [Fact]
    public void Key_comes_from_the_keys_file_when_the_environment_has_none()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "# comment\nOTHER=1\nPLAYTEST_TEST_KEY = abc \n");
            Assert.Equal("abc", ApiKeys.Find("PLAYTEST_TEST_KEY", file));
            Assert.Null(ApiKeys.Find("PLAYTEST_TEST_MISSING", file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
