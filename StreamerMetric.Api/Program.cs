
using Npgsql;
using StreamerMetric.Api.Services;
using StreamerMetric.Api.Data;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var dashboardPath = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, "..", "StreamerMetric.Dashboard"));

builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardPolicy", policy =>
    {
        policy
            .WithOrigins("http://127.0.0.1:5500", "http://localhost:5500")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddDataProtection();
builder.Services.AddScoped<StreamerRepository>();
builder.Services.AddSingleton<TwitchAccountRepository>();
builder.Services.AddScoped<TwitchFollowedChannelService>();
builder.Services.AddHostedService<ViewerTrackingService>();
builder.Services.AddSingleton<FollowedChannelTrackingService>();
builder.Services.AddHostedService(
    serviceProvider => serviceProvider
        .GetRequiredService<FollowedChannelTrackingService>());
builder.Services.AddSingleton<TwitchChatTokenService>();
builder.Services.AddSingleton<TwitchChatService>();
builder.Services.AddSingleton<ChatAnalyticsService>();
builder.Services.AddSingleton<TwitchOAuthStateStore>();
builder.Services.AddSingleton<ThirdPartyEmoteService>();
builder.Services.AddSingleton<ChatRepository>();

var app = builder.Build();

app.UseCors("DashboardPolicy");
if (Directory.Exists(dashboardPath))
{
    var dashboardProvider = new PhysicalFileProvider(dashboardPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = dashboardProvider,
        RequestPath = "/dashboard"
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = dashboardProvider,
        RequestPath = "/dashboard"
    });
}

app.MapGet("/", () => new
{
    name = "StreamerMetric",
    message = "Twitch analytics API is running!",
    version = "1.0"
});

app.MapGet("/api/twitch/user/{username}", async (
    string username,
    IConfiguration configuration,
    StreamerRepository repository) =>
{
    var clientId = configuration["Twitch:ClientId"];
    var clientSecret = configuration["Twitch:ClientSecret"];

    if (string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        return Results.Problem("Twitch credentials are missing.");
    }

    using var httpClient = new HttpClient();

    var tokenResponse = await httpClient.PostAsync(
        "https://id.twitch.tv/oauth2/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        }));

    if (!tokenResponse.IsSuccessStatusCode)
    {
        return Results.Problem("Unable to authenticate with Twitch.");
    }

    var tokenData =
        await tokenResponse.Content.ReadFromJsonAsync<TwitchToken>();

    if (tokenData is null)
    {
        return Results.Problem("Twitch returned an invalid token response.");
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(username)}");

    request.Headers.Add("Client-Id", clientId);
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            tokenData.AccessToken);

    using var response = await httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem("Unable to retrieve the Twitch user.");
    }

    var userData =
        await response.Content.ReadFromJsonAsync<TwitchUserResponse>();

    if (userData?.Data is null || userData.Data.Length == 0)
    {
        return Results.NotFound(new { message = "Twitch user not found." });
    }

    var user = userData.Data[0];

    await repository.SaveStreamerAsync(
        user.Id,
        user.Login,
        user.DisplayName,
        user.ProfileImageUrl);

    return Results.Ok(new
    {
        user.Id,
        user.Login,
        user.DisplayName,
        user.ProfileImageUrl,
        savedToDatabase = true
    });
});

app.MapGet("/api/streamers", async (StreamerRepository repository) =>
{
    var streamers = await repository.GetStreamersAsync();

    return Results.Ok(streamers);
});

app.MapGet("/api/twitch/streams/history/{broadcasterId}", async (
    string broadcasterId,
    StreamerRepository repository) =>
{
        var streams = await repository.GetStreamHistoryAsync(
            broadcasterId);

        return Results.Ok(streams);
    });


app.MapGet("/api/twitch/analytics/{streamId}", async (
    string streamId,
    StreamerRepository repository) =>
{
    var analytics = await repository.GetStreamAnalyticsAsync(streamId);

    if (analytics is null)
    {
        return Results.NotFound(new
        {
            message = "Stream not found in the database."
        });
    }

    return Results.Ok(analytics);
});

