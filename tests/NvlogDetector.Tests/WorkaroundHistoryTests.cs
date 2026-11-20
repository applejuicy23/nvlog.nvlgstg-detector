using NvlogDetector.Core;
using Xunit;

namespace NvlogDetector.Tests;

public class WorkaroundHistoryTests
{
    [Fact]
    public void Actions_survive_restart_and_unchanged_startup_does_not_duplicate_them()
    {
        WithHistory(path =>
        {
            var time = DateTimeOffset.UtcNow;
            var store = HistoryStore.Load(path);
            store.RecordWorkaround(time, false);
            store.RecordWorkaround(time.AddSeconds(1), true, "Applied", "test-user");
            var reloaded = HistoryStore.Load(path);
            Assert.False(reloaded.RecordWorkaround(time.AddSeconds(2), true));
            Assert.Equal(2, reloaded.WorkaroundEvents.Length);
            Assert.Equal("Applied", reloaded.WorkaroundEvents[1].Action);
            Assert.Equal("test-user", reloaded.WorkaroundEvents[1].Actor);
            Assert.Equal(time.AddSeconds(1), reloaded.WorkaroundEvents[1].Timestamp);
            reloaded.RecordWorkaround(time.AddSeconds(3), false, "Restored", "test-user");
            Assert.False(HistoryStore.Load(path).WorkaroundEvents.Last().Configured);
        });
    }

    [Fact]
    public void External_changes_are_observations_without_an_invented_actor()
    {
        WithHistory(path =>
        {
            var store = HistoryStore.Load(path);
            store.RecordWorkaround(DateTimeOffset.UtcNow.AddDays(-1), false);
            store = HistoryStore.Load(path);
            var detected = DateTimeOffset.UtcNow;
            Assert.True(store.RecordWorkaround(detected, true));
            var item = HistoryStore.Load(path).WorkaroundEvents.Last();
            Assert.Equal("Detected", item.Action);
            Assert.Null(item.Actor);
            Assert.Equal(detected, item.Timestamp);
        });
    }

    [Fact]
    public void Older_history_loads_without_losing_recorded_bytes()
    {
        WithHistory(path =>
        {
            File.WriteAllText(path, "{\"totalBytes\":123456,\"episodes\":[],\"minutes\":[]}");
            var store = HistoryStore.Load(path);
            Assert.Empty(store.WorkaroundEvents);
            store.RecordWorkaround(DateTimeOffset.UtcNow, true);
            Assert.Equal(123456, HistoryStore.Load(path).TotalBytes);
        });
    }

    [Fact]
    public void Clearing_history_allows_a_fresh_current_state_observation()
    {
        WithHistory(path =>
        {
            var store = HistoryStore.Load(path);
            store.RecordWorkaround(DateTimeOffset.UtcNow, true, "Applied", "test-user");
            store.Clear();
            Assert.Empty(HistoryStore.Load(path).WorkaroundEvents);
            Assert.True(store.RecordWorkaround(DateTimeOffset.UtcNow, true));
        });
    }

    [Fact]
    public void Failed_save_does_not_prevent_retrying_the_observation()
    {
        WithHistory(path =>
        {
            var store = HistoryStore.Load(path);
            Directory.CreateDirectory(path);
            var error = Record.Exception(() => store.RecordWorkaround(DateTimeOffset.UtcNow, true));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(store.WorkaroundEvents);
            Directory.Delete(path);
            Assert.True(store.RecordWorkaround(DateTimeOffset.UtcNow, true));
            Assert.True(HistoryStore.Load(path).WorkaroundEvents.Single().Configured);
        });
    }

    static void WithHistory(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "nvlog-workaround-history-" + Guid.NewGuid().ToString("N") + ".json");
        try { action(path); }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path);
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
