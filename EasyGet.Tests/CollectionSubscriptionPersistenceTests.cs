using EasyGet.Models;
using EasyGet.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyGet.Tests;

public sealed class CollectionSubscriptionPersistenceTests
{
    [Fact]
    public async Task Upsert_PersistsCompleteBaselineAndSettingsAcrossRestart()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-subscription");
        var observedAt = new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);
        try
        {
            long id;
            using (var service = new HistoryService(dbPath))
            {
                var stored = await service.UpsertCollectionSubscriptionAsync(
                    CreateSubscription("bilibili:season:8920135"),
                    [Entry("BV1", "one", 1), Entry("BV2", "two", 2), Entry("BV3", "not selected", 3)],
                    observedAt);

                id = stored.Id;
                Assert.True(id > 0);
                Assert.Equal("history-group", stored.BatchId);
                Assert.Equal("Nginx course", stored.BatchName);
                Assert.Equal(3, stored.Items.Count);
                Assert.All(stored.Items, item => Assert.Equal(CollectionSubscriptionItemState.Known, item.State));
                Assert.Equal(observedAt, stored.LastSuccessUtc);
                Assert.Equal(observedAt.AddHours(6), stored.NextCheckUtc);
                Assert.Equal(0, stored.PendingNewCount);
            }

            using var reopened = new HistoryService(dbPath);
            var restored = await reopened.GetCollectionSubscriptionByCanonicalKeyAsync("bilibili:season:8920135");
            Assert.NotNull(restored);
            Assert.Equal(id, restored!.Id);
            Assert.Equal(@"D:\Videos\Nginx", restored.OutputDirectory);
            Assert.Equal("mp4", restored.Format);
            Assert.Equal("best", restored.Quality);
            Assert.Equal("all", restored.Subtitle);
            Assert.Equal(3, restored.Items.Count);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Upsert_ExistingSubscriptionPreservesOriginalStorageAndMarksUnseenEntriesNew()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-reimport");
        try
        {
            using var service = new HistoryService(dbPath);
            var first = CreateSubscription("example:collection:reimport");
            first.OutputDirectory = @"D:\Videos\Original";
            first.Format = "mkv";
            first.Quality = "720";
            first.Subtitle = "all";
            first.BatchId = "original-batch";
            first.BatchName = "Original collection";
            first.AutoCheckEnabled = false;
            first.CheckInterval = TimeSpan.FromHours(12);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                first,
                [Entry("a", "A", 1)]);

            var repeated = CreateSubscription(first.CanonicalKey);
            repeated.Platform = "";
            repeated.Title = "";
            repeated.OutputDirectory = @"E:\Other\Location";
            repeated.Format = "mp4";
            repeated.Quality = "best";
            repeated.Subtitle = "none";
            repeated.BatchId = "replacement-batch";
            repeated.BatchName = "Replacement collection";
            repeated.AutoCheckEnabled = true;
            repeated.CheckInterval = TimeSpan.FromHours(1);
            var restored = await service.UpsertCollectionSubscriptionAsync(
                repeated,
                [Entry("a", "", 1), Entry("b", "B", 2)]);

            Assert.Equal(stored.Id, restored.Id);
            Assert.Equal("Example", restored.Platform);
            Assert.Equal("Nginx course", restored.Title);
            Assert.Equal(@"D:\Videos\Original", restored.OutputDirectory);
            Assert.Equal("mkv", restored.Format);
            Assert.Equal("720", restored.Quality);
            Assert.Equal("all", restored.Subtitle);
            Assert.Equal("original-batch", restored.BatchId);
            Assert.Equal("Original collection", restored.BatchName);
            Assert.False(restored.AutoCheckEnabled);
            Assert.Equal(TimeSpan.FromHours(12), restored.CheckInterval);
            Assert.Equal("A", restored.Items.Single(item => item.EntryKey == "a").Title);
            Assert.Equal(CollectionSubscriptionItemState.Known,
                restored.Items.Single(item => item.EntryKey == "a").State);
            Assert.Equal(CollectionSubscriptionItemState.New,
                restored.Items.Single(item => item.EntryKey == "b").State);
            Assert.Equal(1, restored.PendingNewCount);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Upsert_WithInitialQueuedStatesIsAtomicAndDoesNotReplaceExistingTask()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-atomic-upsert");
        try
        {
            using var service = new HistoryService(dbPath);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:atomic-upsert"),
                [Entry("a", "A", 1), Entry("b", "B", 2)],
                [
                    new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = "a",
                        State = CollectionSubscriptionItemState.Queued,
                        TaskId = "task-a",
                        ExpectedTaskId = ""
                    },
                    new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = "b",
                        State = CollectionSubscriptionItemState.Queued,
                        TaskId = "task-b",
                        ExpectedTaskId = ""
                    }
                ]);

            Assert.All(stored.Items, item => Assert.Equal(
                CollectionSubscriptionItemState.Queued,
                item.State));
            Assert.Equal(["task-a", "task-b"],
                stored.Items.OrderBy(item => item.Position).Select(item => item.TaskId).ToArray());

            var repeated = CreateSubscription(stored.CanonicalKey);
            repeated.Title = "Should roll back";
            repeated.OutputDirectory = @"E:\ShouldNotReplace";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpsertCollectionSubscriptionAsync(
                    repeated,
                    [Entry("a", "A changed", 1), Entry("b", "B", 2), Entry("c", "C", 3)],
                    [new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = "a",
                        State = CollectionSubscriptionItemState.Queued,
                        TaskId = "replacement-task",
                        ExpectedTaskId = ""
                    }]));

            var afterConflict = await service.GetCollectionSubscriptionAsync(stored.Id);
            Assert.NotNull(afterConflict);
            Assert.Equal("Nginx course", afterConflict!.Title);
            Assert.Equal(@"D:\Videos\Nginx", afterConflict.OutputDirectory);
            Assert.Equal("task-a",
                afterConflict.Items.Single(item => item.EntryKey == "a").TaskId);
            Assert.DoesNotContain(afterConflict.Items, item => item.EntryKey == "c");
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ApplySnapshot_OnlyReportsNeverSeenKeysAndPreservesStateAcrossRemovalAndRecovery()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-diff");
        try
        {
            using var service = new HistoryService(dbPath);
            var baselineAt = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:1"),
                [Entry("a", "Old A", 1), Entry("b", "Old B", 2)],
                baselineAt);
            var originalSequences = stored.Items.ToDictionary(item => item.EntryKey, item => item.LocalSequence);

            var firstRefresh = await service.ApplyCollectionSubscriptionSnapshotAsync(
                stored.Id,
                [Entry("b", "Renamed B", 1), Entry("a", "Renamed A", 2), Entry("c", "New C", 3)],
                "Updated title",
                baselineAt.AddHours(6));

            var newItem = Assert.Single(firstRefresh.NewItems);
            Assert.Equal("c", newItem.EntryKey);
            Assert.Equal(CollectionSubscriptionItemState.New, newItem.State);
            Assert.Equal("Updated title", firstRefresh.Subscription.Title);
            Assert.Equal(originalSequences["a"], firstRefresh.Subscription.Items.Single(item => item.EntryKey == "a").LocalSequence);
            Assert.Equal("Renamed B", firstRefresh.Subscription.Items.Single(item => item.EntryKey == "b").Title);

            await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "c",
                    State = CollectionSubscriptionItemState.Ignored,
                    TaskId = "task-c"
                }],
                baselineAt.AddHours(7));
            await service.ApplyCollectionSubscriptionSnapshotAsync(
                stored.Id,
                [Entry("a", "Renamed A", 1), Entry("b", "Renamed B", 2)],
                attemptedAtUtc: baselineAt.AddHours(12));

            var recovered = await service.ApplyCollectionSubscriptionSnapshotAsync(
                stored.Id,
                [Entry("c", "C returns", 1), Entry("b", "Renamed B", 2), Entry("a", "Renamed A", 3)],
                attemptedAtUtc: baselineAt.AddHours(18));

            Assert.Empty(recovered.NewItems);
            var recoveredC = recovered.Subscription.Items.Single(item => item.EntryKey == "c");
            Assert.True(recoveredC.IsPresent);
            Assert.Equal(CollectionSubscriptionItemState.Ignored, recoveredC.State);
            Assert.Equal("task-c", recoveredC.TaskId);
            Assert.Equal("C returns", recoveredC.Title);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task RefreshFailure_PreservesLastSuccessfulSnapshotAndAdvancesRetryTime()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-failure");
        try
        {
            using var service = new HistoryService(dbPath);
            var successAt = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:failure"),
                [Entry("a", "A", 1), Entry("b", "B", 2)],
                successAt);

            var failedAt = successAt.AddHours(6);
            var failed = await service.RecordCollectionSubscriptionRefreshFailureAsync(
                stored.Id,
                "network unavailable",
                failedAt);

            Assert.Equal(failedAt, failed.LastAttemptUtc);
            Assert.Equal(successAt, failed.LastSuccessUtc);
            Assert.Equal(failedAt.AddHours(6), failed.NextCheckUtc);
            Assert.Equal("network unavailable", failed.FailureMessage);
            Assert.Equal(["a", "b"], failed.Items.Select(item => item.EntryKey).ToArray());
            Assert.All(failed.Items, item => Assert.True(item.IsPresent));
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task StateUpdates_AreAtomicAndPersistTaskIds()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-states");
        try
        {
            using var service = new HistoryService(dbPath);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:states"),
                [Entry("a", "A", 1), Entry("b", "B", 2)]);
            await service.ApplyCollectionSubscriptionSnapshotAsync(
                stored.Id,
                [Entry("a", "A", 1), Entry("b", "B", 2), Entry("c", "C", 3)]);

            var affected = await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [
                    new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = "a",
                        State = CollectionSubscriptionItemState.Queued,
                        TaskId = "task-a"
                    },
                    new CollectionSubscriptionItemStateUpdate
                    {
                        EntryKey = "c",
                        State = CollectionSubscriptionItemState.Failed,
                        TaskId = "task-c"
                    }
                ]);

            Assert.Equal(2, affected);
            var restored = await service.GetCollectionSubscriptionAsync(stored.Id);
            Assert.NotNull(restored);
            Assert.Equal(CollectionSubscriptionItemState.Queued, restored!.Items.Single(item => item.EntryKey == "a").State);
            Assert.Equal("task-a", restored.Items.Single(item => item.EntryKey == "a").TaskId);
            Assert.Equal(CollectionSubscriptionItemState.Failed, restored.Items.Single(item => item.EntryKey == "c").State);
            Assert.Equal(1, restored.PendingNewCount);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task StateUpdates_WithExpectedTaskId_DoNotLetAnOlderTaskOverwriteANewerTask()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-state-cas");
        try
        {
            using var service = new HistoryService(dbPath);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:state-cas"),
                [Entry("a", "A", 1)]);
            await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "a",
                    State = CollectionSubscriptionItemState.Queued,
                    TaskId = "new-task"
                }]);

            var staleAffected = await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "a",
                    State = CollectionSubscriptionItemState.Downloaded,
                    TaskId = "",
                    ExpectedTaskId = "old-task"
                }]);
            Assert.Equal(0, staleAffected);
            var afterStale = await service.GetCollectionSubscriptionAsync(stored.Id);
            var stillQueued = Assert.Single(afterStale!.Items);
            Assert.Equal(CollectionSubscriptionItemState.Queued, stillQueued.State);
            Assert.Equal("new-task", stillQueued.TaskId);

            var currentAffected = await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "a",
                    State = CollectionSubscriptionItemState.Downloaded,
                    TaskId = "",
                    ExpectedTaskId = "new-task"
                }]);
            Assert.Equal(1, currentAffected);
            var completed = Assert.Single((await service.GetCollectionSubscriptionAsync(stored.Id))!.Items);
            Assert.Equal(CollectionSubscriptionItemState.Downloaded, completed.State);
            Assert.Equal("", completed.TaskId);

            var emptyGuardAffected = await service.UpdateCollectionSubscriptionItemStatesAsync(
                stored.Id,
                [new CollectionSubscriptionItemStateUpdate
                {
                    EntryKey = "a",
                    State = CollectionSubscriptionItemState.New,
                    ExpectedTaskId = ""
                }]);
            Assert.Equal(1, emptyGuardAffected);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ScheduleRead_DoesNotLoadItemSnapshots()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-schedules");
        try
        {
            using var service = new HistoryService(dbPath);
            var stored = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("example:collection:schedule"),
                [Entry("a", "A", 1), Entry("b", "B", 2)]);

            var schedule = Assert.Single(await service.GetCollectionSubscriptionSchedulesAsync());
            Assert.Equal(stored.Id, schedule.Id);
            Assert.Equal(stored.NextCheckUtc, schedule.NextCheckUtc);
            Assert.Empty(schedule.Items);
            Assert.Equal(0, schedule.PendingNewCount);
            Assert.Equal(2, (await service.GetCollectionSubscriptionAsync(stored.Id))!.Items.Count);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Upsert_FromTwoHistoryServices_LeavesOneCanonicalSubscription()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-concurrent-upsert");
        try
        {
            using var first = new HistoryService(dbPath);
            using var second = new HistoryService(dbPath);
            var firstSubscription = CreateSubscription("example:collection:shared");
            var secondSubscription = CreateSubscription("example:collection:shared");
            secondSubscription.Title = "Second writer";

            var stored = await Task.WhenAll(
                first.UpsertCollectionSubscriptionAsync(
                    firstSubscription,
                    [Entry("a", "A", 1)]),
                second.UpsertCollectionSubscriptionAsync(
                    secondSubscription,
                    [Entry("a", "A", 1), Entry("b", "B", 2)]));

            Assert.Equal(stored[0].Id, stored[1].Id);
            var subscriptions = await first.GetCollectionSubscriptionsAsync();
            Assert.Single(subscriptions);
            Assert.Equal(2, subscriptions[0].Items.Count);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Initialization_MigratesOlderCollectionTablesWithoutLosingRows()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-migration");
        try
        {
            await CreateLegacyCollectionTablesAsync(dbPath);
            using var service = new HistoryService(dbPath);

            var migrated = await service.UpsertCollectionSubscriptionAsync(
                CreateSubscription("legacy:key"),
                [Entry("legacy-entry", "Migrated entry", 1)]);

            Assert.Equal(17, migrated.Id);
            var item = Assert.Single(migrated.Items);
            Assert.Equal(23, item.Id);
            Assert.Equal("Migrated entry", item.Title);
            Assert.Equal(CollectionSubscriptionItemState.Known, item.State);
            Assert.True(item.IsPresent);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Initialization_MergesDuplicateSubscriptionsAndItemsBeforeAddingUniqueIndexes()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-collection-deduplicate");
        try
        {
            await CreateDuplicateCollectionTablesAsync(dbPath);
            using var service = new HistoryService(dbPath);

            var subscription = Assert.Single(await service.GetCollectionSubscriptionsAsync());
            Assert.Equal(18, subscription.Id);
            Assert.Equal("new-batch", subscription.BatchId);
            Assert.Equal(2, subscription.Items.Count);
            var merged = subscription.Items.Single(item => item.EntryKey == "same-entry");
            Assert.Equal(CollectionSubscriptionItemState.Downloaded, merged.State);
            Assert.Equal("Newest metadata", merged.Title);
            Assert.Equal("", merged.TaskId);

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var duplicateSubscription = connection.CreateCommand();
            duplicateSubscription.CommandText = "INSERT INTO collection_subscriptions (canonical_key) VALUES ('duplicate:key')";
            await Assert.ThrowsAsync<SqliteException>(() => duplicateSubscription.ExecuteNonQueryAsync());

            await using var duplicateItem = connection.CreateCommand();
            duplicateItem.CommandText = "INSERT INTO collection_subscription_items (subscription_id, entry_key) VALUES (18, 'same-entry')";
            await Assert.ThrowsAsync<SqliteException>(() => duplicateItem.ExecuteNonQueryAsync());
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    private static CollectionSubscription CreateSubscription(string canonicalKey)
        => new()
        {
            CanonicalKey = canonicalKey,
            SourceUrl = "https://example.com/collection/1",
            Platform = "Example",
            Title = "Nginx course",
            OutputDirectory = @"D:\Videos\Nginx",
            Format = "mp4",
            Quality = "best",
            Subtitle = "all",
            BatchId = "history-group",
            BatchName = "Nginx course",
            AutoCheckEnabled = true,
            CheckInterval = TimeSpan.FromHours(6)
        };

    private static CollectionSubscriptionItemSnapshot Entry(string key, string title, int position)
        => new()
        {
            EntryKey = key,
            EntryId = key,
            Url = $"https://example.com/video/{key}",
            Title = title,
            Position = position
        };

    private static async Task CreateLegacyCollectionTablesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE collection_subscriptions (
                id INTEGER PRIMARY KEY,
                canonical_key TEXT NOT NULL
            );
            INSERT INTO collection_subscriptions (id, canonical_key) VALUES (17, 'legacy:key');
            CREATE TABLE collection_subscription_items (
                id INTEGER PRIMARY KEY,
                subscription_id INTEGER NOT NULL,
                entry_key TEXT NOT NULL
            );
            INSERT INTO collection_subscription_items (id, subscription_id, entry_key)
            VALUES (23, 17, 'legacy-entry');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDuplicateCollectionTablesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE collection_subscriptions (
                id INTEGER PRIMARY KEY,
                canonical_key TEXT NOT NULL,
                source_url TEXT NOT NULL DEFAULT '',
                batch_id TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO collection_subscriptions
                (id, canonical_key, source_url, batch_id, updated_at_utc)
            VALUES
                (17, 'duplicate:key', 'https://example.test/old', 'old-batch', '2026-08-29T00:00:00.0000000+00:00'),
                (18, 'duplicate:key', 'https://example.test/new', 'new-batch', '2026-08-30T00:00:00.0000000+00:00');
            CREATE INDEX idx_collection_subscriptions_canonical_key
            ON collection_subscriptions(canonical_key);

            CREATE TABLE collection_subscription_items (
                id INTEGER PRIMARY KEY,
                subscription_id INTEGER NOT NULL,
                entry_key TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                state TEXT NOT NULL DEFAULT 'Known',
                is_present INTEGER NOT NULL DEFAULT 1,
                updated_at_utc TEXT NOT NULL DEFAULT '',
                task_id TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO collection_subscription_items
                (id, subscription_id, entry_key, title, state, is_present, updated_at_utc, task_id)
            VALUES
                (23, 17, 'same-entry', 'Old metadata', 'Downloaded', 1, '2026-08-29T00:00:00.0000000+00:00', ''),
                (24, 18, 'same-entry', 'Newest metadata', 'Failed', 1, '2026-08-30T00:00:00.0000000+00:00', 'old-task'),
                (25, 17, 'other-entry', 'Other', 'Known', 1, '2026-08-29T00:00:00.0000000+00:00', '');
            CREATE UNIQUE INDEX idx_collection_subscription_items_key
            ON collection_subscription_items(subscription_id, entry_key);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
