using Npgsql;

namespace StreamerMetric.Api.Data;

public class ChatRepository
{
    private readonly string _connectionString;

    public ChatRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Database connection string is missing.");
    }

    
    public async Task SaveChatterAsync(
        string twitchUserId,
        string username,
        string displayName)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            INSERT INTO chatters
                (twitch_user_id, username, display_name)
            VALUES
                (@twitchUserId, @username, @displayName)
            ON CONFLICT (twitch_user_id)
            DO UPDATE SET
                username = EXCLUDED.username,
                display_name = EXCLUDED.display_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("twitchUserId", twitchUserId);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("displayName", displayName);

        await command.ExecuteNonQueryAsync();
    }

    
    public async Task<long?> SaveChatMessageAsync(
        string twitchMessageId,
        long streamId,
        string chatterTwitchUserId,
        string messageText,
        DateTimeOffset sentAt)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            INSERT INTO chat_messages
                (twitch_message_id, stream_id, chatter_id,
                message_text, sent_at)
            SELECT
                @messageId, @streamId, c.id,
                @messageText, @sentAt
            FROM chatters c
            WHERE c.twitch_user_id = @chatterTwitchUserId
            ON CONFLICT (twitch_message_id) DO NOTHING
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("messageId", twitchMessageId);
        command.Parameters.AddWithValue("streamId", streamId);
        command.Parameters.AddWithValue(
            "chatterTwitchUserId", chatterTwitchUserId);
        command.Parameters.AddWithValue("messageText", messageText);
        command.Parameters.AddWithValue("sentAt", sentAt);

        var result = await command.ExecuteScalarAsync();

        return result is null ? null : (long)result;
    }


    public async Task SaveMessageEmotesAsync(
        long messageId,
        IEnumerable<string> emoteNames)
    {
        var emoteCounts = emoteNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .Select(group => new
            {
                Name = group.Key,
                Count = group.Count()
            });

        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        foreach (var emote in emoteCounts)
        {
            const string saveEmoteSql = """
                INSERT INTO emotes (emote_key, emote_name)
                VALUES (@emoteName, @emoteName)
                ON CONFLICT (emote_key) DO NOTHING;
                """;

            await using (var saveEmoteCommand =
                new NpgsqlCommand(saveEmoteSql, connection))
            {
                saveEmoteCommand.Parameters.AddWithValue(
                    "emoteName", emote.Name);

                await saveEmoteCommand.ExecuteNonQueryAsync();
            }

            const string saveUsageSql = """
                INSERT INTO chat_message_emotes
                    (message_id, emote_id, usage_count)
                SELECT @messageId, e.id, @usageCount
                FROM emotes e
                WHERE e.emote_key = @emoteName
                ON CONFLICT (message_id, emote_id)
                DO UPDATE SET
                    usage_count = EXCLUDED.usage_count;
                """;

            await using var saveUsageCommand =
                new NpgsqlCommand(saveUsageSql, connection);

            saveUsageCommand.Parameters.AddWithValue(
                "messageId", messageId);
            saveUsageCommand.Parameters.AddWithValue(
                "usageCount", emote.Count);
            saveUsageCommand.Parameters.AddWithValue(
                "emoteName", emote.Name);

            await saveUsageCommand.ExecuteNonQueryAsync();
        }
    }


    public async Task<long?> GetLatestStreamIdAsync(
        string broadcasterTwitchUserId)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT s.id
            FROM streams s
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            AND s.ended_at IS NULL
            ORDER BY s.started_at DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        var result = await command.ExecuteScalarAsync();

        return result is null ? null : (long)result;
    }


    public async Task<object> GetEmoteLeaderboardAsync(
        string broadcasterTwitchUserId,
        long? streamId = null)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                e.emote_name AS "EmoteName",
                SUM(cme.usage_count) AS "UsageCount"
            FROM chat_message_emotes cme
            JOIN emotes e ON e.id = cme.emote_id
            JOIN chat_messages cm ON cm.id = cme.message_id
            JOIN streams s ON s.id = cm.stream_id
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            AND (CAST(@streamId AS BIGINT) IS NULL OR s.id = CAST(@streamId AS BIGINT))
            GROUP BY e.emote_name
            ORDER BY SUM(cme.usage_count) DESC, e.emote_name ASC;
            """;

        var leaderboard = new List<object>();

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        command.Parameters.AddWithValue(
            "streamId", (object?)streamId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            leaderboard.Add(new
            {
                EmoteName = reader.GetString(0),
                UsageCount = reader.GetInt64(1)
            });
        }

        return new
        {
            BroadcasterId = broadcasterTwitchUserId,
            StreamId = streamId,
            TotalUniqueEmotes = leaderboard.Count,
            Leaderboard = leaderboard
        };
    }


    public async Task<object> GetChatAnalyticsAsync(
        string broadcasterTwitchUserId,
        long? streamId = null)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                COUNT(cm.id) AS "TotalMessages",
                COUNT(DISTINCT cm.chatter_id) AS "UniqueChatters"
            FROM chat_messages cm
            JOIN streams s ON s.id = cm.stream_id
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            AND (CAST(@streamId AS BIGINT) IS NULL OR s.id = CAST(@streamId AS BIGINT));
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        command.Parameters.AddWithValue(
            "streamId", (object?)streamId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        await reader.ReadAsync();

        return new
        {
            BroadcasterId = broadcasterTwitchUserId,
            StreamId = streamId,
            TotalMessages = reader.GetInt64(0),
            UniqueChatters = reader.GetInt64(1)
        };
    }


    public async Task<object> GetMessagesPerMinuteAsync(
        string broadcasterTwitchUserId,
        long? streamId = null)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                date_trunc('minute', cm.sent_at) AS "Minute",
                COUNT(cm.id) AS "MessageCount"
            FROM chat_messages cm
            JOIN streams s ON s.id = cm.stream_id
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            AND (CAST(@streamId AS BIGINT) IS NULL OR s.id = CAST(@streamId AS BIGINT))
            GROUP BY date_trunc('minute', cm.sent_at)
            ORDER BY "Minute" ASC;
            """;

        var results = new List<object>();

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        command.Parameters.AddWithValue(
            "streamId", (object?)streamId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                Minute = reader.GetDateTime(0),
                MessageCount = reader.GetInt64(1)
            });
        }

        return new
        {
            BroadcasterId = broadcasterTwitchUserId,
            StreamId = streamId,
            MessagesPerMinute = results
        };
    }


    public async Task<object> GetUniqueChattersPerMinuteAsync(
        string broadcasterTwitchUserId,
        long? streamId = null)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                date_trunc('minute', cm.sent_at) AS "Minute",
                COUNT(DISTINCT cm.chatter_id) AS "UniqueChatters"
            FROM chat_messages cm
            JOIN streams s ON s.id = cm.stream_id
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            AND (CAST(@streamId AS BIGINT) IS NULL OR s.id = CAST(@streamId AS BIGINT))
            GROUP BY date_trunc('minute', cm.sent_at)
            ORDER BY "Minute" ASC;
            """;

        var results = new List<object>();

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        command.Parameters.AddWithValue(
            "streamId", (object?)streamId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                Minute = reader.GetDateTime(0),
                UniqueChatters = reader.GetInt64(1)
            });
        }

        return new
        {
            BroadcasterId = broadcasterTwitchUserId,
            StreamId = streamId,
            UniqueChattersPerMinute = results
        };
    }

    public async Task<object> GetEmotesPerMinuteAsync(
        string broadcasterTwitchUserId,
        long? streamId = null)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                date_trunc('minute', cm.sent_at) AS "Minute",
                COALESCE(SUM(cme.usage_count), 0) AS "EmoteCount"
            FROM chat_messages cm
            JOIN streams s ON s.id = cm.stream_id
            JOIN streamers st ON st.id = s.streamer_id
            LEFT JOIN chat_message_emotes cme
                ON cme.message_id = cm.id
            WHERE st.twitch_user_id = @broadcasterId
            AND (CAST(@streamId AS BIGINT) IS NULL OR s.id = CAST(@streamId AS BIGINT))
            GROUP BY date_trunc('minute', cm.sent_at)
            ORDER BY "Minute" ASC;
            """;

        var results = new List<object>();

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        command.Parameters.AddWithValue(
            "streamId", (object?)streamId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new
            {
                Minute = reader.GetDateTime(0),
                EmoteCount = reader.GetInt64(1)
            });
        }

        return new
        {
            BroadcasterId = broadcasterTwitchUserId,
            StreamId = streamId,
            EmotesPerMinute = results
        };
    }



}

