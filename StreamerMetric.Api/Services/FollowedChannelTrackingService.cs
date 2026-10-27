using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using StreamerMetric.Api.Data;

namespace StreamerMetric.Api.Services;

public sealed class FollowedChannelTrackingService : BackgroundService
{
    // Twitch supports many channel subscriptions on one EventSub transport.
    private const int TrackingCapacity = 20;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FollowedChannelTrackingService> _logger;
    private readonly TwitchChatService _chatService;
    private readonly ConcurrentDictionary<string, FollowedTrackingState> _states = new();

    public FollowedChannelTrackingService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FollowedChannelTrackingService> logger,
        TwitchChatService chatService)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _chatService = chatService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Followed-channel tracking service started with capacity {Capacity}.",
            TrackingCapacity);

        await RunTrackingCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunTrackingCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Followed-channel tracking cycle failed.");
            }
        }
    }

    private async Task RunTrackingCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var accountService = scope.ServiceProvider
            .GetRequiredService<TwitchFollowedChannelService>();

        try
        {
            await accountService.SynchronizeAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(
                exception,
                "Followed-channel synchronization was unavailable.");
            return;
        }

        var accountRepository = scope.ServiceProvider
            .GetRequiredService<TwitchAccountRepository>();
        var channels = await accountRepository
            .GetFollowedTrackingChannelsAsync();
        var accessToken = await GetAppAccessTokenAsync(cancellationToken);
        var liveChannels = new List<LiveChannel>();

        foreach (var channel in channels)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var live = await GetLiveStreamAsync(
                channel.TwitchUserId,
                accessToken,
                cancellationToken);

            if (live is not null)
            {
                liveChannels.Add(new LiveChannel(channel, live));
            }
        }

        var activeChannels = liveChannels
            .OrderBy(channel => channel.Stream.StartedAt)
            .Take(TrackingCapacity)
            .ToArray();

        var activeIds = activeChannels
            .Select(channel => channel.Channel.TwitchUserId)
            .ToHashSet(StringComparer.Ordinal);
        var liveIds = liveChannels
            .Select(channel => channel.Channel.TwitchUserId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var channel in channels)
        {
            if (!activeIds.Contains(channel.TwitchUserId))
                _chatService.StopConnection(channel.TwitchUserId);
        }

        var startedCount = 0;

        foreach (var liveChannel in activeChannels)
        {
            var started = _chatService.TryStartConnection(
                liveChannel.Channel.TwitchUserId,
                TrackingCapacity);

            if (started)
            {
                startedCount++;
            }
        }

        foreach (var channel in channels)
        {
            var state = !liveIds.Contains(channel.TwitchUserId)
                ? "Offline"
                : activeIds.Contains(channel.TwitchUserId)
                    ? "Tracking"
                    : "Waiting";
            var connectionState = state == "Waiting"
                ? "WaitingForCapacity"
                : _chatService.GetConnectionStatus(channel.TwitchUserId).State;
            _states[channel.TwitchUserId] = new FollowedTrackingState(
                channel.TwitchUserId,
                channel.Username,
                state,
                connectionState);
        }

        var waitingCount = Math.Max(
            0,
            liveChannels.Count - startedCount);
        _logger.LogInformation(
            "Followed-channel tracking cycle found {LiveCount} live channels, " +
            "started {StartedCount}, and left {WaitingCount} waiting. " +
            "Shared WebSocket transports in use: {ConnectionCount}. Channel capacity: {Capacity}.",
            liveChannels.Count,
            startedCount,
            waitingCount,
            _chatService.GetRunningConnectionCount(),
            TrackingCapacity);
    }

    public object GetStatus()
    {
        var states = _states.Values
            .OrderBy(state => state.Login)
            .ToArray();

        return new
        {
            capacity = TrackingCapacity,
            followedChannels = states.Length,
            liveChannels = states.Count(state =>
                state.TrackingState is "Tracking" or "Waiting"),
            activelyTracked = states.Count(state =>
                state.TrackingState == "Tracking" &&
                state.ConnectionState == "Connected"),
            waitingForCapacity = states.Count(state =>
                state.TrackingState == "Waiting"),
            channels = states
        };
    }

    public void ClearStatus()
    {
        _states.Clear();
    }

    private async Task<string> GetAppAccessTokenAsync(
        CancellationToken cancellationToken)
    {
        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Twitch application credentials are missing.");
        }

        var response = await _httpClientFactory.CreateClient().PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials"
            }),
            cancellationToken);

        var token = await response.Content
            .ReadFromJsonAsync<AppTokenResponse>(cancellationToken);

        if (!response.IsSuccessStatusCode ||
            token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException(
                "Twitch application token request failed.");
        }

        return token.AccessToken;
    }

    private async Task<LiveStream?> GetLiveStreamAsync(
        string twitchUserId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.twitch.tv/helix/streams?user_id=" +
            $"{Uri.EscapeDataString(twitchUserId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken);
        request.Headers.Add(
            "Client-Id",
            _configuration["Twitch:ClientId"]);

        using var response = await _httpClientFactory.CreateClient()
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Could not check live status for followed broadcaster {BroadcasterId}. " +
                "HTTP {StatusCode}.",
                twitchUserId,
                response.StatusCode);
            return null;
        }

        var result = await response.Content
            .ReadFromJsonAsync<LiveStreamResponse>(cancellationToken);
        return result?.Data?.FirstOrDefault();
    }

    private sealed record LiveChannel(
        FollowedTrackingChannel Channel,
        LiveStream Stream);

    private sealed record AppTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record LiveStreamResponse(
        [property: JsonPropertyName("data")] LiveStream[]? Data);

    private sealed record LiveStream(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("game_name")] string? CategoryName,
        [property: JsonPropertyName("viewer_count")] int ViewerCount,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);
}

public sealed record FollowedTrackingState(
    string TwitchUserId,
    string Login,
    string TrackingState,
    string ConnectionState);
