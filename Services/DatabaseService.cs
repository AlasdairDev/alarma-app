// SQLCipher AES-256. key is random 32 bytes from RNG, kept in the Keystore via SecureStorage,
// never hardcoded / never in SharedPreferences.
// sqlite-net does parameterized queries everywhere so no SQLi (no raw SQL strings here).
// allowBackup=false in the manifest so adb can't pull the db file.
// semaphore + double-check in GetConnectionAsync so we only ever open one connection.

using AlarmaApp.Models;
using Microsoft.Maui.Storage;
using SQLite;

namespace AlarmaApp.Services;

public class DatabaseService
{
    private SQLiteAsyncConnection? _connection;
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    public DatabaseService()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_connection is not null) return _connection;

        await _initSemaphore.WaitAsync();
        try
        {
            if (_connection is not null) return _connection;

            var key = await GetOrCreateDatabaseKeyAsync();
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "alarma.db3");
            var connectionString = new SQLiteConnectionString(
                databasePath,
                storeDateTimeAsTicks: true,
                key: key);
            _connection = new SQLiteAsyncConnection(connectionString);
            return _connection;
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private static async Task<string> GetOrCreateDatabaseKeyAsync()
    {
        const string keyName = "alarma_db_key_v1";
        var existing = await SecureStorage.GetAsync(keyName);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        var keyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var newKey = Convert.ToBase64String(keyBytes);
        await SecureStorage.SetAsync(keyName, newKey);
        return newKey;
    }

    public async Task InitializeAsync()
    {
        var db = await GetConnectionAsync();
        try
        {
            await db.CreateTableAsync<TripHistory>();
            await db.CreateTableAsync<SavedRoute>();
            await db.CreateTableAsync<EmergencyContact>();
            await db.CreateTableAsync<BehavioralProfile>();
            await db.CreateTableAsync<GeocodeCache>();
        }
        catch (SQLiteException)
        {
            // Existing unencrypted database from a previous install — delete and recreate encrypted.
            await db.CloseAsync();
            _connection = null;
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "alarma.db3");
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            db = await GetConnectionAsync();
            await db.CreateTableAsync<TripHistory>();
            await db.CreateTableAsync<SavedRoute>();
            await db.CreateTableAsync<EmergencyContact>();
            await db.CreateTableAsync<BehavioralProfile>();
            await db.CreateTableAsync<GeocodeCache>();
        }
    }

    public async Task<List<TripHistory>> GetTripHistoryAsync(int limit = 20) =>
        await (await GetConnectionAsync())
            .Table<TripHistory>()
            .OrderByDescending(t => t.StartedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<int> SaveTripHistoryAsync(TripHistory item)
    {
        var db = await GetConnectionAsync();
        return item.Id == 0 ? await db.InsertAsync(item) : await db.UpdateAsync(item);
    }

    public async Task<int> ClearTripHistoryAsync() =>
        await (await GetConnectionAsync()).DeleteAllAsync<TripHistory>();

    public async Task<List<SavedRoute>> GetSavedRoutesAsync() =>
        await (await GetConnectionAsync()).Table<SavedRoute>().ToListAsync();

    public async Task<int> SaveRouteAsync(SavedRoute route)
    {
        var db = await GetConnectionAsync();
        return route.Id == 0 ? await db.InsertAsync(route) : await db.UpdateAsync(route);
    }

    public async Task<int> DeleteRouteAsync(SavedRoute route) =>
        await (await GetConnectionAsync()).DeleteAsync(route);

    public async Task<int> ClearSavedRoutesAsync() =>
        await (await GetConnectionAsync()).DeleteAllAsync<SavedRoute>();

    public async Task<List<EmergencyContact>> GetEmergencyContactsAsync() =>
        await (await GetConnectionAsync()).Table<EmergencyContact>().ToListAsync();

    public async Task<int> SaveEmergencyContactAsync(EmergencyContact contact)
    {
        var db = await GetConnectionAsync();
        return contact.Id == 0 ? await db.InsertAsync(contact) : await db.UpdateAsync(contact);
    }

    public async Task<int> DeleteEmergencyContactAsync(EmergencyContact contact) =>
        await (await GetConnectionAsync()).DeleteAsync(contact);

    public async Task<int> ClearEmergencyContactsAsync() =>
        await (await GetConnectionAsync()).DeleteAllAsync<EmergencyContact>();

    public async Task<List<BehavioralProfile>> GetBehavioralProfilesAsync() =>
        await (await GetConnectionAsync()).Table<BehavioralProfile>().ToListAsync();

    public async Task<int> SaveBehavioralProfileAsync(BehavioralProfile profile)
    {
        var db = await GetConnectionAsync();
        return profile.Id == 0 ? await db.InsertAsync(profile) : await db.UpdateAsync(profile);
    }

    public async Task<int> DeleteTripHistoryAsync(TripHistory history) =>
        await (await GetConnectionAsync()).DeleteAsync(history);

    public async Task<int> ClearBehavioralProfilesAsync() =>
        await (await GetConnectionAsync()).DeleteAllAsync<BehavioralProfile>();

    public async Task<int> InsertAllAsync<T>(IEnumerable<T> items) where T : new() =>
        await (await GetConnectionAsync()).InsertAllAsync(items);

    // ── Geocode cache (offline interceptor) ──────────────────────────────────

    public async Task<GeocodeCache?> GetGeocodeCacheAsync(string queryKey) =>
        await (await GetConnectionAsync())
            .Table<GeocodeCache>()
            .Where(c => c.QueryKey == queryKey)
            .FirstOrDefaultAsync();

    public async Task UpsertGeocodeCacheAsync(GeocodeCache entry)
    {
        var db = await GetConnectionAsync();
        if (entry.Id == 0)
            await db.InsertAsync(entry);
        else
            await db.UpdateAsync(entry);
        await EvictGeocodeCacheOverflowAsync(db);
    }

    // The offline geocode cache is a convenience, not a record to keep forever — cap it at 100 so
    // the encrypted db doesn't balloon over months of commuting. Stalest-used rows go first, so what
    // survives is naturally the handful of places the rider actually travels to.
    private static async Task EvictGeocodeCacheOverflowAsync(SQLiteAsyncConnection db)
    {
        const int MaxEntries = 100;
        var count = await db.Table<GeocodeCache>().CountAsync();
        if (count <= MaxEntries) return;
        var excess = count - MaxEntries;
        var toDelete = await db.Table<GeocodeCache>()
            .OrderBy(c => c.LastUsedUtc)
            .Take(excess)
            .ToListAsync();
        foreach (var e in toDelete)
            await db.DeleteAsync(e);
    }
}
