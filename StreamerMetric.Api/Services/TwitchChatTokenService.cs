using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using StreamerMetric.Api.Data;

namespace StreamerMetric.Api.Services;

public sealed class TwitchChatTokenService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchAccountRepository _accountRepository;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public TwitchChatTokenService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TwitchAccountRepository accountRepository,
        IDataProtectionProvider protectionProvider)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _accountRepository = accountRepository;
        _protector = protectionProvider.CreateProtector(
            "StreamerMetric.TwitchAccountTokens.v1");
        _accessToken = configuration["Twitch:ChatAccessToken"];
        _refreshToken = configuration["Twitch:ChatRefreshToken"];
    }

    public async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            var connectedAccount = await _accountRepository.GetAccountAsync();
            if (connectedAccount is not null)
            {
                return await GetConnectedAccountTokenAsync(
                    connectedAccount,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            var clientId = _configuration["Twitch:ClientId"];
            var clientSecret = _configuration["Twitch:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(_refreshToken))
            {
                throw new InvalidOperationException(
                    "Twitch chat credentials are incomplete. " +
                    "ClientId, ClientSecret, and ChatRefreshToken are required.");
            }

            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(
                "https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken
                }),
                cancellationToken);

            TwitchChatRefreshResponse? token = null;

            if (response.IsSuccessStatusCode)
            {
                token = await response.Content
                    .ReadFromJsonAsync<TwitchChatRefreshResponse>(
                        cancellationToken);
            }

            if (token is null ||
                string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException(
                    "Twitch chat token refresh failed. " +
                    "Re-authorize Twitch if the refresh token has expired.");
            }

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(0, token.ExpiresIn));

            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                _refreshToken = token.RefreshToken;
            }

            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> GetConnectedAccountTokenAsync(
        TwitchAccountRecord account,
        CancellationToken cancellationToken)
    {
        if (account.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _protector.Unprotect(account.AccessToken);
        }

        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];
        var refreshToken = _protector.Unprotect(account.RefreshToken);
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
            .ReadFromJsonAsync<TwitchChatRefreshResponse>(
                cancellationToken);

        if (!response.IsSuccessStatusCode ||
            token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException(
                "Connected Twitch account authorization expired. Reconnect Twitch.");
        }

        await _accountRepository.UpdateTokensAsync(
            account.Id,
            _protector.Protect(token.AccessToken),
            _protector.Protect(token.RefreshToken),
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        return token.AccessToken;
    }

    private sealed record TwitchChatRefreshResponse(
        [property: JsonPropertyName("access_token")]
        string? AccessToken,
        [property: JsonPropertyName("refresh_token")]
        string? RefreshToken,
        [property: JsonPropertyName("expires_in")]
        int ExpiresIn);
}
