using Npgsql;

public class StreamerRepository
{
    private readonly string _connectionString;

    public StreamerRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Database connection string is missing.");
    }

    public async Task SaveStreamerAsync(
        string twitchUserId,
        string username,
        string displayName,
        string? profileImageUrl)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            INSERT INTO streamers
                (twitch_user_id, username, display_name, profile_image_url)
            VALUES
                (@id, @username, @displayName, @profileImageUrl)
            ON CONFLICT (twitch_user_id)
            DO UPDATE SET
                username = EXCLUDED.username,
                display_name = EXCLUDED.display_name,
                profile_image_url = EXCLUDED.profile_image_url,
                updated_at = NOW();
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("id", twitchUserId);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("displayName", displayName);
        command.Parameters.AddWithValue(
            "profileImageUrl",
            (object?)profileImageUrl ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

public async Task<List<object>> GetStreamersAsync()
{
    var streamers = new List<object>();

    await using var connection =
        new NpgsqlConnection(_connectionString);

    await connection.OpenAsync();

    const string sql = """
        SELECT twitch_user_id, username, display_name, profile_image_url
        FROM streamers
        ORDER BY username;
        """;

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        streamers.Add(new
        {
            TwitchUserId = reader.GetString(0),
            Username = reader.GetString(1),
            DisplayName = reader.GetString(2),
            ProfileImageUrl = reader.IsDBNull(3)
                ? null
                : reader.GetString(3)
        });
    }

    return streamers;
}

public async Task SaveStreamAsync(
    string twitchStreamId,
    long streamerId,
    string? title,
    string? categoryName,
    DateTime startedAt)
{
    await using var connection =
        new NpgsqlConnection(_connectionString);

    await connection.OpenAsync();

    const string sql = """
        INSERT INTO streams
            (twitch_stream_id, streamer_id, title, category_name, started_at)
        VALUES
            (@streamId, @streamerId, @title, @categoryName, @startedAt)
        ON CONFLICT (twitch_stream_id)
        DO UPDATE SET
            title = EXCLUDED.title,
            category_name = EXCLUDED.category_name;
        """;

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("streamId", twitchStreamId);
    command.Parameters.AddWithValue("streamerId", streamerId);
    command.Parameters.AddWithValue(
        "title", (object?)title ?? DBNull.Value);
    command.Parameters.AddWithValue(
        "categoryName", (object?)categoryName ?? DBNull.Value);
    command.Parameters.AddWithValue("startedAt", startedAt);

    await command.ExecuteNonQueryAsync();
}


    public async Task CloseEndedStreamsAsync(
        long streamerId,
        string? activeTwitchStreamId)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            UPDATE streams
            SET ended_at = NOW()
            WHERE streamer_id = @streamerId
            AND ended_at IS NULL
            AND (
                CAST(@activeStreamId AS TEXT) IS NULL
                OR twitch_stream_id <> CAST(@activeStreamId AS TEXT)
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("streamerId", streamerId);
        command.Parameters.AddWithValue(
            "activeStreamId",
            (object?)activeTwitchStreamId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }


    public async Task<long?> GetStreamerIdAsync(string twitchUserId)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT id
            FROM streamers
            WHERE twitch_user_id = @twitchUserId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("twitchUserId", twitchUserId);

        var result = await command.ExecuteScalarAsync();

        if (result is null)
        {
            return null;
        }

        return Convert.ToInt64(result);
    }


    public async Task SaveViewerSnapshotAsync(
        string twitchStreamId,
        int viewerCount)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            INSERT INTO stream_snapshots (stream_id, viewer_count)
            SELECT id, @viewerCount
            FROM streams
            WHERE twitch_stream_id = @twitchStreamId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("twitchStreamId", twitchStreamId);
        command.Parameters.AddWithValue("viewerCount", viewerCount);

        var rowsInserted = await command.ExecuteNonQueryAsync();

        if (rowsInserted == 0)
        {
            throw new InvalidOperationException(
                "Cannot save viewer snapshot: stream was not found in the database.");
        }
    }


    public async Task<object?> GetStreamAnalyticsAsync(string twitchStreamId)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                s.twitch_stream_id,
                s.title,
                s.category_name,
                s.started_at,
                COUNT(ss.id) AS snapshot_count,
                COALESCE(MAX(ss.viewer_count), 0) AS peak_viewers,
                COALESCE(ROUND(AVG(ss.viewer_count), 2), 0) AS average_viewers
            FROM streams s
            LEFT JOIN stream_snapshots ss ON ss.stream_id = s.id
            WHERE s.twitch_stream_id = @streamId
            GROUP BY
                s.id,
                s.twitch_stream_id,
                s.title,
                s.category_name,
                s.started_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("streamId", twitchStreamId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new
        {
            StreamId = reader.GetString(0),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Category = reader.IsDBNull(2) ? null : reader.GetString(2),
            StartedAt = reader.GetDateTime(3),
            SnapshotCount = reader.GetInt64(4),
            PeakViewers = reader.GetInt32(5),
            AverageViewers = reader.GetDecimal(6)
        };
    }

    public async Task<List<object>> GetStreamHistoryAsync(
        string broadcasterTwitchUserId)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync();

        const string sql = """
            SELECT
                s.id,
                s.twitch_stream_id,
                s.title,
                s.category_name,
                s.started_at,
                s.ended_at
            FROM streams s
            JOIN streamers st ON st.id = s.streamer_id
            WHERE st.twitch_user_id = @broadcasterId
            ORDER BY s.started_at DESC;
            """;

        var streams = new List<object>();

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "broadcasterId", broadcasterTwitchUserId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            streams.Add(new
            {
                Id = reader.GetInt64(0),
                TwitchStreamId = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                CategoryName = reader.IsDBNull(3) ? null : reader.GetString(3),
                StartedAt = reader.GetDateTime(4),
                EndedAt = reader.IsDBNull(5)
                    ? (DateTime?)null
                    : reader.GetDateTime(5)
            });
        }

        return streams;
    }


}

