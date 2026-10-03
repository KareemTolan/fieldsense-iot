using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldSense.Domain;

/// <summary>Telemetry message as published by devices (ESP32 firmware or simulator).</summary>
public sealed record TelemetryMessage
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("seq")] public long Sequence { get; init; }
    [JsonPropertyName("timestamp")] public long? TimestampMs { get; init; }
    [JsonPropertyName("sensorValue")] public int SensorValue { get; init; }
    [JsonPropertyName("batteryV")] public double BatteryVolts { get; init; }
    [JsonPropertyName("temperatureC")] public double TemperatureC { get; init; }
    [JsonPropertyName("rssi")] public int Rssi { get; init; }
    [JsonPropertyName("fw")] public string? FirmwareVersion { get; init; }

    /// <summary>Device time if present, otherwise the time the platform received it.</summary>
    public DateTimeOffset ResolveTime(DateTimeOffset receivedAt) =>
        TimestampMs is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs.Value) : receivedAt;

    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static TelemetryMessage? TryParse(ReadOnlySpan<byte> utf8Json)
    {
        try { return JsonSerializer.Deserialize<TelemetryMessage>(utf8Json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
