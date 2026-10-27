using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamerMetric.Api.Data;
using StreamerMetric.Api.Services;

public sealed class TwitchChatService
{
    private const string EventSubUrl = "wss://eventsub.wss.twitch.tv/ws";
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChatAnalyticsService _chatAnalyticsService;
    private readonly ThirdPartyEmoteService _thirdPartyEmoteService;
    private readonly ChatRepository _chatRepository;
    private readonly ILogger<TwitchChatService> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly TwitchChatTokenService _tokenService;
    private readonly ConcurrentDictionary<string, ChatConnectionStatus> _statuses = new();
    private readonly HashSet<string> _broadcasterIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribedBroadcasterIds = new(StringComparer.Ordinal);
    private readonly object _connectionLock = new();
    private Task? _sharedConnection;
    private string? _sessionId;

    public TwitchChatService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ChatAnalyticsService chatAnalyticsService,
        ThirdPartyEmoteService thirdPartyEmoteService,
        ChatRepository chatRepository,
        ILogger<TwitchChatService> logger,
        IHostApplicationLifetime applicationLifetime,
        TwitchChatTokenService tokenService)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _chatAnalyticsService = chatAnalyticsService;
        _thirdPartyEmoteService = thirdPartyEmoteService;
        _chatRepository = chatRepository;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _tokenService = tokenService;
    }

    public bool StartConnection(string broadcasterId) => TryStartConnection(broadcasterId, null);

    public bool StopConnection(string broadcasterId)
    {
        if (string.IsNullOrWhiteSpace(broadcasterId))
            return false;

        lock (_connectionLock)
        {
            var removed = _broadcasterIds.Remove(broadcasterId);
            _subscribedBroadcasterIds.Remove(broadcasterId);

            if (removed)
            {
                SetStatus(ChatConnectionStatus.Offline(
                    broadcasterId,
                    "Chat tracking stopped because the channel is no longer selected."));
            }

            return removed;
        }
    }

    public void StopAllConnections()
    {
        string[] broadcasterIds;
        lock (_connectionLock)
        {
            broadcasterIds = _broadcasterIds.ToArray();
        }

        foreach (var broadcasterId in broadcasterIds)
            StopConnection(broadcasterId);
    }

    public bool TryStartConnection(string broadcasterId, int? maximumConnections)
    {
        if (string.IsNullOrWhiteSpace(broadcasterId))
            return false;

        lock (_connectionLock)
        {
            if (!_broadcasterIds.Add(broadcasterId))
                return false;

            if (maximumConnections.HasValue && _broadcasterIds.Count > maximumConnections.Value)
            {
                _broadcasterIds.Remove(broadcasterId);
                return false;
            }

            SetStatus(ChatConnectionStatus.Connecting(
                broadcasterId, "Connecting to the shared Twitch EventSub WebSocket."));

            if (_sharedConnection is null || _sharedConnection.IsCompleted)
            {
                _sharedConnection = RunSharedConnectionAsync();
                _ = _sharedConnection.ContinueWith(
                    _ => { lock (_connectionLock) _sharedConnection = null; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // A live session subscribes new channels immediately; the welcome path
            // subscribes all channels already registered.
            if (_sessionId is not null)
                _ = SubscribeAdditionalBroadcasterAsync(
                    _sessionId, broadcasterId, _applicationLifetime.ApplicationStopping);

            return true;
        }
    }

    public int GetRunningConnectionCount()
    {
        lock (_connectionLock)
            return _sharedConnection is { IsCompleted: false } ? 1 : 0;
    }

    public ChatConnectionStatus GetConnectionStatus(string broadcasterId) =>
        _statuses.TryGetValue(broadcasterId, out var status)
            ? status
            : ChatConnectionStatus.Offline(broadcasterId);

    // Kept for the existing manual route. It joins the shared session and waits
    // until that shared transport closes, matching the old route's behavior.
    public async Task ConnectAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        if (!StartConnection(broadcasterId))
            throw new InvalidOperationException("The broadcaster is already connected.");

        Task connection;
        lock (_connectionLock) connection = _sharedConnection!;
        await connection.WaitAsync(cancellationToken);
    }

    private async Task RunSharedConnectionAsync()
    {
        var channelIds = SnapshotBroadcasters();
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(EventSubUrl), _applicationLifetime.ApplicationStopping);
            _logger.LogInformation("Connected to shared Twitch EventSub WebSocket for {Count} broadcasters.",
                channelIds.Length);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open &&
                   !_applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),
                        _applicationLifetime.ApplicationStopping);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetAllStatuses(ChatConnectionStatus.Offline,
                            $"Twitch closed the shared WebSocket connection ({result.CloseStatus}).");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var json = JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
                var root = json.RootElement;
                var messageType = root.GetProperty("metadata").GetProperty("message_type").GetString();
                _logger.LogDebug("Received Twitch EventSub message type {MessageType}.", messageType);

                if (messageType == "session_welcome")
                {
                    _sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(_sessionId))
                        throw new InvalidOperationException("Twitch welcome message did not contain a session ID.");

                    foreach (var id in SnapshotBroadcasters())
                    {
                        await CreateChatSubscriptionAsync(_sessionId, id,
                            _applicationLifetime.ApplicationStopping);
                        SetStatus(ChatConnectionStatus.Connected(id,
                            "Shared WebSocket and EventSub chat subscription are active."));
                    }
                }
                else if (messageType == "session_reconnect")
                {
                    _logger.LogInformation(
                        "Twitch requested a shared EventSub WebSocket reconnect.");
                    return;
                }
                else if (messageType == "notification")
                {
                    await ProcessNotificationAsync(root);
                }
            }
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            SetAllStatuses(ChatConnectionStatus.Offline, "Chat connection stopped.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Shared Twitch chat connection failed.");
            SetAllStatuses(ChatConnectionStatus.Error, exception.Message);
        }
        finally
        {
            lock (_connectionLock)
            {
                _sessionId = null;
                _subscribedBroadcasterIds.Clear();
            }

            if (!_applicationLifetime.ApplicationStopping.IsCancellationRequested &&
                SnapshotBroadcasters().Length > 0)
            {
                _ = RestartSharedConnectionAsync();
            }
        }
    }

    private async Task RestartSharedConnectionAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2), _applicationLifetime.ApplicationStopping);

        lock (_connectionLock)
        {
            if (_applicationLifetime.ApplicationStopping.IsCancellationRequested ||
                _broadcasterIds.Count == 0 ||
                (_sharedConnection is not null && !_sharedConnection.IsCompleted))
            {
                return;
            }

            _logger.LogInformation(
                "Restarting the shared Twitch EventSub WebSocket for {Count} broadcasters.",
                _broadcasterIds.Count);
            _sharedConnection = RunSharedConnectionAsync();
            _ = _sharedConnection.ContinueWith(
                _ => { lock (_connectionLock) _sharedConnection = null; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ProcessNotificationAsync(JsonElement root)
    {
        var eventData = root.GetProperty("payload").GetProperty("event");
        var broadcasterId = eventData.GetProperty("broadcaster_user_id").GetString();
        if (string.IsNullOrWhiteSpace(broadcasterId) || !IsRegistered(broadcasterId))
            return;

        if (!eventData.TryGetProperty("message_id", out var messageIdElement) ||
            !eventData.TryGetProperty("message", out var messageObject) ||
            !messageObject.TryGetProperty("text", out var textElement))
            return;

        var chatterId = eventData.GetProperty("chatter_user_id").GetString();
        var chatterUsername = eventData.GetProperty("chatter_user_login").GetString() ?? "unknown";
        var chatterDisplayName = eventData.GetProperty("chatter_user_name").GetString() ?? chatterUsername;
        var twitchMessageId = messageIdElement.GetString();
        if (string.IsNullOrWhiteSpace(chatterId) || string.IsNullOrWhiteSpace(twitchMessageId))
            return;

        var emoteNames = new List<string>();
        if (messageObject.TryGetProperty("fragments", out var fragments))
            foreach (var fragment in fragments.EnumerateArray())
                if (fragment.GetProperty("type").GetString() == "emote")
                    emoteNames.Add(fragment.GetProperty("text").GetString() ?? string.Empty);

        // Loading third-party emotes preserves the existing cache/warm-up behavior.
        await _thirdPartyEmoteService.GetEmoteNamesAsync(broadcasterId);
        await _chatRepository.SaveChatterAsync(chatterId, chatterUsername, chatterDisplayName);
        var streamId = await _chatRepository.GetLatestStreamIdAsync(broadcasterId);
        if (streamId.HasValue)
        {
            var messageId = await _chatRepository.SaveChatMessageAsync(
                twitchMessageId, streamId.Value, chatterId, textElement.GetString() ?? string.Empty,
                DateTimeOffset.UtcNow);
            if (messageId.HasValue)
                await _chatRepository.SaveMessageEmotesAsync(messageId.Value, emoteNames);
        }
        _chatAnalyticsService.RecordMessage(broadcasterId, chatterId, emoteNames);
    }

    private async Task SubscribeAdditionalBroadcasterAsync(
        string sessionId,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        try
        {
            await CreateChatSubscriptionAsync(sessionId, broadcasterId, cancellationToken);
            SetStatus(ChatConnectionStatus.Connected(
                broadcasterId, "Shared WebSocket and EventSub chat subscription are active."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Could not add broadcaster {BroadcasterId} to the shared EventSub session.",
                broadcasterId);
            SetStatus(ChatConnectionStatus.Error(broadcasterId, exception.Message));
        }
    }

    private async Task CreateChatSubscriptionAsync(string sessionId, string broadcasterId,
        CancellationToken cancellationToken)
    {
        lock (_connectionLock)
        {
            if (!_subscribedBroadcasterIds.Add(broadcasterId))
                return;
        }

        var clientId = _configuration["Twitch:ClientId"];
        var chatUserId = _configuration["Twitch:ChatUserId"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(chatUserId))
        {
            lock (_connectionLock) _subscribedBroadcasterIds.Remove(broadcasterId);
            throw new InvalidOperationException("Twitch chat subscription credentials are missing.");
        }

        try
        {
            var accessToken = await _tokenService.GetAccessTokenAsync(cancellationToken);
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Add("Client-Id", clientId);
            using var response = await client.PostAsJsonAsync(
                "https://api.twitch.tv/helix/eventsub/subscriptions",
                new
                {
                    type = "channel.chat.message", version = "1",
                    condition = new { broadcaster_user_id = broadcasterId, user_id = chatUserId },
                    transport = new { method = "websocket", session_id = sessionId }
                }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Twitch EventSub subscription failed with HTTP {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data")[0];
            if (data.GetProperty("status").GetString() != "enabled" ||
                data.GetProperty("type").GetString() != "channel.chat.message")
                throw new InvalidOperationException("Twitch EventSub did not return an enabled chat subscription.");
            _logger.LogInformation("Twitch EventSub chat subscription confirmed for broadcaster {BroadcasterId}.",
                broadcasterId);
        }
        catch
        {
            lock (_connectionLock) _subscribedBroadcasterIds.Remove(broadcasterId);
            throw;
        }
    }

    private string[] SnapshotBroadcasters()
    {
        lock (_connectionLock) return _broadcasterIds.ToArray();
    }

    private bool IsRegistered(string broadcasterId)
    {
        lock (_connectionLock) return _broadcasterIds.Contains(broadcasterId);
    }

    private void SetAllStatuses(
        Func<string, string?, ChatConnectionStatus> factory, string detail)
    {
        foreach (var id in SnapshotBroadcasters())
            SetStatus(factory(id, detail));
    }

    private void SetStatus(ChatConnectionStatus status) => _statuses[status.BroadcasterId] = status;
}

public sealed record ChatConnectionStatus(
    string BroadcasterId, string State, string? Detail, DateTimeOffset UpdatedAt)
{
    public static ChatConnectionStatus Connecting(string id, string detail) =>
        new(id, "Connecting", detail, DateTimeOffset.UtcNow);
    public static ChatConnectionStatus Connected(string id, string detail) =>
        new(id, "Connected", detail, DateTimeOffset.UtcNow);
    public static ChatConnectionStatus Offline(string id, string? detail = null) =>
        new(id, "Offline", detail, DateTimeOffset.UtcNow);
    public static ChatConnectionStatus Error(string id, string? detail) =>
        new(id, "Error", detail, DateTimeOffset.UtcNow);
}
