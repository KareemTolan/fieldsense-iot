using Azure.Messaging.EventHubs.Consumer;
using FieldSense.Domain;
using Microsoft.Extensions.Options;

namespace FieldSense.Ingestion;

/// <summary>
/// Reads device-to-cloud telemetry from Azure IoT Hub through its built-in
/// Event Hub-compatible endpoint. The device id is taken from the IoT Hub
/// system property, so a device cannot claim another device's identity.
/// </summary>
public sealed class AzureIoTHubTelemetrySource(IOptions<IngestionOptions> options, ILogger<AzureIoTHubTelemetrySource> log)
    : ITelemetrySource
{
    private const string DeviceIdProperty = "iothub-connection-device-id";
    private readonly AzureIoTHubOptions _o = options.Value.AzureIoTHub;

    public async Task RunAsync(Func<IncomingTelemetry, ValueTask> onTelemetry, CancellationToken ct)
    {
        await using var consumer = new EventHubConsumerClient(_o.ConsumerGroup, _o.EventHubConnectionString);
        log.LogInformation("Reading telemetry from Azure IoT Hub (consumer group {Group})", _o.ConsumerGroup);

        var readOptions = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(5) };
        await foreach (var partitionEvent in consumer.ReadEventsAsync(startReadingAtEarliestEvent: false, readOptions, ct))
        {
            var data = partitionEvent.Data;
            if (data is null) continue; // wait-time heartbeat

            var hubDeviceId = data.SystemProperties.TryGetValue(DeviceIdProperty, out var id) ? id?.ToString() : null;
            var message = TelemetryMessage.TryParse(data.EventBody.ToArray());
            if (hubDeviceId is null || message is null) continue;

            var errors = TelemetryValidator.Validate(message, hubDeviceId);
            if (errors.Count > 0)
            {
                log.LogWarning("Rejected telemetry from {Device}: {Errors}", hubDeviceId, string.Join("; ", errors));
                continue;
            }
            await onTelemetry(new IncomingTelemetry(message, message.ResolveTime(data.EnqueuedTime)));
        }
    }
}
