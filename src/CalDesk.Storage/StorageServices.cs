using CalDesk.Core.Services;

namespace CalDesk.Storage;

/// <summary>Public facade over the SQLite persistence layer. Construct a
/// <see cref="CalDeskDatabase"/>, call <see cref="CalDeskDatabase.InitializeAsync"/>,
/// then resolve the repositories you need from this object.</summary>
public sealed class StorageServices
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
}
