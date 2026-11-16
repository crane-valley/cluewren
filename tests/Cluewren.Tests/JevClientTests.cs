using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cluewren.Decisions;
using Cluewren.Kit;

namespace Cluewren.Tests;

public sealed class FakeJev : IAsyncDisposable
{
    readonly WebApplication _app;
    public readonly List<string> Bodies = [];
    public readonly List<string?> Authorizations = [];
    public int ModelCalls;
    public int Elsewhere;
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
        app.MapPost("/v1/elsewhere", () =>
        {
            Interlocked.Increment(ref fake.Elsewhere);
            return Good;
        });
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
        if (status is >= 300 and < 400)
            context.Response.Headers.Location = "/v1/elsewhere";
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
    static readonly DecisionRequest Request = new("Open the gate", DecisionRequest.DefaultInstructions,
        new Dictionary<string, object?> { ["health"] = 80 },
        [
            new OfferedOption("a", "pull the lever", new Dictionary<string, object?> { ["distance"] = 3.5 }),
            new OfferedOption("b", "run away", new Dictionary<string, object?>()),
        ],
        new NowDoing("b", 1.26), [new RecentOption("a", 2.04, false, "stuck")]);

    // Not the decision deadline: a machine busy with other work has stalled a first call past it.
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(1);

    static JevClient Client(FakeJev fake, Func<JevOptions, JevOptions>? change = null)
    {
        var options = new JevOptions("test-key") { BaseUrl = fake.Url, AttemptTimeout = Patience, TotalTimeout = Patience };
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
        await using var client = Client(fake, options => options with { BaseUrl = dualStackName });
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
        await Assert.ThrowsAsync<DecisionException>(() => client.DecideAsync(Request, default));
        Assert.Single(fake.Bodies);
    }

    // The key goes with every request: a redirect followed would hand it to wherever the answer points.
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_is_not_followed(int status)
    {
        await using var fake = await FakeJev.StartAsync();
        fake.Answer = _ => (status, "{}", TimeSpan.Zero);
        await using var client = Client(fake);
        var refused = await Assert.ThrowsAsync<DecisionException>(() => client.DecideAsync(Request, default));
        Assert.Equal($"HTTP {status}", refused.Message);
        Assert.Single(fake.Bodies);
        Assert.Equal(0, fake.Elsewhere);
    }

    static Func<string, string?> Settings(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(setting => setting.Name == name).Value;

    [Fact]
    public void Settings_that_are_not_set_leave_the_default_model_and_address()
    {
        var options = JevOptions.FromSettings("key", Settings());
        Assert.Equal(new JevOptions("key"), options);
        Assert.Equal(("key", JevOptions.DefaultModel, new Uri("https://api.typesafe.ai")), (options.ApiKey, options.Model, options.BaseUrl));
    }

    [Fact]
    public async Task The_model_setting_is_the_model_asked()
    {
        var options = JevOptions.FromSettings("key", Settings((ApiKeys.TypeSafeModel, "jev-latest")));
        Assert.Equal(new JevOptions("key") { Model = "jev-latest" }, options);
        await using var fake = await FakeJev.StartAsync();
        await using var client = new JevClient(options with { BaseUrl = fake.Url, AttemptTimeout = Patience, TotalTimeout = Patience });
        await client.DecideAsync(Request, default);
        using var relayed = JsonDocument.Parse("""{"model":"the-child-asked-for-this","state":{},"questions":{}}""");
        await client.DecideRawAsync(relayed.RootElement, default);
        Assert.All(fake.Bodies, body =>
        {
            using var sent = JsonDocument.Parse(body);
            Assert.Equal("jev-latest", sent.RootElement.GetProperty("model").GetString());
            Assert.Single(sent.RootElement.EnumerateObject(), field => field.Name == "model");
        });
        Assert.Equal(2, fake.Bodies.Count);
    }

    [Theory]
    [InlineData("https://decisions.example.test")]
    [InlineData("https://decisions.example.test/")]
    [InlineData("HTTPS://127.0.0.1:8443")]
    public void An_https_base_url_setting_is_taken(string address)
    {
        var options = JevOptions.FromSettings("key", Settings((ApiKeys.TypeSafeBaseUrl, address)));
        Assert.Equal(new JevOptions("key") { BaseUrl = new Uri(address) }, options);
    }

    [Theory]
    [InlineData("http://decisions.example.test")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://user:pass@decisions.example.test")]
    [InlineData("https://user@decisions.example.test")]
    [InlineData("decisions.example.test")]
    [InlineData("//decisions.example.test")]
    [InlineData("wss://decisions.example.test")]
    [InlineData("file:///C:/keys")]
    [InlineData("")]
    public void A_base_url_setting_that_is_not_https_or_carries_a_user_is_refused(string address)
    {
        var refused = Assert.Throws<DecisionException>(() => JevOptions.FromSettings("key",
            Settings((ApiKeys.TypeSafeModel, "jev-latest"), (ApiKeys.TypeSafeBaseUrl, address))));
        Assert.Equal("TYPESAFE_BASE_URL must be an https address of a host alone: no user, password, path or query", refused.Message);
    }

    // Before, an address with a path was taken, and the requests, sent to paths from the host's root, carried the key to
    // another place than the one the address named.
    [Theory]
    [InlineData("https://decisions.example.test/base/")]
    [InlineData("https://decisions.example.test:8443/base/")]
    [InlineData("https://decisions.example.test/?a=1")]
    [InlineData("https://decisions.example.test/#top")]
    public void A_base_url_setting_with_a_path_a_query_or_a_fragment_is_refused(string address)
    {
        Assert.Throws<DecisionException>(() => JevOptions.FromSettings("key", Settings((ApiKeys.TypeSafeBaseUrl, address))));
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
        await Assert.ThrowsAsync<DecisionException>(() => client.DecideAsync(Request, default));
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