app.MapGet("/api/twitch/stream/{username}", async (
    string username,
    IConfiguration configuration,
    StreamerRepository repository) =>
{
    var clientId = configuration["Twitch:ClientId"];
    var clientSecret = configuration["Twitch:ClientSecret"];

    if (string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        return Results.Problem("Twitch credentials are missing.");
    }

    using var httpClient = new HttpClient();

    var tokenResponse = await httpClient.PostAsync(
        "https://id.twitch.tv/oauth2/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        }));

    if (!tokenResponse.IsSuccessStatusCode)
    {
        return Results.Problem("Unable to authenticate with Twitch.");
    }

    var tokenData =
        await tokenResponse.Content.ReadFromJsonAsync<TwitchToken>();

    if (tokenData is null)
    {
        return Results.Problem("Twitch returned an invalid token response.");
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(username)}");

    request.Headers.Add("Client-Id", clientId);
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            tokenData.AccessToken);

    using var response = await httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem("Unable to retrieve Twitch stream information.");
    }

    var streamData =
        await response.Content.ReadFromJsonAsync<TwitchStreamResponse>();

    if (streamData?.Data is null || streamData.Data.Length == 0)
    {
        return Results.Ok(new
        {
            username,
            isLive = false,
            message = "This streamer is not currently live."
        });
    }

    var stream = streamData.Data[0];

    var savedStreamerId = await repository.GetStreamerIdAsync(stream.UserId);

    if (savedStreamerId is null)
    {
        return Results.NotFound(new
        {
            message = "Streamer not found in your database. Look up their Twitch profile first."
        });
    }

    await repository.SaveStreamAsync(
        stream.Id,
        savedStreamerId.Value,
        stream.Title,
        stream.GameName,
        stream.StartedAt);

        
    await repository.SaveViewerSnapshotAsync(
        stream.Id,
        stream.ViewerCount);

    return Results.Ok(new
    {
        username = stream.UserLogin,
        isLive = true,
        streamId = stream.Id,
        title = stream.Title,
        category = stream.GameName,
        viewerCount = stream.ViewerCount,
        startedAt = stream.StartedAt
    });
});


app.MapGet("/api/database/test", async (IConfiguration configuration) =>
{
    var connectionString =
        configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("Database connection string is missing.");
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = 'public'",
            connection);

        var tableCount = (long)(await command.ExecuteScalarAsync())!;

        return Results.Ok(new
        {
            status = "Connected",
            database = "streamermetric",
            tablesFound = tableCount
        });
    }
    catch (NpgsqlException)
    {
        return Results.Problem(
            "Could not connect to PostgreSQL. Check that PostgreSQL is running and your connection settings are correct.",
            statusCode: 503);
    }
});



app.MapGet("/api/twitch/chat/connect/{broadcasterId}", async (
    string broadcasterId,
    TwitchChatService chatService,
    CancellationToken cancellationToken) =>
{
    await chatService.ConnectAsync(
        broadcasterId,
        cancellationToken);

    return Results.Ok(new
    {
        message = "EventSub connection closed."
    });
});

