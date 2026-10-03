using FieldSense.Domain;

namespace FieldSense.Ingestion;

/// <summary>A validated telemetry message plus where/when it arrived.</summary>
public sealed record IncomingTelemetry(TelemetryMessage Message, DateTimeOffset Time);

/// <summary>Where telemetry comes from: local MQTT broker or Azure IoT Hub.</summary>
public interface ITelemetrySource
{
    Task RunAsync(Func<IncomingTelemetry, ValueTask> onTelemetry, CancellationToken ct);
}

public sealed class IngestionOptions
{
    public string Source { get; set; } = "Mqtt";          // "Mqtt" or "AzureIoTHub"
    public int BatchSize { get; set; } = 500;
    public int FlushIntervalMs { get; set; } = 1000;
    public MqttSourceOptions Mqtt { get; set; } = new();
    public AzureIoTHubOptions AzureIoTHub { get; set; } = new();
}

public sealed class MqttSourceOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8883;
    public bool UseTls { get; set; } = true;
    public string? CaCertPath { get; set; }
    public string Username { get; set; } = "ingestion";
    public string Password { get; set; } = "";
    // Shared subscription: several ingestion replicas split the load.
    public string Topic { get; set; } = "$share/ingestion/fieldsense/devices/+/telemetry";
}

public sealed class AzureIoTHubOptions
{
    // IoT Hub > Built-in endpoints > Event Hub-compatible endpoint
    public string EventHubConnectionString { get; set; } = "";
    public string ConsumerGroup { get; set; } = "$Default";
}
