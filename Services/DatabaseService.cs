using AlarmaApp.Models;
using Microsoft.Maui.Storage;
using SQLite;

namespace AlarmaApp.Services;

public class DatabaseService
{
    private readonly Lazy<SQLiteAsyncConnection> _connection;

    public DatabaseService()
    {
        SQLitePCL.Batteries_V2.Init();
        var databasePath = Path.Combine(FileSystem.AppDataDirectory, "alarma.db3");
        _connection = new Lazy<SQLiteAsyncConnection>(() => new SQLiteAsyncConnection(databasePath));
    }

    public async Task InitializeAsync()
    {
        var db = _connection.Value;
        await db.CreateTableAsync<TripHistory>();
        await db.CreateTableAsync<SavedRoute>();
        await db.CreateTableAsync<EmergencyContact>();
        await db.CreateTableAsync<BehavioralProfile>();
    }

    public Task<List<TripHistory>> GetTripHistoryAsync() => _connection.Value.Table<TripHistory>().ToListAsync();

    public Task<int> SaveTripHistoryAsync(TripHistory item) =>
        item.Id == 0 ? _connection.Value.InsertAsync(item) : _connection.Value.UpdateAsync(item);

    public Task<int> ClearTripHistoryAsync() => _connection.Value.DeleteAllAsync<TripHistory>();

    public Task<List<SavedRoute>> GetSavedRoutesAsync() => _connection.Value.Table<SavedRoute>().ToListAsync();

    public Task<int> SaveRouteAsync(SavedRoute route) =>
        route.Id == 0 ? _connection.Value.InsertAsync(route) : _connection.Value.UpdateAsync(route);

    public Task<int> DeleteRouteAsync(SavedRoute route) => _connection.Value.DeleteAsync(route);

    public Task<int> ClearSavedRoutesAsync() => _connection.Value.DeleteAllAsync<SavedRoute>();

    public Task<List<EmergencyContact>> GetEmergencyContactsAsync() => _connection.Value.Table<EmergencyContact>().ToListAsync();

    public Task<int> SaveEmergencyContactAsync(EmergencyContact contact) =>
        contact.Id == 0 ? _connection.Value.InsertAsync(contact) : _connection.Value.UpdateAsync(contact);

    public Task<int> DeleteEmergencyContactAsync(EmergencyContact contact) => _connection.Value.DeleteAsync(contact);

    public Task<int> ClearEmergencyContactsAsync() => _connection.Value.DeleteAllAsync<EmergencyContact>();

    public Task<List<BehavioralProfile>> GetBehavioralProfilesAsync() => _connection.Value.Table<BehavioralProfile>().ToListAsync();

    public Task<int> SaveBehavioralProfileAsync(BehavioralProfile profile) =>
        profile.Id == 0 ? _connection.Value.InsertAsync(profile) : _connection.Value.UpdateAsync(profile);

    public Task<int> DeleteTripHistoryAsync(TripHistory history) => _connection.Value.DeleteAsync(history);

    public Task<int> ClearBehavioralProfilesAsync() => _connection.Value.DeleteAllAsync<BehavioralProfile>();

    public Task<int> InsertAllAsync<T>(IEnumerable<T> items) where T : new() =>
        _connection.Value.InsertAllAsync(items);
}
