using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;

namespace FieldSense.Ingestion;

/// <summary>
/// Pipeline: source -> bounded channel -> batch writer.
/// Each batch is written to TimescaleDB with binary COPY (fast bulk insert)
/// and then fanned out on RabbitMQ so other services (Alerts) can react.
/// The bounded channel applies back-pressure if the database slows down.
/// </summary>
public sealed class IngestionWorker(
    ITelemetrySource source,
    NpgsqlDataSource db,
    IConnection rabbit,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> log) : BackgroundService
{
    public const string Exchange = "fieldsense.telemetry";

    private readonly IngestionOptions _o = options.Value;
    private readonly Channel<IncomingTelemetry> _channel =
        Channel.CreateBounded<IncomingTelemetry>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var writer = Task.Run(() => WriteLoopAsync(ct), ct);
        await source.RunAsync(t => _channel.Writer.WriteAsync(t, ct), ct);
        _channel.Writer.TryComplete();
        await writer;
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        using var model = rabbit.CreateModel();
        model.ExchangeDeclare(Exchange, ExchangeType.Fanout, durable: true);
        var props = model.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2; // persistent

        var batch = new List<IncomingTelemetry>(_o.BatchSize);
        var reader = _channel.Reader;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for the first item, then collect until batch is full or flush interval passes.
                if (!await reader.WaitToReadAsync(ct)) break;
                var deadline = DateTime.UtcNow.AddMilliseconds(_o.FlushIntervalMs);
                while (batch.Count < _o.BatchSize && DateTime.UtcNow < deadline)
                {
                    if (reader.TryRead(out var item)) batch.Add(item);
                    else await Task.Delay(20, ct);
                }

                await WriteBatchAsync(batch, ct);
                foreach (var t in batch)
                    model.BasicPublish(Exchange, routingKey: "", props, JsonSerializer.SerializeToUtf8Bytes(t.Message));

                log.LogDebug("Stored and published {Count} telemetry rows", batch.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Batch of {Count} failed; will retry", batch.Count);
                await Task.Delay(2000, ct).ContinueWith(_ => { });
                continue; // keep the batch and retry
            }
            batch.Clear();
        }
    }

    private async Task WriteBatchAsync(List<IncomingTelemetry> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var importer = await conn.BeginBinaryImportAsync(
            "COPY telemetry (time, device_id, seq, sensor_value, battery_v, temperature_c, rssi) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var (m, time) in batch)
            {
                await importer.StartRowAsync(ct);
                await importer.WriteAsync(time.ToUniversalTime(), NpgsqlDbType.TimestampTz, ct);
                await importer.WriteAsync(m.DeviceId, NpgsqlDbType.Text, ct);
                await importer.WriteAsync(m.Sequence, NpgsqlDbType.Bigint, ct);
                await importer.WriteAsync(m.SensorValue, NpgsqlDbType.Integer, ct);
                await importer.WriteAsync(m.BatteryVolts, NpgsqlDbType.Double, ct);
                await importer.WriteAsync(m.TemperatureC, NpgsqlDbType.Double, ct);
                await importer.WriteAsync(m.Rssi, NpgsqlDbType.Integer, ct);
            }
            await importer.CompleteAsync(ct);
        }

        // Auto-register devices and keep last_seen / firmware current.
        foreach (var group in batch.GroupBy(b => b.Message.DeviceId))
        {
            var latest = group.MaxBy(g => g.Time)!;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO devices (device_id, last_seen, firmware_version)
                VALUES ($1, $2, $3)
                ON CONFLICT (device_id) DO UPDATE
                SET last_seen = GREATEST(devices.last_seen, EXCLUDED.last_seen),
                    firmware_version = COALESCE(EXCLUDED.firmware_version, devices.firmware_version)
                """, conn, tx);
            cmd.Parameters.AddWithValue(group.Key);
            cmd.Parameters.AddWithValue(latest.Time.ToUniversalTime());
            cmd.Parameters.AddWithValue((object?)latest.Message.FirmwareVersion ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
