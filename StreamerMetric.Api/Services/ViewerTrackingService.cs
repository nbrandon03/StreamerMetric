
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

public class ViewerTrackingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ViewerTrackingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    
    private async Task<string?> GetTwitchAccessTokenAsync(
        IHttpClientFactory httpClientFactory)
    {
        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            _logger.LogError("Twitch credentials are missing from configuration.");
            return null;
        }

        var client = httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("grant_type", "client_credentials")
        });

        var response = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();

            _logger.LogError(
                "Twitch token request failed. Status: {StatusCode}. Response: {ErrorBody}",
                response.StatusCode,
                errorBody);

            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>();

        return result?.AccessToken;
    }

    
    private async Task<TwitchStreamStatus?> CheckStreamerLiveStatusAsync(
        string twitchUserId,
        string accessToken)
    {
        var client = _httpClientFactory.CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken);

        client.DefaultRequestHeaders.Add(
            "Client-Id", _configuration["Twitch:ClientId"]);

        var url =
            $"https://api.twitch.tv/helix/streams?user_id={Uri.EscapeDataString(twitchUserId)}";

        var response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Could not check live status for Twitch user ID {UserId}. Status: {StatusCode}",
                twitchUserId,
                response.StatusCode);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<TwitchStreamStatusResponse>();

        if (result?.Data == null || result.Data.Count == 0)
        {
            _logger.LogInformation(
                "Twitch user ID {UserId} is currently offline.",
                twitchUserId);
            return null;
        }

        var stream = result.Data[0];

        _logger.LogInformation(
            "Twitch user ID {UserId} is LIVE: {Title} ({Viewers} viewers)",
            twitchUserId,
            stream.Title,
            stream.ViewerCount);

        return stream;
    }

    private sealed class TwitchStreamStatusResponse
    {
        public List<TwitchStreamStatus>? Data { get; set; }
    }


    private sealed class TwitchStreamStatus
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string? Title { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("game_name")]
        public string? CategoryName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("viewer_count")]
        public int ViewerCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("started_at")]
        public DateTime StartedAt { get; set; }
    }

    private sealed class TwitchTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }


    public ViewerTrackingService(
        IServiceScopeFactory scopeFactory,
        ILogger<ViewerTrackingService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }


    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Viewer tracking service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var accessToken = await GetTwitchAccessTokenAsync(
                    _httpClientFactory);

                if (accessToken == null)
                {
                    _logger.LogError(
                        "Viewer tracking could not obtain a Twitch access token.");
                }
                else
                {
                    using var scope = _scopeFactory.CreateScope();

                    var repository = scope.ServiceProvider
                        .GetRequiredService<StreamerRepository>();

                    var streamers = await repository.GetStreamersAsync();

                    foreach (var streamer in streamers)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var twitchUserId = (string)streamer.GetType()
                            .GetProperty("TwitchUserId")!
                            .GetValue(streamer)!;

                        var streamerId = await repository.GetStreamerIdAsync(
                            twitchUserId);

                        if (streamerId == null)
                        {
                            _logger.LogWarning(
                                "Could not find database record for Twitch user ID {UserId}.",
                                twitchUserId);
                            continue;
                        }

                        var liveStream = await CheckStreamerLiveStatusAsync(
                            twitchUserId, accessToken);

                        await repository.CloseEndedStreamsAsync(
                            streamerId.Value,
                            liveStream?.Id);

                        if (liveStream == null)
                        {
                            continue;
                        }

                        await repository.SaveStreamAsync(
                            liveStream.Id,
                            streamerId.Value,
                            liveStream.Title,
                            liveStream.CategoryName,
                            liveStream.StartedAt);

                        await repository.SaveViewerSnapshotAsync(
                            liveStream.Id,
                            liveStream.ViewerCount);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during viewer tracking.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Viewer tracking service stopped.");
    }

}

