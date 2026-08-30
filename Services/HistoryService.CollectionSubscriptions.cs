using System.Globalization;
using EasyGet.Models;
using Microsoft.Data.Sqlite;

namespace EasyGet.Services;

public partial class HistoryService
{
    private const int DefaultCollectionCheckIntervalMinutes = 24 * 60;
    private const int MaximumCollectionCheckIntervalMinutes = 365 * 24 * 60;

    private const string CollectionSubscriptionColumns = "id, canonical_key, source_url, platform, title, output_directory, format, quality, subtitle, batch_id, batch_name, auto_check_enabled, check_interval_minutes, last_attempt_utc, last_success_utc, next_check_utc, failure_message, created_at_utc, updated_at_utc";
    private const string CollectionSubscriptionItemColumns = "id, subscription_id, entry_key, entry_id, url, title, position, local_sequence, state, is_present, first_seen_utc, last_seen_utc, state_changed_utc, updated_at_utc, task_id";

    private void InitializeCollectionSubscriptionDatabase()
    {
        using var transaction = _connection.BeginTransaction();
        using (var subscriptions = _connection.CreateCommand())
        {
            subscriptions.Transaction = transaction;
            subscriptions.CommandText = """
                CREATE TABLE IF NOT EXISTS collection_subscriptions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_key TEXT NOT NULL DEFAULT '',
                    source_url TEXT NOT NULL DEFAULT '',
                    platform TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL DEFAULT '',
                    output_directory TEXT NOT NULL DEFAULT '',
                    format TEXT NOT NULL DEFAULT 'mp4',
                    quality TEXT NOT NULL DEFAULT 'best',
                    subtitle TEXT NOT NULL DEFAULT 'none',
                    batch_id TEXT NOT NULL DEFAULT '',
                    batch_name TEXT NOT NULL DEFAULT '',
                    auto_check_enabled INTEGER NOT NULL DEFAULT 1,
                    check_interval_minutes INTEGER NOT NULL DEFAULT 1440,
                    last_attempt_utc TEXT,
                    last_success_utc TEXT,
                    next_check_utc TEXT,
                    failure_message TEXT NOT NULL DEFAULT '',
                    created_at_utc TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL DEFAULT ''
                )
                """;
            subscriptions.ExecuteNonQuery();
        }

        using (var items = _connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = """
                CREATE TABLE IF NOT EXISTS collection_subscription_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    subscription_id INTEGER NOT NULL DEFAULT 0,
                    entry_key TEXT NOT NULL DEFAULT '',
                    entry_id TEXT NOT NULL DEFAULT '',
                    url TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL DEFAULT '',
                    position INTEGER NOT NULL DEFAULT 0,
                    local_sequence INTEGER NOT NULL DEFAULT 0,
                    state TEXT NOT NULL DEFAULT 'Known',
                    is_present INTEGER NOT NULL DEFAULT 1,
                    first_seen_utc TEXT NOT NULL DEFAULT '',
                    last_seen_utc TEXT NOT NULL DEFAULT '',
                    state_changed_utc TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL DEFAULT '',
                    task_id TEXT NOT NULL DEFAULT ''
                )
                """;
            items.ExecuteNonQuery();
        }

        EnsureCollectionSubscriptionColumns(transaction);
        using (var dropLegacyIndexes = _connection.CreateCommand())
        {
            dropLegacyIndexes.Transaction = transaction;
            dropLegacyIndexes.CommandText = """
                DROP INDEX IF EXISTS idx_collection_subscriptions_canonical_key;
                DROP INDEX IF EXISTS idx_collection_subscription_items_key;
                """;
            dropLegacyIndexes.ExecuteNonQuery();
        }

        NormalizeLegacyCollectionKeys(transaction);
        MergeDuplicateCollectionSubscriptions(transaction);
        MergeDuplicateCollectionItems(transaction);

        using var indexes = _connection.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = """
            CREATE UNIQUE INDEX idx_collection_subscriptions_canonical_key
            ON collection_subscriptions (canonical_key);
            CREATE INDEX IF NOT EXISTS idx_collection_subscriptions_due
            ON collection_subscriptions (auto_check_enabled, next_check_utc);
            CREATE UNIQUE INDEX idx_collection_subscription_items_key
            ON collection_subscription_items (subscription_id, entry_key);
            CREATE INDEX IF NOT EXISTS idx_collection_subscription_items_pending
            ON collection_subscription_items (subscription_id, is_present, state);
            """;
        indexes.ExecuteNonQuery();
        transaction.Commit();
    }

    private void EnsureCollectionSubscriptionColumns(SqliteTransaction transaction)
    {
        EnsureCollectionTableColumns("collection_subscriptions", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["canonical_key"] = "TEXT NOT NULL DEFAULT ''",
            ["source_url"] = "TEXT NOT NULL DEFAULT ''",
            ["platform"] = "TEXT NOT NULL DEFAULT ''",
            ["title"] = "TEXT NOT NULL DEFAULT ''",
            ["output_directory"] = "TEXT NOT NULL DEFAULT ''",
            ["format"] = "TEXT NOT NULL DEFAULT 'mp4'",
            ["quality"] = "TEXT NOT NULL DEFAULT 'best'",
            ["subtitle"] = "TEXT NOT NULL DEFAULT 'none'",
            ["batch_id"] = "TEXT NOT NULL DEFAULT ''",
            ["batch_name"] = "TEXT NOT NULL DEFAULT ''",
            ["auto_check_enabled"] = "INTEGER NOT NULL DEFAULT 1",
            ["check_interval_minutes"] = "INTEGER NOT NULL DEFAULT 1440",
            ["last_attempt_utc"] = "TEXT",
            ["last_success_utc"] = "TEXT",
            ["next_check_utc"] = "TEXT",
            ["failure_message"] = "TEXT NOT NULL DEFAULT ''",
            ["created_at_utc"] = "TEXT NOT NULL DEFAULT ''",
            ["updated_at_utc"] = "TEXT NOT NULL DEFAULT ''"
        }, transaction);

