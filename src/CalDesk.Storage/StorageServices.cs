using CalDesk.Core.Services;

namespace CalDesk.Storage;

/// <summary>Public facade over the SQLite persistence layer. Construct a
/// <see cref="CalDeskDatabase"/>, call <see cref="CalDeskDatabase.InitializeAsync"/>,
/// then resolve the repositories you need from this object.
///
/// Also implements <see cref="Core.Services.ITransactionRunner"/> so application services
/// (e.g. <c>EventService</c>) can group multiple repository writes into one atomic SQLite
/// transaction.</summary>
public sealed class StorageServices : ITransactionRunner
{
    public StorageServices(CalDeskDatabase database)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        Accounts = new AccountRepository(database);
        Calendars = new CalendarRepository(database);
        Events = new EventRepository(database);
        SyncState = new SyncStateRepository(database);
        SyncOperations = new SyncOperationRepository(database);
    }

    public CalDeskDatabase Database { get; }

    public IAccountRepository Accounts { get; }
    public ICalendarRepository Calendars { get; }
    public IEventRepository Events { get; }
    public ISyncStateRepository SyncState { get; }
    public ISyncOperationRepository SyncOperations { get; }

    /// <summary>Runs <paramref name="work"/> inside a single SQLite transaction; repository
    /// calls made inside it automatically enlist on the shared connection.</summary>
    public Task RunInTransactionAsync(Func<Task> work, CancellationToken ct = default)
        => Database.RunInTransactionAsync(work, ct);
}
