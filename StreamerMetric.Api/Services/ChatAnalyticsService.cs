
using System.Collections.Concurrent;

namespace StreamerMetric.Api.Services;


public sealed class ChatAnalyticsService
{
    private readonly ConcurrentDictionary<string, MinuteStats> _minutes = new();

    
public void RecordMessage(
    string broadcasterId,
    string chatterId,
    IEnumerable<string> emoteNames)
    {
        var minute = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-ddTHH:mm:00Z");

        var key = $"{broadcasterId}:{minute}";

        var stats = _minutes.GetOrAdd(key, _ => new MinuteStats());

        stats.Record(chatterId, emoteNames);

                if (_minutes.Count > 1500)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

            foreach (var entry in _minutes)
            {
                var separatorIndex = entry.Key.IndexOf(':');

                if (separatorIndex < 0)
                {
                    continue;
                }

                var timestamp = entry.Key[(separatorIndex + 1)..];

                if (DateTimeOffset.TryParse(
                        timestamp,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedMinute) &&
                    parsedMinute < cutoff)
                {
                    _minutes.TryRemove(entry.Key, out _);
                }
            }
        }
    }

    

    public object GetMinuteStats(string broadcasterId, DateTimeOffset minute)
    {
        var normalizedMinute = new DateTimeOffset(
            minute.Year,
            minute.Month,
            minute.Day,
            minute.Hour,
            minute.Minute,
            0,
            TimeSpan.Zero);

        var key = $"{broadcasterId}:{normalizedMinute:yyyy-MM-ddTHH:mm:00Z}";

        if (!_minutes.TryGetValue(key, out var stats))
        {
            return new
            {
                BroadcasterId = broadcasterId,
                Minute = normalizedMinute,
                MessageCount = 0,
                UniqueChatters = 0,
                EmoteUsage = new Dictionary<string, int>()
            };
        }

        return stats.GetSnapshot(broadcasterId, normalizedMinute);
    }

    public object GetEmoteLeaderboard(string broadcasterId)
    {
        var leaderboard = new Dictionary<string, int>(
            StringComparer.Ordinal);

        foreach (var entry in _minutes)
        {
            var separatorIndex = entry.Key.IndexOf(':');

            if (separatorIndex < 0 ||
                entry.Key[..separatorIndex] != broadcasterId)
            {
                continue;
            }

            var stats = entry.Value;
            var emoteUsage = stats.GetEmoteUsageSnapshot();

            foreach (var emote in emoteUsage)
            {
                leaderboard.TryGetValue(emote.Key, out var count);
                leaderboard[emote.Key] = count + emote.Value;
            }
        }

        var rankedEmotes = leaderboard
            .OrderByDescending(emote => emote.Value)
            .ThenBy(emote => emote.Key, StringComparer.Ordinal)
            .Select((emote, index) => new
            {
                Rank = index + 1,
                Emote = emote.Key,
                UsageCount = emote.Value
            })
            .ToList();

        return new
        {
            BroadcasterId = broadcasterId,
            TotalUniqueEmotes = rankedEmotes.Count,
            Leaderboard = rankedEmotes
        };
    }

    private sealed class MinuteStats
    {
        private readonly object _lock = new();
        private int _messageCount;
        private readonly HashSet<string> _chatters = new();
        private readonly Dictionary<string, int> _emoteUsage = new();

        public void Record(string chatterId, IEnumerable<string> emoteNames)
        {
            lock (_lock)
            {
                _messageCount++;
                _chatters.Add(chatterId);

                foreach (var emoteName in emoteNames)
                {
                    _emoteUsage.TryGetValue(emoteName, out var count);
                    _emoteUsage[emoteName] = count + 1;
                }
            }
        }

        public Dictionary<string, int> GetEmoteUsageSnapshot()
        {
            lock (_lock)
            {
                return new Dictionary<string, int>(_emoteUsage);
            }
        }

        public object GetSnapshot(
            string broadcasterId,
            DateTimeOffset minute)
        {
            lock (_lock)
            {
                return new
                {
                    BroadcasterId = broadcasterId,
                    Minute = minute,
                    MessageCount = _messageCount,
                    UniqueChatters = _chatters.Count,
                    EmoteUsage = new Dictionary<string, int>(_emoteUsage)
                };
            }
        }
    }
}
