using System.Text;
using FieldSense.Domain;
using Xunit;

namespace FieldSense.Domain.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private readonly AlertEvaluator _sut = new(new AlertThresholds());

    private static TelemetryMessage Healthy() => new()
    {
        DeviceId = "trap-001", SensorValue = 400, BatteryVolts = 4.0, TemperatureC = 30, Rssi = -60
    };

    [Fact]
    public void Healthy_reading_produces_no_alerts() =>
        Assert.Empty(_sut.Evaluate(Healthy(), Now));

    [Fact]
    public void High_sensor_value_raises_trap_triggered() =>
        Assert.Contains(_sut.Evaluate(Healthy() with { SensorValue = 3000 }, Now), a => a.Rule == "trap_triggered");

    [Theory]
    [InlineData(3.3, "battery_low", AlertSeverity.Warning)]
    [InlineData(3.1, "battery_critical", AlertSeverity.Critical)]
    public void Battery_levels_map_to_one_severity(double volts, string rule, AlertSeverity severity)
    {
        var alerts = _sut.Evaluate(Healthy() with { BatteryVolts = volts }, Now);
        var alert = Assert.Single(alerts);
        Assert.Equal(rule, alert.Rule);
        Assert.Equal(severity, alert.Severity);
    }
}

public class TelemetryValidatorTests
{
    [Fact]
    public void Device_id_must_match_topic()
    {
        var m = new TelemetryMessage { DeviceId = "trap-001", BatteryVolts = 4, Rssi = -50 };
        Assert.Contains("deviceId does not match the MQTT topic", TelemetryValidator.Validate(m, "trap-002"));
    }

    [Theory]
    [InlineData("fieldsense/devices/trap-7/telemetry", "trap-7")]
    [InlineData("fieldsense/devices/trap-7/status", null)]
    [InlineData("other/devices/trap-7/telemetry", null)]
    public void Extracts_device_id_from_topic(string topic, string? expected) =>
        Assert.Equal(expected, TelemetryValidator.DeviceIdFromTopic(topic));

    [Fact]
    public void Out_of_range_values_are_rejected()
    {
        var m = new TelemetryMessage { DeviceId = "trap-001", SensorValue = 9999, BatteryVolts = 4, Rssi = 10 };
        var errors = TelemetryValidator.Validate(m);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Malformed_json_returns_null() =>
        Assert.Null(TelemetryMessage.TryParse(Encoding.UTF8.GetBytes("{not json")));

    [Fact]
    public void Parses_device_payload()
    {
        var json = """{"deviceId":"trap-001","seq":5,"sensorValue":512,"batteryV":3.95,"temperatureC":28.5,"rssi":-61,"fw":"1.0.0"}""";
        var m = TelemetryMessage.TryParse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(m);
        Assert.Equal(512, m!.SensorValue);
        Assert.Empty(TelemetryValidator.Validate(m, "trap-001"));
    }
}