        EnsureCollectionTableColumns("collection_subscription_items", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["subscription_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["entry_key"] = "TEXT NOT NULL DEFAULT ''",
            ["entry_id"] = "TEXT NOT NULL DEFAULT ''",
            ["url"] = "TEXT NOT NULL DEFAULT ''",
            ["title"] = "TEXT NOT NULL DEFAULT ''",
            ["position"] = "INTEGER NOT NULL DEFAULT 0",
            ["local_sequence"] = "INTEGER NOT NULL DEFAULT 0",
            ["state"] = "TEXT NOT NULL DEFAULT 'Known'",
            ["is_present"] = "INTEGER NOT NULL DEFAULT 1",
            ["first_seen_utc"] = "TEXT NOT NULL DEFAULT ''",
            ["last_seen_utc"] = "TEXT NOT NULL DEFAULT ''",
            ["state_changed_utc"] = "TEXT NOT NULL DEFAULT ''",
            ["updated_at_utc"] = "TEXT NOT NULL DEFAULT ''",
            ["task_id"] = "TEXT NOT NULL DEFAULT ''"
        }, transaction);
    }

    private void EnsureCollectionTableColumns(
        string tableName,
        IReadOnlyDictionary<string, string> requiredColumns,
        SqliteTransaction transaction)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(ReadString(reader, "name"));
        }

        foreach (var column in requiredColumns)
        {
            if (existingColumns.Contains(column.Key))
                continue;

            using var alter = _connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {column.Key} {column.Value}";
            alter.ExecuteNonQuery();
        }
    }

    private void NormalizeLegacyCollectionKeys(SqliteTransaction transaction)
    {
        using var normalize = _connection.CreateCommand();
        normalize.Transaction = transaction;
        normalize.CommandText = """
            UPDATE collection_subscriptions
            SET canonical_key = CASE
                WHEN TRIM(COALESCE(canonical_key, '')) = '' THEN 'legacy:subscription:' || id
                ELSE TRIM(canonical_key)
            END;
            UPDATE collection_subscription_items
            SET entry_key = CASE
                WHEN TRIM(COALESCE(entry_key, '')) = '' THEN 'legacy:item:' || id
                ELSE TRIM(entry_key)
            END;
            """;
        normalize.ExecuteNonQuery();
    }

    private void MergeDuplicateCollectionSubscriptions(SqliteTransaction transaction)
    {
        var duplicateKeys = new List<string>();
        using (var duplicates = _connection.CreateCommand())
        {
            duplicates.Transaction = transaction;
            duplicates.CommandText = """
                SELECT canonical_key
                FROM collection_subscriptions
                GROUP BY canonical_key
                HAVING COUNT(*) > 1
                """;
            using var reader = duplicates.ExecuteReader();
            while (reader.Read())
                duplicateKeys.Add(ReadString(reader, 0));
        }

        foreach (var canonicalKey in duplicateKeys)
        {
            var rows = new List<CollectionSubscription>();
            using (var read = _connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = $"SELECT {CollectionSubscriptionColumns} FROM collection_subscriptions WHERE canonical_key = $canonicalKey";
                read.Parameters.AddWithValue("$canonicalKey", canonicalKey);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                    rows.Add(ReadCollectionSubscription(reader));
            }

            var ordered = rows
                .OrderByDescending(row => row.UpdatedAtUtc)
                .ThenByDescending(row => row.LastSuccessUtc)
                .ThenByDescending(row => row.Id)
                .ToList();
            var keeper = ordered[0];
            keeper.SourceUrl = FirstNonEmptyCollectionValue(ordered, row => row.SourceUrl);
            keeper.Platform = FirstNonEmptyCollectionValue(ordered, row => row.Platform);
            keeper.Title = FirstNonEmptyCollectionValue(ordered, row => row.Title);
            keeper.OutputDirectory = FirstNonEmptyCollectionValue(ordered, row => row.OutputDirectory);
            keeper.Format = FirstNonEmptyCollectionValue(ordered, row => row.Format, "mp4");
            keeper.Quality = FirstNonEmptyCollectionValue(ordered, row => row.Quality, "best");
            keeper.Subtitle = FirstNonEmptyCollectionValue(ordered, row => row.Subtitle, "none");
            keeper.BatchId = FirstNonEmptyCollectionValue(ordered, row => row.BatchId);
            keeper.BatchName = FirstNonEmptyCollectionValue(ordered, row => row.BatchName);
            keeper.LastAttemptUtc = ordered.Max(row => row.LastAttemptUtc);
            keeper.LastSuccessUtc = ordered.Max(row => row.LastSuccessUtc);
            keeper.NextCheckUtc = ordered.Max(row => row.NextCheckUtc);
            keeper.FailureMessage = ordered
                .OrderByDescending(row => row.LastAttemptUtc)
                .Select(row => row.FailureMessage)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            keeper.CreatedAtUtc = MinNonDefaultCollectionDate(ordered.Select(row => row.CreatedAtUtc));
            keeper.UpdatedAtUtc = ordered.Max(row => row.UpdatedAtUtc);

            foreach (var duplicate in rows.Where(row => row.Id != keeper.Id))
            {
                using (var moveItems = _connection.CreateCommand())
                {
                    moveItems.Transaction = transaction;
                    moveItems.CommandText = "UPDATE collection_subscription_items SET subscription_id = $keeperId WHERE subscription_id = $duplicateId";
                    moveItems.Parameters.AddWithValue("$keeperId", keeper.Id);
                    moveItems.Parameters.AddWithValue("$duplicateId", duplicate.Id);
                    moveItems.ExecuteNonQuery();
                }

                using var delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM collection_subscriptions WHERE id = $id";
                delete.Parameters.AddWithValue("$id", duplicate.Id);
                delete.ExecuteNonQuery();
            }

            WriteMergedCollectionSubscription(keeper, transaction);
        }
    }

    private void WriteMergedCollectionSubscription(
        CollectionSubscription subscription,
        SqliteTransaction transaction)
    {
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE collection_subscriptions
            SET source_url = $sourceUrl,
                platform = $platform,
                title = $title,
                output_directory = $outputDirectory,
                format = $format,
                quality = $quality,
                subtitle = $subtitle,
                batch_id = $batchId,
                batch_name = $batchName,
                auto_check_enabled = $autoCheckEnabled,
                check_interval_minutes = $checkIntervalMinutes,
                last_attempt_utc = $lastAttemptUtc,
                last_success_utc = $lastSuccessUtc,
                next_check_utc = $nextCheckUtc,
                failure_message = $failureMessage,
                created_at_utc = $createdAtUtc,
                updated_at_utc = $updatedAtUtc
            WHERE id = $id
            """;
        AddCollectionSubscriptionSettingsParameters(update, subscription);
        update.Parameters.AddWithValue("$lastAttemptUtc", ToCollectionDbValue(subscription.LastAttemptUtc));
        update.Parameters.AddWithValue("$lastSuccessUtc", ToCollectionDbValue(subscription.LastSuccessUtc));
        update.Parameters.AddWithValue("$nextCheckUtc", ToCollectionDbValue(subscription.NextCheckUtc));
        update.Parameters.AddWithValue("$failureMessage", subscription.FailureMessage);
        update.Parameters.AddWithValue("$createdAtUtc", FormatCollectionDateOrEmpty(subscription.CreatedAtUtc));
        update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDateOrEmpty(subscription.UpdatedAtUtc));
        update.Parameters.AddWithValue("$id", subscription.Id);
        update.ExecuteNonQuery();
    }

    private void MergeDuplicateCollectionItems(SqliteTransaction transaction)
    {
        var duplicateKeys = new List<(long SubscriptionId, string EntryKey)>();
        using (var duplicates = _connection.CreateCommand())
        {
            duplicates.Transaction = transaction;
            duplicates.CommandText = """
                SELECT subscription_id, entry_key
                FROM collection_subscription_items
                GROUP BY subscription_id, entry_key
                HAVING COUNT(*) > 1
                """;
            using var reader = duplicates.ExecuteReader();
            while (reader.Read())
            {
                duplicateKeys.Add((
                    ReadNonNegativeInt64(reader, 0),
                    ReadString(reader, 1)));
            }
        }

        foreach (var duplicateKey in duplicateKeys)
        {
            var rows = new List<CollectionSubscriptionItem>();
            using (var read = _connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = $"SELECT {CollectionSubscriptionItemColumns} FROM collection_subscription_items WHERE subscription_id = $subscriptionId AND entry_key = $entryKey";
                read.Parameters.AddWithValue("$subscriptionId", duplicateKey.SubscriptionId);
                read.Parameters.AddWithValue("$entryKey", duplicateKey.EntryKey);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                    rows.Add(ReadCollectionSubscriptionItem(reader));
            }

            var stateWinner = rows
                .OrderByDescending(row => GetCollectionItemMigrationStatePriority(row.State))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.TaskId))
                .ThenByDescending(row => row.StateChangedUtc)
                .ThenByDescending(row => row.Id)
                .First();
            var metadataRows = rows
                .OrderByDescending(row => row.UpdatedAtUtc)
                .ThenByDescending(row => row.Id)
                .ToList();
            var metadataWinner = metadataRows[0];
            var merged = new CollectionSubscriptionItem
            {
                Id = stateWinner.Id,
                SubscriptionId = duplicateKey.SubscriptionId,
                EntryKey = duplicateKey.EntryKey,
                EntryId = FirstNonEmptyCollectionValue(metadataRows, row => row.EntryId),
                Url = FirstNonEmptyCollectionValue(metadataRows, row => row.Url),
                Title = FirstNonEmptyCollectionValue(metadataRows, row => row.Title),
                Position = metadataWinner.Position,
                LocalSequence = rows.Where(row => row.LocalSequence > 0)
                    .Select(row => row.LocalSequence)
                    .DefaultIfEmpty(metadataWinner.LocalSequence)
                    .Min(),
                State = stateWinner.State,
                IsPresent = rows.Any(row => row.IsPresent),
                FirstSeenUtc = MinNonDefaultCollectionDate(rows.Select(row => row.FirstSeenUtc)),
                LastSeenUtc = rows.Max(row => row.LastSeenUtc),
                StateChangedUtc = stateWinner.StateChangedUtc,
                UpdatedAtUtc = rows.Max(row => row.UpdatedAtUtc),
                TaskId = stateWinner.TaskId
            };

            WriteMergedCollectionItem(merged, transaction);
            foreach (var duplicate in rows.Where(row => row.Id != merged.Id))
            {
                using var delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM collection_subscription_items WHERE id = $id";
                delete.Parameters.AddWithValue("$id", duplicate.Id);
                delete.ExecuteNonQuery();
            }
        }
    }

    private void WriteMergedCollectionItem(
        CollectionSubscriptionItem item,
        SqliteTransaction transaction)
    {
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE collection_subscription_items
            SET subscription_id = $subscriptionId,
                entry_key = $entryKey,
                entry_id = $entryId,
                url = $url,
                title = $title,
                position = $position,
                local_sequence = $localSequence,
                state = $state,
                is_present = $isPresent,
                first_seen_utc = $firstSeenUtc,
                last_seen_utc = $lastSeenUtc,
                state_changed_utc = $stateChangedUtc,
                updated_at_utc = $updatedAtUtc,
                task_id = $taskId
            WHERE id = $id
            """;
        update.Parameters.AddWithValue("$subscriptionId", item.SubscriptionId);
        update.Parameters.AddWithValue("$entryKey", item.EntryKey);
        update.Parameters.AddWithValue("$entryId", item.EntryId);
        update.Parameters.AddWithValue("$url", item.Url);
        update.Parameters.AddWithValue("$title", item.Title);
        update.Parameters.AddWithValue("$position", item.Position);
        update.Parameters.AddWithValue("$localSequence", item.LocalSequence);
        update.Parameters.AddWithValue("$state", item.State.ToString());
        update.Parameters.AddWithValue("$isPresent", item.IsPresent ? 1 : 0);
        update.Parameters.AddWithValue("$firstSeenUtc", FormatCollectionDateOrEmpty(item.FirstSeenUtc));
        update.Parameters.AddWithValue("$lastSeenUtc", FormatCollectionDateOrEmpty(item.LastSeenUtc));
        update.Parameters.AddWithValue("$stateChangedUtc", FormatCollectionDateOrEmpty(item.StateChangedUtc));
        update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDateOrEmpty(item.UpdatedAtUtc));
        update.Parameters.AddWithValue("$taskId", item.TaskId);
        update.Parameters.AddWithValue("$id", item.Id);
        update.ExecuteNonQuery();
    }

    private static string FirstNonEmptyCollectionValue<T>(
        IEnumerable<T> values,
        Func<T, string> selector,
        string fallback = "")
        => values.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? fallback;

    private static DateTimeOffset MinNonDefaultCollectionDate(IEnumerable<DateTimeOffset> values)
        => values.Where(value => value != default).DefaultIfEmpty(DateTimeOffset.MinValue).Min();

    private static int GetCollectionItemMigrationStatePriority(CollectionSubscriptionItemState state)
        => state switch
        {
            CollectionSubscriptionItemState.Downloaded => 6,
            CollectionSubscriptionItemState.Queued => 5,
            CollectionSubscriptionItemState.Failed => 4,
            CollectionSubscriptionItemState.New => 3,
            CollectionSubscriptionItemState.Ignored => 2,
            _ => 1
        };

    public Task<List<CollectionSubscription>> GetCollectionSubscriptionsAsync()
        => WithConnectionAsync(() => ReadCollectionSubscriptionsCoreAsync());

    /// <summary>
    /// Reads subscription scheduling metadata without loading item snapshots.
    /// </summary>
    public Task<List<CollectionSubscription>> GetCollectionSubscriptionSchedulesAsync()
        => WithConnectionAsync(() => ReadCollectionSubscriptionSchedulesCoreAsync());

    public Task<CollectionSubscription?> GetCollectionSubscriptionAsync(long subscriptionId)
    {
        if (subscriptionId <= 0)
            return Task.FromResult<CollectionSubscription?>(null);

        return WithConnectionAsync(() => ReadCollectionSubscriptionCoreAsync(subscriptionId));
    }

    public Task<CollectionSubscription?> GetCollectionSubscriptionByCanonicalKeyAsync(string canonicalKey)
    {
        var normalizedKey = NormalizeRequiredCollectionText(canonicalKey, nameof(canonicalKey));
        return WithConnectionAsync(() => ReadCollectionSubscriptionByCanonicalKeyCoreAsync(normalizedKey));
    }

    public Task<List<CollectionSubscription>> GetDueCollectionSubscriptionsAsync(DateTimeOffset? asOfUtc = null)
        => WithConnectionAsync(async () =>
        {
            var now = NormalizeUtc(asOfUtc ?? DateTimeOffset.UtcNow);
            var subscriptions = await ReadCollectionSubscriptionSchedulesCoreAsync();
            return subscriptions
                .Where(subscription => subscription.AutoCheckEnabled
                    && (subscription.NextCheckUtc is null || subscription.NextCheckUtc <= now))
                .ToList();
        });

    public Task<CollectionSubscription> UpsertCollectionSubscriptionAsync(
        CollectionSubscription subscription,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> baselineItems,
        DateTimeOffset? observedAtUtc = null)
        => UpsertCollectionSubscriptionCoreAsync(
            subscription,
            baselineItems,
            [],
            observedAtUtc);

    public Task<CollectionSubscription> UpsertCollectionSubscriptionAsync(
        CollectionSubscription subscription,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> baselineItems,
        IReadOnlyCollection<CollectionSubscriptionItemStateUpdate> initialItemStates,
        DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(baselineItems);
        ArgumentNullException.ThrowIfNull(initialItemStates);
        return UpsertCollectionSubscriptionCoreAsync(
            subscription,
            baselineItems,
            initialItemStates,
            observedAtUtc);
    }

    private Task<CollectionSubscription> UpsertCollectionSubscriptionCoreAsync(
        CollectionSubscription subscription,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> baselineItems,
        IReadOnlyCollection<CollectionSubscriptionItemStateUpdate> initialItemStates,
        DateTimeOffset? observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(baselineItems);
        var normalized = NormalizeCollectionSubscription(subscription);
        var snapshot = NormalizeCollectionSnapshot(baselineItems);
        var normalizedInitialStates = NormalizeCollectionStateUpdates(initialItemStates);
        var observedAt = NormalizeUtc(observedAtUtc ?? DateTimeOffset.UtcNow);

        return WithConnectionAsync(async () =>
        {
            using var transaction = _connection.BeginTransaction();
            var insertedId = await InsertCollectionSubscriptionCoreAsync(normalized, observedAt, transaction);
            var existing = insertedId is null
                ? await ReadCollectionSubscriptionByCanonicalKeyCoreAsync(normalized.CanonicalKey, transaction)
                    ?? throw new InvalidOperationException("The conflicting collection subscription could not be read.")
                : null;
            var id = insertedId ?? existing!.Id;
            normalized.Id = id;
            if (existing is null)
            {
                normalized.BatchId = ResolveBatchId(normalized.BatchId, null, id);
                normalized.BatchName = ResolveBatchName(normalized.BatchName, null, normalized.Title);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(normalized.Platform))
                    normalized.Platform = existing.Platform;
                if (string.IsNullOrWhiteSpace(normalized.Title))
                    normalized.Title = existing.Title;
                if (!string.IsNullOrWhiteSpace(existing.OutputDirectory))
                    normalized.OutputDirectory = existing.OutputDirectory;
                normalized.Format = existing.Format;
                normalized.Quality = existing.Quality;
                normalized.Subtitle = existing.Subtitle;
                normalized.AutoCheckEnabled = existing.AutoCheckEnabled;
                normalized.CheckInterval = existing.CheckInterval;
                normalized.BatchId = ResolveBatchId(existing.BatchId, normalized.BatchId, id);
                normalized.BatchName = ResolveBatchName(existing.BatchName, normalized.BatchName, normalized.Title);
            }

            await WriteCollectionSubscriptionCoreAsync(
                normalized,
                observedAt,
                observedAt,
                CalculateNextCheckUtc(normalized.AutoCheckEnabled, observedAt, normalized.CheckInterval),
                "",
                transaction);
            await ApplyCollectionItemsCoreAsync(
                id,
                snapshot,
                observedAt,
                existing is null
                    ? CollectionSubscriptionItemState.Known
                    : CollectionSubscriptionItemState.New,
                transaction);
            var initialStateCount = await UpdateCollectionSubscriptionItemStatesCoreAsync(
                id,
                normalizedInitialStates,
                observedAt,
                transaction);
            if (initialStateCount != normalizedInitialStates.Count)
            {
                throw new InvalidOperationException(
                    "One or more initial collection item states did not match the stored baseline.");
            }
            transaction.Commit();

            subscription.Id = id;
            var stored = await ReadCollectionSubscriptionCoreAsync(id)
                ?? throw new InvalidOperationException("The collection subscription could not be read after it was stored.");
            return stored;
        });
    }

    public Task<CollectionSubscriptionRefreshResult> ApplyCollectionSubscriptionSnapshotAsync(
        long subscriptionId,
        IReadOnlyCollection<CollectionSubscriptionItemSnapshot> snapshotItems,
        string? title = null,
        DateTimeOffset? attemptedAtUtc = null)
    {
        if (subscriptionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(subscriptionId));
        ArgumentNullException.ThrowIfNull(snapshotItems);
        var snapshot = NormalizeCollectionSnapshot(snapshotItems);
        var attemptedAt = NormalizeUtc(attemptedAtUtc ?? DateTimeOffset.UtcNow);

        return WithConnectionAsync(async () =>
        {
            using var transaction = _connection.BeginTransaction();
            var subscription = await ReadCollectionSubscriptionCoreAsync(subscriptionId, transaction)
                ?? throw new KeyNotFoundException($"Collection subscription {subscriptionId} does not exist.");
            var newKeys = await ApplyCollectionItemsCoreAsync(
                subscriptionId,
                snapshot,
                attemptedAt,
                CollectionSubscriptionItemState.New,
                transaction);

            var refreshedTitle = string.IsNullOrWhiteSpace(title) ? subscription.Title : title.Trim();
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE collection_subscriptions
                    SET title = $title,
                        last_attempt_utc = $lastAttemptUtc,
                        last_success_utc = $lastSuccessUtc,
                        next_check_utc = $nextCheckUtc,
                        failure_message = '',
                        updated_at_utc = $updatedAtUtc
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$title", refreshedTitle);
                update.Parameters.AddWithValue("$lastAttemptUtc", FormatCollectionDate(attemptedAt));
                update.Parameters.AddWithValue("$lastSuccessUtc", FormatCollectionDate(attemptedAt));
                update.Parameters.AddWithValue(
                    "$nextCheckUtc",
                    ToCollectionDbValue(CalculateNextCheckUtc(
                        subscription.AutoCheckEnabled,
                        attemptedAt,
                        subscription.CheckInterval)));
                update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDate(attemptedAt));
                update.Parameters.AddWithValue("$id", subscriptionId);
                await update.ExecuteNonQueryAsync();
            }

            transaction.Commit();
            var stored = await ReadCollectionSubscriptionCoreAsync(subscriptionId)
                ?? throw new InvalidOperationException("The refreshed collection subscription could not be read.");
            return new CollectionSubscriptionRefreshResult
            {
                Subscription = stored,
                NewItems = stored.Items.Where(item => newKeys.Contains(item.EntryKey)).ToList()
            };
        });
    }

    public Task<CollectionSubscription> RecordCollectionSubscriptionRefreshFailureAsync(
        long subscriptionId,
        string failureMessage,
        DateTimeOffset? attemptedAtUtc = null)
    {
        if (subscriptionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(subscriptionId));
        var attemptedAt = NormalizeUtc(attemptedAtUtc ?? DateTimeOffset.UtcNow);
        var normalizedFailure = (failureMessage ?? "").Trim();

        return WithConnectionAsync(async () =>
        {
            var subscription = await ReadCollectionSubscriptionCoreAsync(subscriptionId)
                ?? throw new KeyNotFoundException($"Collection subscription {subscriptionId} does not exist.");
            using var update = _connection.CreateCommand();
            update.CommandText = """
                UPDATE collection_subscriptions
                SET last_attempt_utc = $lastAttemptUtc,
                    next_check_utc = $nextCheckUtc,
                    failure_message = $failureMessage,
                    updated_at_utc = $updatedAtUtc
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$lastAttemptUtc", FormatCollectionDate(attemptedAt));
            update.Parameters.AddWithValue(
                "$nextCheckUtc",
                ToCollectionDbValue(CalculateNextCheckUtc(
                    subscription.AutoCheckEnabled,
                    attemptedAt,
                    subscription.CheckInterval)));
            update.Parameters.AddWithValue("$failureMessage", normalizedFailure);
            update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDate(attemptedAt));
            update.Parameters.AddWithValue("$id", subscriptionId);
            await update.ExecuteNonQueryAsync();
            return await ReadCollectionSubscriptionCoreAsync(subscriptionId)
                ?? throw new InvalidOperationException("The failed collection subscription refresh could not be read.");
        });
    }

    public Task<CollectionSubscription> UpdateCollectionSubscriptionAsync(CollectionSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(subscription), "A persisted subscription id is required.");
        var normalized = NormalizeCollectionSubscription(subscription);

        return WithConnectionAsync(async () =>
        {
            var existing = await ReadCollectionSubscriptionCoreAsync(normalized.Id)
                ?? throw new KeyNotFoundException($"Collection subscription {normalized.Id} does not exist.");
            var collision = await ReadCollectionSubscriptionByCanonicalKeyCoreAsync(normalized.CanonicalKey);
            if (collision is not null && collision.Id != normalized.Id)
                throw new InvalidOperationException("Another collection subscription already uses this canonical key.");

            normalized.BatchId = ResolveBatchId(normalized.BatchId, existing.BatchId, normalized.Id);
            normalized.BatchName = ResolveBatchName(normalized.BatchName, existing.BatchName, normalized.Title);
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? nextCheck;
            if (!normalized.AutoCheckEnabled)
            {
                nextCheck = null;
            }
            else if (!existing.AutoCheckEnabled || existing.NextCheckUtc is null)
            {
                nextCheck = now;
            }
            else if (GetCollectionCheckIntervalMinutes(normalized.CheckInterval)
                     != GetCollectionCheckIntervalMinutes(existing.CheckInterval))
            {
                nextCheck = CalculateNextCheckUtc(
                    true,
                    existing.LastAttemptUtc ?? now,
                    normalized.CheckInterval);
            }
            else
            {
                nextCheck = existing.NextCheckUtc;
            }
            await WriteCollectionSubscriptionSettingsCoreAsync(normalized, nextCheck, now);
            return await ReadCollectionSubscriptionCoreAsync(normalized.Id)
                ?? throw new InvalidOperationException("The updated collection subscription could not be read.");
        });
    }

    public Task<CollectionSubscription> UpdateCollectionSubscriptionSettingsAsync(CollectionSubscription subscription)
        => UpdateCollectionSubscriptionAsync(subscription);

    public Task<int> UpdateCollectionSubscriptionItemStatesAsync(
        long subscriptionId,
        IReadOnlyCollection<CollectionSubscriptionItemStateUpdate> updates,
        DateTimeOffset? changedAtUtc = null)
    {
        if (subscriptionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(subscriptionId));
        ArgumentNullException.ThrowIfNull(updates);
        var normalizedUpdates = NormalizeCollectionStateUpdates(updates);
        if (normalizedUpdates.Count == 0)
            return Task.FromResult(0);
        var changedAt = NormalizeUtc(changedAtUtc ?? DateTimeOffset.UtcNow);

        return WithConnectionAsync(async () =>
        {
            using var transaction = _connection.BeginTransaction();
            var affected = await UpdateCollectionSubscriptionItemStatesCoreAsync(
                subscriptionId,
                normalizedUpdates,
                changedAt,
                transaction);
            transaction.Commit();
            return affected;
        });
    }

    private async Task<int> UpdateCollectionSubscriptionItemStatesCoreAsync(
        long subscriptionId,
        IReadOnlyCollection<CollectionSubscriptionItemStateUpdate> updates,
        DateTimeOffset changedAtUtc,
        SqliteTransaction transaction)
    {
        var affected = 0;
        foreach (var stateUpdate in updates)
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE collection_subscription_items
                SET state_changed_utc = CASE WHEN state <> $state THEN $changedAtUtc ELSE state_changed_utc END,
                    state = $state,
                    task_id = CASE WHEN $replaceTaskId = 1 THEN $taskId ELSE task_id END,
                    updated_at_utc = $changedAtUtc
                WHERE subscription_id = $subscriptionId
                  AND entry_key = $entryKey
                  AND ($hasExpectedTaskId = 0 OR task_id = $expectedTaskId)
                """;
            update.Parameters.AddWithValue("$state", stateUpdate.State.ToString());
            update.Parameters.AddWithValue("$replaceTaskId", stateUpdate.TaskId is null ? 0 : 1);
            update.Parameters.AddWithValue("$taskId", stateUpdate.TaskId ?? "");
            update.Parameters.AddWithValue("$hasExpectedTaskId", stateUpdate.ExpectedTaskId is null ? 0 : 1);
            update.Parameters.AddWithValue("$expectedTaskId", stateUpdate.ExpectedTaskId ?? "");
            update.Parameters.AddWithValue("$changedAtUtc", FormatCollectionDate(changedAtUtc));
            update.Parameters.AddWithValue("$subscriptionId", subscriptionId);
            update.Parameters.AddWithValue("$entryKey", stateUpdate.EntryKey);
            affected += await update.ExecuteNonQueryAsync();
        }

        return affected;
    }

    public Task<int> UpdateCollectionSubscriptionItemStateAsync(
        long subscriptionId,
        string entryKey,
        CollectionSubscriptionItemState state,
        string? taskId = null,
        DateTimeOffset? changedAtUtc = null,
        string? expectedTaskId = null)
        => UpdateCollectionSubscriptionItemStatesAsync(
            subscriptionId,
            [new CollectionSubscriptionItemStateUpdate
            {
                EntryKey = entryKey,
                State = state,
                TaskId = taskId,
                ExpectedTaskId = expectedTaskId
            }],
            changedAtUtc);

    public Task<bool> DeleteCollectionSubscriptionAsync(long subscriptionId)
    {
        if (subscriptionId <= 0)
            return Task.FromResult(false);

        return WithConnectionAsync(async () =>
        {
            using var transaction = _connection.BeginTransaction();
            using (var deleteItems = _connection.CreateCommand())
            {
                deleteItems.Transaction = transaction;
                deleteItems.CommandText = "DELETE FROM collection_subscription_items WHERE subscription_id = $id";
                deleteItems.Parameters.AddWithValue("$id", subscriptionId);
                await deleteItems.ExecuteNonQueryAsync();
            }

            int deleted;
            using (var deleteSubscription = _connection.CreateCommand())
            {
                deleteSubscription.Transaction = transaction;
                deleteSubscription.CommandText = "DELETE FROM collection_subscriptions WHERE id = $id";
                deleteSubscription.Parameters.AddWithValue("$id", subscriptionId);
                deleted = await deleteSubscription.ExecuteNonQueryAsync();
            }

            transaction.Commit();
            return deleted > 0;
        });
    }

    private async Task<long?> InsertCollectionSubscriptionCoreAsync(
        CollectionSubscription subscription,
        DateTimeOffset observedAtUtc,
        SqliteTransaction transaction)
    {
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO collection_subscriptions (
                canonical_key, source_url, platform, title, output_directory, format, quality,
                subtitle, batch_id, batch_name, auto_check_enabled, check_interval_minutes,
                created_at_utc, updated_at_utc)
            VALUES (
                $canonicalKey, $sourceUrl, $platform, $title, $outputDirectory, $format, $quality,
                $subtitle, $batchId, $batchName, $autoCheckEnabled, $checkIntervalMinutes,
                $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(canonical_key) DO NOTHING
            RETURNING id
            """;
        AddCollectionSubscriptionSettingsParameters(insert, subscription);
        insert.Parameters.AddWithValue("$createdAtUtc", FormatCollectionDate(observedAtUtc));
        insert.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDate(observedAtUtc));
        var result = await insert.ExecuteScalarAsync();
        return result is long id && id > 0 ? id : null;
    }

    private async Task WriteCollectionSubscriptionCoreAsync(
        CollectionSubscription subscription,
        DateTimeOffset lastAttemptUtc,
        DateTimeOffset lastSuccessUtc,
        DateTimeOffset? nextCheckUtc,
        string failureMessage,
        SqliteTransaction transaction)
    {
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE collection_subscriptions
            SET canonical_key = $canonicalKey,
                source_url = $sourceUrl,
                platform = $platform,
                title = $title,
                output_directory = $outputDirectory,
                format = $format,
                quality = $quality,
                subtitle = $subtitle,
                batch_id = $batchId,
                batch_name = $batchName,
                auto_check_enabled = $autoCheckEnabled,
                check_interval_minutes = $checkIntervalMinutes,
                last_attempt_utc = $lastAttemptUtc,
                last_success_utc = $lastSuccessUtc,
                next_check_utc = $nextCheckUtc,
                failure_message = $failureMessage,
                updated_at_utc = $updatedAtUtc
            WHERE id = $id
            """;
        AddCollectionSubscriptionSettingsParameters(update, subscription);
        update.Parameters.AddWithValue("$lastAttemptUtc", FormatCollectionDate(lastAttemptUtc));
        update.Parameters.AddWithValue("$lastSuccessUtc", FormatCollectionDate(lastSuccessUtc));
        update.Parameters.AddWithValue("$nextCheckUtc", ToCollectionDbValue(nextCheckUtc));
        update.Parameters.AddWithValue("$failureMessage", failureMessage);
        update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDate(lastAttemptUtc));
        update.Parameters.AddWithValue("$id", subscription.Id);
        await update.ExecuteNonQueryAsync();
    }

    private async Task WriteCollectionSubscriptionSettingsCoreAsync(
        CollectionSubscription subscription,
        DateTimeOffset? nextCheckUtc,
        DateTimeOffset updatedAtUtc)
    {
        using var update = _connection.CreateCommand();
        update.CommandText = """
            UPDATE collection_subscriptions
            SET canonical_key = $canonicalKey,
                source_url = $sourceUrl,
                platform = $platform,
                title = $title,
                output_directory = $outputDirectory,
                format = $format,
                quality = $quality,
                subtitle = $subtitle,
                batch_id = $batchId,
                batch_name = $batchName,
                auto_check_enabled = $autoCheckEnabled,
                check_interval_minutes = $checkIntervalMinutes,
                next_check_utc = $nextCheckUtc,
                updated_at_utc = $updatedAtUtc
            WHERE id = $id
            """;
        AddCollectionSubscriptionSettingsParameters(update, subscription);
        update.Parameters.AddWithValue("$nextCheckUtc", ToCollectionDbValue(nextCheckUtc));
        update.Parameters.AddWithValue("$updatedAtUtc", FormatCollectionDate(updatedAtUtc));
        update.Parameters.AddWithValue("$id", subscription.Id);
        await update.ExecuteNonQueryAsync();
    }

    private static void AddCollectionSubscriptionSettingsParameters(
        SqliteCommand command,
        CollectionSubscription subscription)
    {
        command.Parameters.AddWithValue("$canonicalKey", subscription.CanonicalKey);
        command.Parameters.AddWithValue("$sourceUrl", subscription.SourceUrl);
        command.Parameters.AddWithValue("$platform", subscription.Platform);
        command.Parameters.AddWithValue("$title", subscription.Title);
        command.Parameters.AddWithValue("$outputDirectory", subscription.OutputDirectory);
        command.Parameters.AddWithValue("$format", subscription.Format);
        command.Parameters.AddWithValue("$quality", subscription.Quality);
        command.Parameters.AddWithValue("$subtitle", subscription.Subtitle);
        command.Parameters.AddWithValue("$batchId", subscription.BatchId);
        command.Parameters.AddWithValue("$batchName", subscription.BatchName);
        command.Parameters.AddWithValue("$autoCheckEnabled", subscription.AutoCheckEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$checkIntervalMinutes", GetCollectionCheckIntervalMinutes(subscription.CheckInterval));
    }

    private async Task<HashSet<string>> ApplyCollectionItemsCoreAsync(
        long subscriptionId,
        IReadOnlyList<CollectionSubscriptionItemSnapshot> snapshot,
        DateTimeOffset observedAtUtc,
        CollectionSubscriptionItemState stateForUnseenEntries,
        SqliteTransaction transaction)
    {
        var existingItems = await ReadCollectionItemsCoreAsync(subscriptionId, transaction);
        var existingByKey = existingItems.ToDictionary(item => item.EntryKey, StringComparer.Ordinal);
        var snapshotKeys = snapshot.Select(item => item.EntryKey).ToHashSet(StringComparer.Ordinal);
        var newKeys = new HashSet<string>(StringComparer.Ordinal);
        var nextLocalSequence = existingItems.Count == 0 ? 1 : existingItems.Max(item => item.LocalSequence) + 1;
        var timestamp = FormatCollectionDate(observedAtUtc);

        foreach (var entry in snapshot)
        {
            if (existingByKey.TryGetValue(entry.EntryKey, out var existing))
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE collection_subscription_items
                    SET entry_id = CASE WHEN $entryId = '' THEN entry_id ELSE $entryId END,
                        url = CASE WHEN $url = '' THEN url ELSE $url END,
                        title = CASE WHEN $title = '' THEN title ELSE $title END,
                        position = $position,
                        is_present = 1,
                        last_seen_utc = $lastSeenUtc,
                        updated_at_utc = $updatedAtUtc
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$entryId", entry.EntryId);
                update.Parameters.AddWithValue("$url", entry.Url);
                update.Parameters.AddWithValue("$title", entry.Title);
                update.Parameters.AddWithValue("$position", entry.Position);
                update.Parameters.AddWithValue("$lastSeenUtc", timestamp);
                update.Parameters.AddWithValue("$updatedAtUtc", timestamp);
                update.Parameters.AddWithValue("$id", existing.Id);
                await update.ExecuteNonQueryAsync();
                continue;
            }

            var localSequence = entry.LocalSequence > 0 ? entry.LocalSequence : nextLocalSequence;
            nextLocalSequence = Math.Max(nextLocalSequence, localSequence + 1);
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO collection_subscription_items (
                    subscription_id, entry_key, entry_id, url, title, position, local_sequence,
                    state, is_present, first_seen_utc, last_seen_utc, state_changed_utc,
                    updated_at_utc, task_id)
                VALUES (
                    $subscriptionId, $entryKey, $entryId, $url, $title, $position, $localSequence,
                    $state, 1, $firstSeenUtc, $lastSeenUtc, $stateChangedUtc,
                    $updatedAtUtc, '')
                """;
            insert.Parameters.AddWithValue("$subscriptionId", subscriptionId);
            insert.Parameters.AddWithValue("$entryKey", entry.EntryKey);
            insert.Parameters.AddWithValue("$entryId", entry.EntryId);
            insert.Parameters.AddWithValue("$url", entry.Url);
            insert.Parameters.AddWithValue("$title", entry.Title);
            insert.Parameters.AddWithValue("$position", entry.Position);
            insert.Parameters.AddWithValue("$localSequence", localSequence);
            insert.Parameters.AddWithValue("$state", stateForUnseenEntries.ToString());
            insert.Parameters.AddWithValue("$firstSeenUtc", timestamp);
            insert.Parameters.AddWithValue("$lastSeenUtc", timestamp);
            insert.Parameters.AddWithValue("$stateChangedUtc", timestamp);
            insert.Parameters.AddWithValue("$updatedAtUtc", timestamp);
            await insert.ExecuteNonQueryAsync();
            if (stateForUnseenEntries == CollectionSubscriptionItemState.New)
                newKeys.Add(entry.EntryKey);
        }

        foreach (var removed in existingItems.Where(item => !snapshotKeys.Contains(item.EntryKey) && item.IsPresent))
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE collection_subscription_items
                SET is_present = 0, updated_at_utc = $updatedAtUtc
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$updatedAtUtc", timestamp);
            update.Parameters.AddWithValue("$id", removed.Id);
            await update.ExecuteNonQueryAsync();
        }

        return newKeys;
    }

    private async Task<List<CollectionSubscription>> ReadCollectionSubscriptionsCoreAsync(
        SqliteTransaction? transaction = null)
    {
        var subscriptions = await ReadCollectionSubscriptionSchedulesCoreAsync(transaction);
        foreach (var subscription in subscriptions)
            subscription.Items = await ReadCollectionItemsCoreAsync(subscription.Id, transaction);
        return subscriptions;
    }

    private async Task<List<CollectionSubscription>> ReadCollectionSubscriptionSchedulesCoreAsync(
        SqliteTransaction? transaction = null)
    {
        var subscriptions = new List<CollectionSubscription>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {CollectionSubscriptionColumns} FROM collection_subscriptions ORDER BY title COLLATE NOCASE, id";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                subscriptions.Add(ReadCollectionSubscription(reader));
        }
        return subscriptions;
    }

    private async Task<CollectionSubscription?> ReadCollectionSubscriptionCoreAsync(
        long subscriptionId,
        SqliteTransaction? transaction = null)
    {
        CollectionSubscription? subscription = null;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {CollectionSubscriptionColumns} FROM collection_subscriptions WHERE id = $id LIMIT 1";
            command.Parameters.AddWithValue("$id", subscriptionId);
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                subscription = ReadCollectionSubscription(reader);
        }

        if (subscription is not null)
            subscription.Items = await ReadCollectionItemsCoreAsync(subscription.Id, transaction);
        return subscription;
    }

    private async Task<CollectionSubscription?> ReadCollectionSubscriptionByCanonicalKeyCoreAsync(
        string canonicalKey,
        SqliteTransaction? transaction = null)
    {
        CollectionSubscription? subscription = null;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {CollectionSubscriptionColumns} FROM collection_subscriptions WHERE canonical_key = $canonicalKey ORDER BY id LIMIT 1";
            command.Parameters.AddWithValue("$canonicalKey", canonicalKey);
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                subscription = ReadCollectionSubscription(reader);
        }

        if (subscription is not null)
            subscription.Items = await ReadCollectionItemsCoreAsync(subscription.Id, transaction);
        return subscription;
    }

    private async Task<List<CollectionSubscriptionItem>> ReadCollectionItemsCoreAsync(
        long subscriptionId,
        SqliteTransaction? transaction = null)
    {
        var items = new List<CollectionSubscriptionItem>();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {CollectionSubscriptionItemColumns} FROM collection_subscription_items WHERE subscription_id = $subscriptionId ORDER BY local_sequence, position, id";
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(ReadCollectionSubscriptionItem(reader));
        return items;
    }

    private static CollectionSubscription ReadCollectionSubscription(SqliteDataReader reader)
    {
        var intervalMinutes = (int)Math.Clamp(
            ReadNonNegativeInt64(reader, "check_interval_minutes"),
            1,
            MaximumCollectionCheckIntervalMinutes);
        return new CollectionSubscription
        {
            Id = ReadNonNegativeInt64(reader, "id"),
            CanonicalKey = ReadString(reader, "canonical_key"),
            SourceUrl = ReadString(reader, "source_url"),
            Platform = ReadString(reader, "platform"),
            Title = ReadString(reader, "title"),
            OutputDirectory = ReadString(reader, "output_directory"),
            Format = ReadString(reader, "format"),
            Quality = ReadString(reader, "quality"),
            Subtitle = ReadString(reader, "subtitle"),
            BatchId = ReadString(reader, "batch_id"),
            BatchName = ReadString(reader, "batch_name"),
            AutoCheckEnabled = ReadNonNegativeInt64(reader, "auto_check_enabled") != 0,
            CheckInterval = TimeSpan.FromMinutes(intervalMinutes),
            LastAttemptUtc = ParseCollectionDate(reader, "last_attempt_utc"),
            LastSuccessUtc = ParseCollectionDate(reader, "last_success_utc"),
            NextCheckUtc = ParseCollectionDate(reader, "next_check_utc"),
            FailureMessage = ReadString(reader, "failure_message"),
            CreatedAtUtc = ParseCollectionDate(reader, "created_at_utc") ?? DateTimeOffset.MinValue,
            UpdatedAtUtc = ParseCollectionDate(reader, "updated_at_utc") ?? DateTimeOffset.MinValue
        };
    }

    private static CollectionSubscriptionItem ReadCollectionSubscriptionItem(SqliteDataReader reader)
        => new()
        {
            Id = ReadNonNegativeInt64(reader, "id"),
            SubscriptionId = ReadNonNegativeInt64(reader, "subscription_id"),
            EntryKey = ReadString(reader, "entry_key"),
            EntryId = ReadString(reader, "entry_id"),
            Url = ReadString(reader, "url"),
            Title = ReadString(reader, "title"),
            Position = (int)Math.Min(int.MaxValue, ReadNonNegativeInt64(reader, "position")),
            LocalSequence = (int)Math.Min(int.MaxValue, ReadNonNegativeInt64(reader, "local_sequence")),
            State = ParseCollectionItemState(ReadString(reader, "state")),
            IsPresent = ReadNonNegativeInt64(reader, "is_present") != 0,
            FirstSeenUtc = ParseCollectionDate(reader, "first_seen_utc") ?? DateTimeOffset.MinValue,
            LastSeenUtc = ParseCollectionDate(reader, "last_seen_utc") ?? DateTimeOffset.MinValue,
            StateChangedUtc = ParseCollectionDate(reader, "state_changed_utc") ?? DateTimeOffset.MinValue,
            UpdatedAtUtc = ParseCollectionDate(reader, "updated_at_utc") ?? DateTimeOffset.MinValue,
            TaskId = ReadString(reader, "task_id")
        };

    private static CollectionSubscription NormalizeCollectionSubscription(CollectionSubscription source)
    {
        var canonicalKey = NormalizeRequiredCollectionText(source.CanonicalKey, nameof(source.CanonicalKey));
        var sourceUrl = NormalizeRequiredCollectionText(source.SourceUrl, nameof(source.SourceUrl));
        return new CollectionSubscription
        {
            Id = source.Id,
            CanonicalKey = canonicalKey,
            SourceUrl = sourceUrl,
            Platform = NormalizeCollectionText(source.Platform),
            Title = NormalizeCollectionText(source.Title),
            OutputDirectory = NormalizeCollectionText(source.OutputDirectory),
            Format = NormalizeCollectionText(source.Format, "mp4"),
            Quality = NormalizeCollectionText(source.Quality, "best"),
            Subtitle = NormalizeCollectionText(source.Subtitle, "none"),
            BatchId = NormalizeCollectionText(source.BatchId),
            BatchName = NormalizeCollectionText(source.BatchName),
            AutoCheckEnabled = source.AutoCheckEnabled,
            CheckInterval = TimeSpan.FromMinutes(GetCollectionCheckIntervalMinutes(source.CheckInterval))
        };
    }

    private static List<CollectionSubscriptionItemSnapshot> NormalizeCollectionSnapshot(
        IEnumerable<CollectionSubscriptionItemSnapshot> items)
    {
        var normalized = new List<CollectionSubscriptionItemSnapshot>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var source in items)
        {
            if (source is null)
                throw new ArgumentException("A collection snapshot cannot contain null entries.", nameof(items));
            ordinal++;
            var entryId = NormalizeCollectionText(source.EntryId);
            var url = NormalizeCollectionText(source.Url);
            var entryKey = NormalizeCollectionText(source.EntryKey);
            if (entryKey.Length == 0)
                entryKey = entryId.Length > 0 ? entryId : url;
            if (entryKey.Length == 0)
                throw new ArgumentException("Each collection entry needs a stable key, id, or URL.", nameof(items));
            if (!keys.Add(entryKey))
                throw new ArgumentException($"The collection snapshot contains duplicate entry key '{entryKey}'.", nameof(items));

            normalized.Add(new CollectionSubscriptionItemSnapshot
            {
                EntryKey = entryKey,
                EntryId = entryId,
                Url = url,
                Title = NormalizeCollectionText(source.Title),
                Position = source.Position > 0 ? source.Position : ordinal,
                LocalSequence = Math.Max(0, source.LocalSequence)
            });
        }

        return normalized;
    }

    private static List<CollectionSubscriptionItemStateUpdate> NormalizeCollectionStateUpdates(
        IEnumerable<CollectionSubscriptionItemStateUpdate> updates)
    {
        var byKey = new Dictionary<string, CollectionSubscriptionItemStateUpdate>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (update is null)
                throw new ArgumentException("A state update cannot be null.", nameof(updates));
            var key = NormalizeRequiredCollectionText(update.EntryKey, nameof(update.EntryKey));
            if (!Enum.IsDefined(update.State))
                throw new ArgumentOutOfRangeException(nameof(updates), "The collection item state is invalid.");
            byKey[key] = new CollectionSubscriptionItemStateUpdate
            {
                EntryKey = key,
                State = update.State,
                TaskId = update.TaskId?.Trim(),
                ExpectedTaskId = update.ExpectedTaskId?.Trim()
            };
        }

        return byKey.Values.ToList();
    }

    private static int GetCollectionCheckIntervalMinutes(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || double.IsNaN(interval.TotalMinutes))
            return DefaultCollectionCheckIntervalMinutes;
        return (int)Math.Clamp(
            Math.Ceiling(interval.TotalMinutes),
            1,
            MaximumCollectionCheckIntervalMinutes);
    }

    private static DateTimeOffset? CalculateNextCheckUtc(
        bool autoCheckEnabled,
        DateTimeOffset attemptedAtUtc,
        TimeSpan interval)
        => autoCheckEnabled
            ? attemptedAtUtc.AddMinutes(GetCollectionCheckIntervalMinutes(interval))
            : null;

    private static string ResolveBatchId(string requestedBatchId, string? existingBatchId, long subscriptionId)
    {
        if (!string.IsNullOrWhiteSpace(requestedBatchId))
            return requestedBatchId.Trim();
        if (!string.IsNullOrWhiteSpace(existingBatchId))
            return existingBatchId.Trim();
        return $"collection-subscription-{subscriptionId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string ResolveBatchName(string requestedBatchName, string? existingBatchName, string title)
    {
        if (!string.IsNullOrWhiteSpace(requestedBatchName))
            return requestedBatchName.Trim();
        return string.IsNullOrWhiteSpace(existingBatchName)
            ? title
            : existingBatchName.Trim();
    }

    private static string NormalizeRequiredCollectionText(string? value, string parameterName)
    {
        var normalized = NormalizeCollectionText(value);
        if (normalized.Length == 0)
            throw new ArgumentException("The value cannot be empty.", parameterName);
        return normalized;
    }

    private static string NormalizeCollectionText(string? value, string fallback = "")
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static string FormatCollectionDate(DateTimeOffset value)
        => NormalizeUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private static string FormatCollectionDateOrEmpty(DateTimeOffset value)
        => value == default ? "" : FormatCollectionDate(value);

    private static object ToCollectionDbValue(DateTimeOffset? value)
        => value is null ? DBNull.Value : FormatCollectionDate(value.Value);

    private static DateTimeOffset? ParseCollectionDate(SqliteDataReader reader, string columnName)
    {
        var value = ReadString(reader, columnName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static CollectionSubscriptionItemState ParseCollectionItemState(string value)
        => Enum.TryParse<CollectionSubscriptionItemState>(value, true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : CollectionSubscriptionItemState.Known;
}
