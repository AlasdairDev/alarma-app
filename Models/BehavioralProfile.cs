using SQLite;

namespace AlarmaApp.Models;

public class BehavioralProfile
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int AlarmLeadMinutes { get; set; }

    public string AlarmSound { get; set; } = "default";

    public string? Notes { get; set; }
}
