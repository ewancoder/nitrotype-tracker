using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NitroType.Tracker.Domain;
using Npgsql;
using StackExchange.Redis;
using Tyr.Framework;

var builder = WebApplication.CreateBuilder(args);
var isDebug = false;
#if DEBUG
isDebug = true;
#endif

var cache = new MemoryCache(new MemoryCacheOptions());

var config = TyrHostConfiguration.Default(
    builder.Configuration,
    "NitroType",
    isDebug: isDebug);

await builder.ConfigureTyrApplicationBuilderAsync(config);

//builder.Services.AddHttpClient();
//builder.Services.AddSingleton<DataRetriever>();
//builder.Services.AddSingleton<RawDataRepository>();
//builder.Services.AddSingleton<SmartDataRetriever>();
builder.Services.AddSingleton<DataProcessor>();

builder.Services.AddSingleton<NpgsqlDataSource>(provider =>
{
    var connectionString = provider.GetRequiredService<IConfiguration>()["DbConnectionString"]
                           ?? throw new InvalidOperationException("DB connection string is not set.");

    var dbBuilder = new NpgsqlDataSourceBuilder(connectionString);
    return dbBuilder.Build();
});

builder.Services.AddSingleton<NormalizedDataRepository>();
builder.Services.AddSingleton<DataNormalizer>();

var app = builder.Build();

using var cts = new CancellationTokenSource();

app.ConfigureTyrApplication(config);

//var retriever = app.Services.GetRequiredService<SmartDataRetriever>();
//retriever.RegisterTeam("KECATS");
//retriever.RegisterTeam("SSH");
//var task = retriever.RunAsync(cts.Token);

var processor = app.Services.GetRequiredService<DataProcessor>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

var normalizer = app.Services.GetRequiredService<DataNormalizer>();

_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await normalizer.ProcessTeamDataAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to normalize data");
        }

        await Task.Delay(TimeSpan.FromMinutes(2));
    }
});

app.MapGet("/api/statistics/{team}", async (string team, NormalizedDataRepository repository) =>
{
    var cached = cache.Get<List<PlayerInfo>>(team);
    if (cached is not null)
        return cached;

    var sw = new Stopwatch();
    sw.Start();

    // Start of the league season
    var now = DateTime.UtcNow;
    var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0);

    var stats = await repository.GetTeamStatsAsync(team, monthStart);

    // Convert to the expected PlayerInfo format
    var result = stats.Select(s => new PlayerInfo
    {
        Username = s.Username,
        Team = s.Team,
        Typed = s.Typed,
        Errors = s.Errors,
        Name = s.Name,
        RacesPlayed = s.RacesPlayed,
        Timestamp = s.Timestamp,
        Secs = s.Secs,
        AccuracyDiff = 0,
        AverageSpeedDiff = 0, // Temporarily disable diffs.
        RacesPlayedDiff = s.RacesPlayedDiff
    }).ToList();

    sw.Stop();
    logger.LogInformation("Gathered data for team {Team}, took {Seconds} seconds", team, sw.Elapsed.TotalSeconds);

    cache.Set(team, result, TimeSpan.FromMinutes(3));

    return result;
});

await app.RunAsync();

public sealed class PlayerInfo
{
    public required string Username { get; set; }
    public required string Team { get; set; }
    public required long Typed { get; set; }
    public required long Errors { get; set; }
    public required string Name { get; set; }
    public required int RacesPlayed { get; set; }
    public required DateTime Timestamp { get; set; }
    public required long Secs { get; set; }

    public decimal Accuracy => Typed == 0 ? 0 : 100m * (Typed - Errors) / Typed;
    public decimal AverageTextLength => RacesPlayed == 0 ? 0 : (decimal)Typed / RacesPlayed;
    // ReSharper disable once ArrangeRedundantParentheses
    public decimal AverageSpeed => Secs == 0 ? 0 : (60m / 5) * Typed / Secs;

    public decimal AccuracyDiff { get; set; }
    public decimal AverageSpeedDiff { get; set; }
    public decimal RacesPlayedDiff { get; set; }

    public string TimeSpent
    {
        get
        {
            var time = TimeSpan.FromSeconds(Secs);
            var parts = new List<string>();
            if (time.Days > 0)
                parts.Add($"{time.Days} day{(time.Days > 1 ? "s" : "")}");
            if (time.Hours > 0)
                parts.Add($"{time.Hours} hour{(time.Hours > 1 ? "s" : "")}");
            if (time.Minutes > 0)
                parts.Add($"{time.Minutes} minute{(time.Minutes > 1 ? "s" : "")}");
            if (time.Seconds > 0)
                parts.Add($"{time.Seconds} second{(time.Minutes > 1 ? "s" : "")}");

            return string.Join(" ", parts);
        }
    }

    public static PlayerInfo operator -(PlayerInfo one, PlayerInfo two)
    {
        if (one.Username != two.Username)
            throw new InvalidOperationException("Cannot subtract different users.");

        return new PlayerInfo
        {
            Username = one.Username,
            Name = one.Name,
            Team = one.Team,
            Timestamp = one.Timestamp,
            Typed = one.Typed - two.Typed,
            Secs = one.Secs - two.Secs,
            Errors = one.Errors - two.Errors,
            RacesPlayed = one.RacesPlayed - two.RacesPlayed
        };
    }
}
