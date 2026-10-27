using Npgsql;

namespace StreamerMetric.Api.Data;

public sealed class TwitchAccountRepository
{
    private readonly string _connectionString;

    public TwitchAccountRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Database connection string is missing.");
    }

    public async Task<long> UpsertAccountAsync(
        string twitchUserId,
        string login,
        string displayName,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            INSERT INTO twitch_accounts
                (twitch_user_id, login, display_name, access_token,
                 refresh_token, access_token_expires_at)
            VALUES
                (@userId, @login, @displayName, @accessToken,
                 @refreshToken, @expiresAt)
            ON CONFLICT (twitch_user_id)
            DO UPDATE SET
                login = EXCLUDED.login,
                display_name = EXCLUDED.display_name,
                access_token = EXCLUDED.access_token,
                refresh_token = EXCLUDED.refresh_token,
                access_token_expires_at = EXCLUDED.access_token_expires_at,
                updated_at = NOW()
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("userId", twitchUserId);
        command.Parameters.AddWithValue("login", login);
        command.Parameters.AddWithValue("displayName", displayName);
        command.Parameters.AddWithValue("accessToken", accessToken);
        command.Parameters.AddWithValue("refreshToken", refreshToken);
        command.Parameters.AddWithValue("expiresAt", expiresAt);

        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "Twitch account was not saved."));
    }

    public async Task<TwitchAccountRecord?> GetAccountAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT id, twitch_user_id, login, display_name,
                   access_token, refresh_token, access_token_expires_at,
                   last_sync_at
            FROM twitch_accounts
            ORDER BY id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new TwitchAccountRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7));
    }

    public async Task UpdateTokensAsync(
        long accountId,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            UPDATE twitch_accounts
            SET access_token = @accessToken,
                refresh_token = @refreshToken,
                access_token_expires_at = @expiresAt,
                updated_at = NOW()
            WHERE id = @accountId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("accessToken", accessToken);
        command.Parameters.AddWithValue("refreshToken", refreshToken);
        command.Parameters.AddWithValue("expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveFollowedChannelsAsync(
        long accountId,
        IReadOnlyCollection<FollowedChannelRecord> channels)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string upsertSql = """
            INSERT INTO twitch_account_followed_channels
                (account_id, streamer_id, is_following, last_seen_at)
            VALUES
                (@accountId, @streamerId, TRUE, NOW())
            ON CONFLICT (account_id, streamer_id)
            DO UPDATE SET
                is_following = TRUE,
                last_seen_at = NOW();
            """;

        foreach (var channel in channels)
        {
            await using var command = new NpgsqlCommand(
                upsertSql,
                connection,
                transaction);
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("streamerId", channel.StreamerId);
            await command.ExecuteNonQueryAsync();
        }

        const string markMissingSql = """
            UPDATE twitch_account_followed_channels
            SET is_following = FALSE
            WHERE account_id = @accountId
              AND last_seen_at < @syncStartedAt;
            """;

        await using var markMissing = new NpgsqlCommand(
            markMissingSql,
            connection,
            transaction);
        markMissing.Parameters.AddWithValue("accountId", accountId);
        markMissing.Parameters.AddWithValue(
            "syncStartedAt",
            channels.Count == 0
                ? DateTimeOffset.UtcNow
                : channels.Min(channel => channel.SyncStartedAt));
        await markMissing.ExecuteNonQueryAsync();

        const string syncSql = """
            UPDATE twitch_accounts
            SET last_sync_at = NOW(), updated_at = NOW()
            WHERE id = @accountId;
            """;

        await using var syncCommand = new NpgsqlCommand(
            syncSql,
            connection,
            transaction);
        syncCommand.Parameters.AddWithValue("accountId", accountId);
        await syncCommand.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<FollowedChannelStatus>> GetFollowedChannelsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT st.twitch_user_id, st.username, st.display_name,
                   st.profile_image_url, afc.is_following
            FROM twitch_account_followed_channels afc
            JOIN twitch_accounts a ON a.id = afc.account_id
            JOIN streamers st ON st.id = afc.streamer_id
            WHERE a.id = (SELECT id FROM twitch_accounts ORDER BY id LIMIT 1)
              AND afc.is_following = TRUE
            ORDER BY st.username;
            """;

        var result = new List<FollowedChannelStatus>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new FollowedChannelStatus(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return result;
    }

    public async Task<IReadOnlyList<FollowedTrackingChannel>> GetFollowedTrackingChannelsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT st.id, st.twitch_user_id, st.username
            FROM twitch_account_followed_channels afc
            JOIN twitch_accounts a ON a.id = afc.account_id
            JOIN streamers st ON st.id = afc.streamer_id
            WHERE a.id = (SELECT id FROM twitch_accounts ORDER BY id LIMIT 1)
              AND afc.is_following = TRUE
            ORDER BY st.username;
            """;

        var result = new List<FollowedTrackingChannel>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new FollowedTrackingChannel(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return result;
    }

    public async Task DeleteAccountAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = """
            DELETE FROM twitch_accounts
            WHERE id = (SELECT id FROM twitch_accounts ORDER BY id LIMIT 1);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record TwitchAccountRecord(
    long Id,
    string TwitchUserId,
    string Login,
    string DisplayName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset? LastSyncAt);

public sealed record FollowedChannelRecord(
    long StreamerId,
    DateTimeOffset SyncStartedAt);

public sealed record FollowedChannelStatus(
    string TwitchUserId,
    string Login,
    string DisplayName,
    string? ProfileImageUrl,
    bool IsFollowing);

public sealed record FollowedTrackingChannel(
    long StreamerId,
    string TwitchUserId,
    string Username);
