namespace FieldSense.Domain;

public enum AlertSeverity { Info, Warning, Critical }

public sealed record Alert(string DeviceId, string Rule, AlertSeverity Severity, string Message, DateTimeOffset Time);

public sealed record AlertThresholds
{
    public int TrapTriggeredSensorValue { get; init; } = 2000;
    public double LowBatteryVolts { get; init; } = 3.4;
    public double CriticalBatteryVolts { get; init; } = 3.2;
    public double HighTemperatureC { get; init; } = 60;
    public int WeakSignalRssi { get; init; } = -85;
}

/// <summary>Stateless rule engine: turns one telemetry message into zero or more alerts.</summary>
public sealed class AlertEvaluator(AlertThresholds thresholds)
{
    public IReadOnlyList<Alert> Evaluate(TelemetryMessage m, DateTimeOffset time)
    {
        var alerts = new List<Alert>();

        if (m.SensorValue >= thresholds.TrapTriggeredSensorValue)
            alerts.Add(new(m.DeviceId, "trap_triggered", AlertSeverity.Warning,
                $"Sensor value {m.SensorValue} reached trigger level", time));

        if (m.BatteryVolts <= thresholds.CriticalBatteryVolts)
            alerts.Add(new(m.DeviceId, "battery_critical", AlertSeverity.Critical,
                $"Battery at {m.BatteryVolts:F2} V", time));
        else if (m.BatteryVolts <= thresholds.LowBatteryVolts)
            alerts.Add(new(m.DeviceId, "battery_low", AlertSeverity.Warning,
                $"Battery at {m.BatteryVolts:F2} V", time));

        if (m.TemperatureC >= thresholds.HighTemperatureC)
            alerts.Add(new(m.DeviceId, "high_temperature", AlertSeverity.Critical,
                $"Temperature {m.TemperatureC:F1} °C", time));

        if (m.Rssi <= thresholds.WeakSignalRssi)
            alerts.Add(new(m.DeviceId, "weak_signal", AlertSeverity.Info,
                $"RSSI {m.Rssi} dBm", time));

        return alerts;
    }
}
