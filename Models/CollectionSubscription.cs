namespace EasyGet.Models;

/// <summary>
/// Persistent lifecycle state for one item discovered through a collection subscription.
/// </summary>
public enum CollectionSubscriptionItemState
{
    Known,
    New,
    Queued,
    Downloaded,
    Ignored,
    Failed
}

/// <summary>
/// A tracked collection and the complete set of entries ever observed for it.
/// </summary>
public sealed class CollectionSubscription
{
    public long Id { get; set; }

    public string CanonicalKey { get; set; } = "";

    public string SourceUrl { get; set; } = "";

    public string Platform { get; set; } = "";

    public string Title { get; set; } = "";

    public string OutputDirectory { get; set; } = "";

    public string Format { get; set; } = "mp4";

    public string Quality { get; set; } = "best";

    public string Subtitle { get; set; } = "none";

    /// <summary>
    /// Stable history-group identifier shared by the initial and subsequent downloads.
    /// </summary>
    public string BatchId { get; set; } = "";

    public string BatchName { get; set; } = "";

    public bool AutoCheckEnabled { get; set; } = true;

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromDays(1);

    public DateTimeOffset? LastAttemptUtc { get; set; }

    public DateTimeOffset? LastSuccessUtc { get; set; }

    public DateTimeOffset? NextCheckUtc { get; set; }

    public string FailureMessage { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<CollectionSubscriptionItem> Items { get; set; } = [];

    public int PendingNewCount => Items.Count(item =>
        item.IsPresent
        && item.State is CollectionSubscriptionItemState.New
            or CollectionSubscriptionItemState.Failed);
}

/// <summary>
/// One persistent entry in a tracked collection. Rows remain after remote removal so a later
/// reappearance cannot be mistaken for a newly published entry.
/// </summary>
public sealed class CollectionSubscriptionItem
{
    public long Id { get; set; }

    public long SubscriptionId { get; set; }

    public string EntryKey { get; set; } = "";

    public string EntryId { get; set; } = "";

    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>The current one-based position reported by the remote collection.</summary>
    public int Position { get; set; }

    /// <summary>A stable local sequence that is not changed when the author reorders a collection.</summary>
    public int LocalSequence { get; set; }

    public CollectionSubscriptionItemState State { get; set; } = CollectionSubscriptionItemState.Known;

    public bool IsPresent { get; set; } = true;

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset StateChangedUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public string TaskId { get; set; } = "";
}

/// <summary>
/// Remote metadata used to establish a baseline or apply a later collection snapshot.
/// </summary>
public sealed class CollectionSubscriptionItemSnapshot
{
    public string EntryKey { get; set; } = "";

    public string EntryId { get; set; } = "";

    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public int Position { get; set; }

    /// <summary>
    /// Optional explicit local sequence. Existing entries always retain their stored sequence.
    /// </summary>
    public int LocalSequence { get; set; }
}

/// <summary>
/// A requested item-state transition. A null TaskId preserves the stored task id; an empty value
/// explicitly clears it.
/// </summary>
public sealed class CollectionSubscriptionItemStateUpdate
{
    public string EntryKey { get; set; } = "";

    public CollectionSubscriptionItemState State { get; set; }

    public string? TaskId { get; set; }

    /// <summary>
    /// Optional compare-and-swap guard. Null disables the guard; any other value, including an
    /// empty string, requires the stored task id to match before the state can be changed.
    /// </summary>
    public string? ExpectedTaskId { get; set; }
}

public sealed class CollectionSubscriptionRefreshResult
{
    public required CollectionSubscription Subscription { get; init; }

    public required IReadOnlyList<CollectionSubscriptionItem> NewItems { get; init; }
}
