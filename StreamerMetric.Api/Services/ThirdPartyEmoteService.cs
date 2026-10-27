
using System.Net.Http;
using System.Text.Json;

namespace StreamerMetric.Api.Services;

public sealed class ThirdPartyEmoteService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, (HashSet<string> Names, DateTime CachedAt)>
    _emoteCache = new();

    private readonly object _cacheLock = new();

    public ThirdPartyEmoteService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HashSet<string>> GetEmoteNamesAsync(
    string broadcasterId)
{
    lock (_cacheLock)
    {
        if (_emoteCache.TryGetValue(broadcasterId, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(10))
        {
            return new HashSet<string>(cached.Names, StringComparer.Ordinal);
        }
    }

        var emoteNames = new HashSet<string>(StringComparer.Ordinal);
        var client = _httpClientFactory.CreateClient();

        // 7TV channel emotes
        try
        {
            var response = await client.GetAsync(
                $"https://7tv.io/v3/users/twitch/{broadcasterId}");

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                if (document.RootElement.TryGetProperty(
                    "emote_set", out var emoteSet) &&
                    emoteSet.TryGetProperty("emotes", out var emotes))
                {
                    foreach (var emote in emotes.EnumerateArray())
                    {
                        if (emote.TryGetProperty("name", out var name))
                        {
                            var value = name.GetString();

                            if (!string.IsNullOrWhiteSpace(value))
                                emoteNames.Add(value);
                        }
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Continue if 7TV is unavailable.
        }
        catch (JsonException)
        {
            // Continue if the response is invalid.
        }

        // BetterTTV channel and shared emotes
        try
        {
            var response = await client.GetAsync(
                $"https://api.betterttv.net/3/cached/users/twitch/{broadcasterId}");

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                AddBttvEmotes(document.RootElement, "channelEmotes", emoteNames);
                AddBttvEmotes(document.RootElement, "sharedEmotes", emoteNames);
            }
        }
        catch (HttpRequestException)
        {
            // Continue if BetterTTV is unavailable.
        }
        catch (JsonException)
        {
            // Continue if the response is invalid.
        }

        // FrankerFaceZ channel emotes
        try
        {
            var response = await client.GetAsync(
                $"https://api.frankerfacez.com/v1/room/id/{broadcasterId}");

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                if (document.RootElement.TryGetProperty("sets", out var sets))
                {
                    foreach (var set in sets.EnumerateObject())
                    {
                        if (!set.Value.TryGetProperty(
                            "emoticons", out var emotes))
                            continue;

                        foreach (var emote in emotes.EnumerateArray())
                        {
                            if (emote.TryGetProperty("name", out var name))
                            {
                                var value = name.GetString();

                                if (!string.IsNullOrWhiteSpace(value))
                                    emoteNames.Add(value);
                            }
                        }
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Continue if FrankerFaceZ is unavailable.
        }
        catch (JsonException)
        {
            // Continue if the response is invalid.
        }

        lock (_cacheLock)
        {
            _emoteCache[broadcasterId] = (
                new HashSet<string>(emoteNames, StringComparer.Ordinal),
                DateTime.UtcNow);
        }

        return emoteNames;
    }

    private static void AddBttvEmotes(
        JsonElement root,
        string propertyName,
        HashSet<string> emoteNames)
    {
        if (!root.TryGetProperty(propertyName, out var emotes))
            return;

        foreach (var emote in emotes.EnumerateArray())
        {
            if (emote.TryGetProperty("code", out var code))
            {
                var value = code.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    emoteNames.Add(value);
            }
        }
    }

    public async Task<Dictionary<string, string>> Get7TvEmoteImagesAsync(
    string broadcasterId)
{
    var emoteImages = new Dictionary<string, string>(
        StringComparer.Ordinal);

    var client = _httpClientFactory.CreateClient();

    try
    {
        var response = await client.GetAsync(
            $"https://7tv.io/v3/users/twitch/{broadcasterId}");

        if (!response.IsSuccessStatusCode)
            return emoteImages;

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var root = document.RootElement;

        if (!root.TryGetProperty("emote_set", out var emoteSet) ||
            !emoteSet.TryGetProperty("emotes", out var emotes))
        {
            return emoteImages;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            if (!emote.TryGetProperty("name", out var nameElement) ||
                !emote.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("host", out var host))
            {
                continue;
            }

            var name = nameElement.GetString();

            if (string.IsNullOrWhiteSpace(name) ||
                !host.TryGetProperty("url", out var hostUrlElement) ||
                !host.TryGetProperty("files", out var files))
            {
                continue;
            }

            var hostUrl = hostUrlElement.GetString();

            if (string.IsNullOrWhiteSpace(hostUrl))
                continue;

            if (hostUrl.StartsWith("//"))
                hostUrl = "https:" + hostUrl;

            foreach (var file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("name", out var fileNameElement))
                    continue;

                var fileName = fileNameElement.GetString();

                if (fileName is "1x.webp" or "1x.avif" or "1x.png")
                {
                    emoteImages[name] = $"{hostUrl.TrimEnd('/')}/{fileName}";
                    break;
                }
            }
        }
    }
    catch (HttpRequestException)
    {
        // Return any emote images retrieved so far.
    }
    catch (JsonException)
    {
        // Return any emote images retrieved so far.
    }

    return emoteImages;
}

    public async Task<Dictionary<string, string>> GetEmoteImagesAsync(
        string broadcasterId)
    {
        var emoteImages = await GetTwitchEmoteImagesAsync(broadcasterId);
        var sevenTvImages = await Get7TvEmoteImagesAsync(broadcasterId);

        foreach (var emote in sevenTvImages)
        {
            emoteImages[emote.Key] = emote.Value;
        }

        return emoteImages;
    }

    private async Task<Dictionary<string, string>> GetTwitchEmoteImagesAsync(
        string broadcasterId)
    {
        var emoteImages = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var clientId = _configuration["Twitch:ClientId"];
        var clientSecret = _configuration["Twitch:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return emoteImages;
        }

        var client = _httpClientFactory.CreateClient();

        try
        {
            var tokenResponse = await client.PostAsync(
                "https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "client_credentials"
                }));

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return emoteImages;
            }

            using var tokenDocument = JsonDocument.Parse(
                await tokenResponse.Content.ReadAsStringAsync());

            if (!tokenDocument.RootElement.TryGetProperty(
                    "access_token",
                    out var accessTokenElement))
            {
                return emoteImages;
            }

            var accessToken = accessTokenElement.GetString();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return emoteImages;
            }

            foreach (var endpoint in new[]
            {
                "https://api.twitch.tv/helix/chat/emotes/global",
                $"https://api.twitch.tv/helix/chat/emotes?broadcaster_id={Uri.EscapeDataString(broadcasterId)}"
            })
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    endpoint);

                request.Headers.Add("Client-Id", clientId);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        accessToken);

                using var response = await client.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                if (!document.RootElement.TryGetProperty(
                        "data",
                        out var emotes))
                {
                    continue;
                }

                foreach (var emote in emotes.EnumerateArray())
                {
                    if (!emote.TryGetProperty("name", out var nameElement) ||
                        !emote.TryGetProperty("images", out var images) ||
                        !images.TryGetProperty("url_1x", out var imageElement))
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    var imageUrl = imageElement.GetString();

                    if (!string.IsNullOrWhiteSpace(name) &&
                        !string.IsNullOrWhiteSpace(imageUrl))
                    {
                        emoteImages[name] = imageUrl;
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Return any images retrieved before the request failed.
        }
        catch (JsonException)
        {
            // Return any images retrieved before the response was parsed.
        }

        return emoteImages;
    }

}
