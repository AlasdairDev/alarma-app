using SQLite;

namespace AlarmaApp.Models;

public class EmergencyContact
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }
}