app.MapPost("/api/twitch/chat/connect/{broadcasterId}/start", async (
    string broadcasterId,
    TwitchChatService chatService,
    TwitchChatTokenService tokenService) =>
{
    try
    {
        await tokenService.GetAccessTokenAsync();
        var started = chatService.StartConnection(broadcasterId);

        return Results.Ok(new
        {
            broadcasterId,
            started,
            message = started
                ? "Chat connection started."
                : "Chat connection is already running."
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.MapGet("/api/twitch/chat/status/{broadcasterId}", (
    string broadcasterId,
    TwitchChatService chatService) =>
{
    return Results.Ok(chatService.GetConnectionStatus(broadcasterId));
});



app.MapGet("/api/twitch/auth/start", async (
    TwitchFollowedChannelService accountService) =>
{
    try
    {
        return Results.Ok(new
        {
            authorizationUrl = await accountService.StartAuthorizationAsync()
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});




app.MapGet("/api/twitch/auth/callback", async (
    string? code,
    string? error,
    string? state,
    TwitchOAuthStateStore stateStore,
    TwitchFollowedChannelService accountService,
    CancellationToken cancellationToken) =>
{
    if (!stateStore.ValidateAndConsume(state))
    {
        return Results.BadRequest(new
        {
            message = "Invalid or expired OAuth state. Start authorization again without restarting the API."
        });

    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        return Results.BadRequest(new
        {
            message = "Twitch authorization was denied.",
            error
        });
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.BadRequest(new
        {
            message = "No authorization code was received."
        });
    }

    try
    {
        await accountService.CompleteAuthorizationAsync(
            code,
            cancellationToken);
        return Results.Ok(new
        {
            message = "Twitch account connected and followed channels synchronized."
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.MapGet("/api/twitch/account/connect", async (
    TwitchFollowedChannelService accountService) =>
{
    try
    {
        return Results.Ok(new
        {
            authorizationUrl = await accountService.StartAuthorizationAsync()
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.MapGet("/api/twitch/account/callback", async (
    string? code,
    string? error,
    string? state,
    TwitchOAuthStateStore stateStore,
    TwitchFollowedChannelService accountService,
    CancellationToken cancellationToken) =>
{
    static IResult RedirectToDashboard(string status, string? message = null)
    {
        var query = $"oauth={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrWhiteSpace(message))
            query += $"&message={Uri.EscapeDataString(message)}";

        return Results.Redirect($"/dashboard?{query}");
    }

    if (!stateStore.ValidateAndConsume(state))
    {
        return RedirectToDashboard(
            "error",
            "Invalid or expired Twitch OAuth state. Start authorization again.");
    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        return RedirectToDashboard("error", "Twitch authorization was denied.");
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        return RedirectToDashboard(
            "error",
            "Twitch did not return an authorization code.");
    }

    try
    {
        await accountService.CompleteAuthorizationAsync(
            code,
            cancellationToken);
        return RedirectToDashboard(
            "success",
            "Twitch account connected and followed channels synchronized.");
    }
    catch (InvalidOperationException exception)
    {
        return RedirectToDashboard("error", exception.Message);
    }
});

app.MapGet("/api/twitch/account/status", async (
    TwitchFollowedChannelService accountService) =>
    Results.Ok(await accountService.GetStatusAsync()));

app.MapGet("/api/twitch/account/followed-channels", async (
    TwitchAccountRepository accountRepository) =>
    Results.Ok(await accountRepository.GetFollowedChannelsAsync()));

app.MapPost("/api/twitch/account/sync", async (
    TwitchFollowedChannelService accountService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await accountService.SynchronizeAsync(cancellationToken);
        return Results.Ok(await accountService.GetStatusAsync());
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.MapPost("/api/twitch/account/disconnect", async (
    TwitchFollowedChannelService accountService,
    CancellationToken cancellationToken) =>
{
    await accountService.DisconnectAsync(cancellationToken);
    return Results.Ok(new { message = "Twitch account disconnected." });
});

app.MapGet("/api/twitch/account/tracking-status", (
    FollowedChannelTrackingService trackingService) =>
    Results.Ok(trackingService.GetStatus()));


app.MapGet("/api/twitch/chat/analytics/{broadcasterId}", (
    string broadcasterId,
    DateTimeOffset? minute,
    ChatAnalyticsService analyticsService) =>
{
    var targetMinute = minute ?? DateTimeOffset.UtcNow;

    return Results.Ok(
        analyticsService.GetMinuteStats(broadcasterId, targetMinute));
});


app.MapGet("/api/twitch/chat/analytics/persistent/{broadcasterId}",
    async (
        string broadcasterId,
        long? streamId,
        ChatRepository chatRepository) =>
{
    var analytics = await chatRepository
        .GetChatAnalyticsAsync(broadcasterId, streamId);

    return Results.Ok(analytics);
});

app.MapGet(
    "/api/twitch/chat/analytics/stream/{broadcasterId}/{streamId:long}",
    async (
        string broadcasterId,
        long streamId,
        ChatRepository chatRepository) =>
    {
        var analytics = await chatRepository.GetChatAnalyticsAsync(
            broadcasterId,
            streamId);

        return Results.Ok(analytics);
    });

app.MapGet("/api/twitch/chat/analytics/messages-per-minute/{broadcasterId}",
    async (
        string broadcasterId,
        long? streamId,
        ChatRepository chatRepository) =>
{
    var results = await chatRepository
        .GetMessagesPerMinuteAsync(broadcasterId, streamId);

    return Results.Ok(results);
});

app.MapGet("/api/twitch/chat/analytics/unique-chatters-per-minute/{broadcasterId}",
    async (
        string broadcasterId,
        long? streamId,
        ChatRepository chatRepository) =>
{
    var results = await chatRepository
        .GetUniqueChattersPerMinuteAsync(broadcasterId, streamId);

    return Results.Ok(results);
});

app.MapGet("/api/twitch/chat/analytics/emotes-per-minute/{broadcasterId}",
    async (
        string broadcasterId,
        long? streamId,
        ChatRepository chatRepository) =>
{
    var results = await chatRepository
        .GetEmotesPerMinuteAsync(broadcasterId, streamId);

    return Results.Ok(results);
});


app.MapGet("/api/twitch/chat/leaderboard/{broadcasterId}",
    async (
        string broadcasterId,
        long? streamId,
        ChatRepository chatRepository) =>
{
    var leaderboard = await chatRepository
        .GetEmoteLeaderboardAsync(broadcasterId, streamId);

    return Results.Ok(leaderboard);
});

app.MapGet(
    "/api/twitch/emotes/7tv/{broadcasterId}",
    async (
        string broadcasterId,
        ThirdPartyEmoteService emoteService) =>
    {
        var images = await emoteService.GetEmoteImagesAsync(
            broadcasterId);

        return Results.Ok(images);
    });

app.Run();

record TwitchToken(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")]
    string AccessToken);

record TwitchUserResponse(TwitchUser[] Data);

record TwitchUser(
    string Id,
    string Login,
    [property: System.Text.Json.Serialization.JsonPropertyName("display_name")]
    string DisplayName,
    [property: System.Text.Json.Serialization.JsonPropertyName("profile_image_url")]
    string ProfileImageUrl);

    
record TwitchStreamResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("data")]
    TwitchStream[] Data);

record TwitchStream(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")]
    string Id,

    [property: System.Text.Json.Serialization.JsonPropertyName("user_id")]
    string UserId,

    [property: System.Text.Json.Serialization.JsonPropertyName("user_login")]
    string UserLogin,

    [property: System.Text.Json.Serialization.JsonPropertyName("title")]
    string Title,

    [property: System.Text.Json.Serialization.JsonPropertyName("game_name")]
    string GameName,

    [property: System.Text.Json.Serialization.JsonPropertyName("viewer_count")]
    int ViewerCount,

    [property: System.Text.Json.Serialization.JsonPropertyName("started_at")]
    DateTime StartedAt);

public sealed class TwitchChatTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("scope")]
    public string[] Scope { get; set; } = [];
}


public sealed class TwitchOAuthStateStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, DateTimeOffset> _states = new();

    public string CreateState()
    {
        var state = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _states[state] = DateTimeOffset.UtcNow.AddMinutes(10);

        return state;
    }

    public bool ValidateAndConsume(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        if (!_states.TryRemove(state, out var expiresAt))
        {
            return false;
        }

        return expiresAt > DateTimeOffset.UtcNow;
    }
}
