using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using StreamerMetric.Api.Data;

namespace StreamerMetric.Api.Services;

public sealed class TwitchFollowedChannelService
{
    private const string FollowedChannelsUrl =
        "https://api.twitch.tv/helix/channels/followed";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchAccountRepository _accounts;
    private readonly StreamerRepository _streamers;
    private readonly IDataProtector _protector;
    private readonly ILogger<TwitchFollowedChannelService> _logger;
    private readonly TwitchOAuthStateStore _stateStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public TwitchFollowedChannelService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TwitchAccountRepository accounts,
        StreamerRepository streamers,
        IDataProtectionProvider protectionProvider,
        ILogger<TwitchFollowedChannelService> logger,
        TwitchOAuthStateStore stateStore)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _accounts = accounts;
        _streamers = streamers;
        _protector = protectionProvider.CreateProtector(
            "StreamerMetric.TwitchAccountTokens.v1");
        _logger = logger;
        _stateStore = stateStore;
    }

    public async Task<string> StartAuthorizationAsync()
    {
        var clientId = _configuration["Twitch:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Twitch Client ID is missing.");
        }

        var state = _stateStore.CreateState();
        var redirectUri = GetRedirectUri();
        var scopes = "user:read:follows user:read:chat";
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state
        }.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

        return $"https://id.twitch.tv/oauth2/authorize?{query}";
    }

    public async Task CompleteAuthorizationAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Twitch OAuth credentials are incomplete.");
        }

        var client = _httpClientFactory.CreateClient();
        using var tokenResponse = await client.PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = GetRedirectUri()
            }),
            cancellationToken);

        var token = await tokenResponse.Content
            .ReadFromJsonAsync<TwitchUserToken>(cancellationToken);

        if (!tokenResponse.IsSuccessStatusCode ||
            token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException(
                "Twitch authorization could not be completed.");
        }

        var user = await GetCurrentUserAsync(token.AccessToken, cancellationToken);
        await _accounts.UpsertAccountAsync(
            user.Id,
            user.Login,
            user.DisplayName,
            _protector.Protect(token.AccessToken),
            _protector.Protect(token.RefreshToken),
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));

        await SynchronizeAsync(cancellationToken);
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var account = await _accounts.GetAccountAsync()
                ?? throw new InvalidOperationException(
                    "No Twitch account is connected.");
            var token = await GetValidAccessTokenAsync(account, cancellationToken);
            var channels = new List<FollowedChannelRecord>();
            string? cursor = null;
            var syncStartedAt = DateTimeOffset.UtcNow;

            do
            {
                var url = $"{FollowedChannelsUrl}?user_id=" +
                    $"{Uri.EscapeDataString(account.TwitchUserId)}&first=100";
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url += $"&after={Uri.EscapeDataString(cursor)}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddTwitchHeaders(request, token);
                using var response = await _httpClientFactory
                    .CreateClient()
                    .SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Twitch followed-channel request failed with HTTP {(int)response.StatusCode}.");
                }

                var page = await response.Content
                    .ReadFromJsonAsync<FollowedChannelsResponse>(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Twitch returned an invalid followed-channel response.");

                foreach (var channel in page.Data)
                {
                    await _streamers.SaveStreamerAsync(
                        channel.BroadcasterId,
                        channel.BroadcasterLogin,
                        channel.BroadcasterName,
                        null);
                    var streamerId = await _streamers.GetStreamerIdAsync(
                        channel.BroadcasterId)
                        ?? throw new InvalidOperationException(
                            "Followed streamer could not be saved.");
                    channels.Add(new FollowedChannelRecord(
                        streamerId,
                        syncStartedAt));
                }

                cursor = page.Pagination?.Cursor;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            await _accounts.SaveFollowedChannelsAsync(account.Id, channels);
            _logger.LogInformation(
                "Synchronized {FollowedChannelCount} Twitch followed channels.",
                channels.Count);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<object> GetStatusAsync()
    {
        var account = await _accounts.GetAccountAsync();
        var channels = account is null
            ? Array.Empty<FollowedChannelStatus>()
            : await _accounts.GetFollowedChannelsAsync();

        return new
        {
            connected = account is not null,
            twitchUser = account is null
                ? null
                : new
                {
                    id = account.TwitchUserId,
                    login = account.Login,
                    displayName = account.DisplayName
                },
            followedChannels = channels.Count,
            lastSuccessfulSyncAt = account?.LastSyncAt
        };
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var account = await _accounts.GetAccountAsync();
        if (account is null)
        {
            return;
        }

        var token = _protector.Unprotect(account.AccessToken);
        var clientId = _configuration["Twitch:ClientId"];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://id.twitch.tv/oauth2/revoke?client_id=" +
                $"{Uri.EscapeDataString(clientId)}&token=" +
                $"{Uri.EscapeDataString(token)}");
            await _httpClientFactory.CreateClient()
                .SendAsync(request, cancellationToken);
        }

        await _accounts.DeleteAccountAsync();
    }

    private async Task<string> GetValidAccessTokenAsync(
        TwitchAccountRecord account,
        CancellationToken cancellationToken)
    {
        if (account.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _protector.Unprotect(account.AccessToken);
        }

        var refreshToken = _protector.Unprotect(account.RefreshToken);
        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];
        var response = await _httpClientFactory.CreateClient().PostAsync(
            "https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId ?? "",
                ["client_secret"] = clientSecret ?? "",
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }),
            cancellationToken);
        var token = await response.Content
            .ReadFromJsonAsync<TwitchUserToken>(cancellationToken);

        if (!response.IsSuccessStatusCode ||
            token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException(
                "Twitch account authorization expired. Reconnect Twitch.");
        }

        await _accounts.UpdateTokensAsync(
            account.Id,
            _protector.Protect(token.AccessToken),
            _protector.Protect(token.RefreshToken),
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        return token.AccessToken;
    }

    private async Task<TwitchUser> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.twitch.tv/helix/users");
        AddTwitchHeaders(request, accessToken);
        using var response = await _httpClientFactory.CreateClient()
            .SendAsync(request, cancellationToken);
        var users = await response.Content
            .ReadFromJsonAsync<TwitchUsersResponse>(cancellationToken);

        if (!response.IsSuccessStatusCode ||
            users?.Data is null ||
            users.Data.Length == 0)
        {
            throw new InvalidOperationException(
                "Twitch did not return the connected account.");
        }

        return users.Data[0];
    }

    private void AddTwitchHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token);
        request.Headers.Add(
            "Client-Id",
            _configuration["Twitch:ClientId"]);
    }

    private string GetRedirectUri() =>
        _configuration["Twitch:OAuthRedirectUri"]
        ?? "http://localhost:5168/api/twitch/account/callback";

    private sealed record TwitchUserToken(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record TwitchUsersResponse(
        [property: JsonPropertyName("data")] TwitchUser[] Data);

    private sealed record TwitchUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("display_name")] string DisplayName);

    private sealed record FollowedChannelsResponse(
        [property: JsonPropertyName("data")] FollowedChannel[] Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    private sealed record FollowedChannel(
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId,
        [property: JsonPropertyName("broadcaster_login")] string BroadcasterLogin,
        [property: JsonPropertyName("broadcaster_name")] string BroadcasterName);

    private sealed record Pagination(
        [property: JsonPropertyName("cursor")] string? Cursor);
}
