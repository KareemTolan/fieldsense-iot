using System.Text.RegularExpressions;

namespace FieldSense.Domain;

/// <summary>Rejects malformed or out-of-range telemetry before it reaches storage.</summary>
public static partial class TelemetryValidator
{
    [GeneratedRegex("^[a-zA-Z0-9-]{3,64}$")]
    private static partial Regex DeviceIdPattern();

    public static IReadOnlyList<string> Validate(TelemetryMessage m, string? topicDeviceId = null)
    {
        var errors = new List<string>();
        if (!DeviceIdPattern().IsMatch(m.DeviceId)) errors.Add("deviceId is invalid");
        if (topicDeviceId is not null && topicDeviceId != m.DeviceId)
            errors.Add("deviceId does not match the MQTT topic"); // stops one device spoofing another
        if (m.SensorValue is < 0 or > 4095) errors.Add("sensorValue out of range 0-4095");
        if (m.BatteryVolts is < 0 or > 6) errors.Add("batteryV out of range");
        if (m.TemperatureC is < -40 or > 125) errors.Add("temperatureC out of range");
        if (m.Rssi is < -127 or > 0) errors.Add("rssi out of range");
        return errors;
    }

    /// <summary>Extracts the device id from fieldsense/devices/{id}/telemetry.</summary>
    public static string? DeviceIdFromTopic(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 4 && parts[0] == "fieldsense" && parts[1] == "devices" && parts[3] == "telemetry"
            ? parts[2]
            : null;
    }
}
