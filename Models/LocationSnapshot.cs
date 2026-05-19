namespace AlarmaApp.Models;

public record LocationSnapshot(double Latitude, double Longitude, float AccuracyMeters, DateTimeOffset Timestamp);
