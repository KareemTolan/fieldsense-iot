using System.Collections.Concurrent;
using FieldSense.Domain;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FieldSense.Alerts;

/// <summary>
/// Consumes telemetry from RabbitMQ, evaluates alert rules and stores alerts.
/// Uses manual acks so a message is only removed after the alert is saved,
/// and suppresses repeats of the same rule per device for a cooldown period.
/// </summary>
public sealed class AlertsWorker(
    IConnection rabbit,
    NpgsqlDataSource db,
    AlertEvaluator evaluator,
    IConfiguration config,
    ILogger<AlertsWorker> log) : BackgroundService
{
    private const string Exchange = "fieldsense.telemetry";
    private const string Queue = "fieldsense.alerts";

    private readonly TimeSpan _cooldown = TimeSpan.FromMinutes(config.GetValue("Alerts:CooldownMinutes", 10));
    private readonly ConcurrentDictionary<(string Device, string Rule), DateTimeOffset> _lastRaised = new();

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var model = rabbit.CreateModel();
        model.ExchangeDeclare(Exchange, ExchangeType.Fanout, durable: true);
        model.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false);
        model.QueueBind(Queue, Exchange, routingKey: "");
        model.BasicQos(0, prefetchCount: 50, global: false);

        var consumer = new AsyncEventingBasicConsumer(model);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                var message = TelemetryMessage.TryParse(ea.Body.Span);
                if (message is not null)
                {
                    var alerts = evaluator.Evaluate(message, message.ResolveTime(DateTimeOffset.UtcNow))
                        .Where(ShouldRaise)
                        .ToList();
                    if (alerts.Count > 0) await SaveAsync(alerts, ct);
                }
                model.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to process telemetry; requeueing");
                model.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        model.BasicConsume(Queue, autoAck: false, consumer);
        log.LogInformation("Alerts worker consuming {Queue}", Queue);

        ct.Register(() => model.Dispose());
        return Task.Delay(Timeout.Infinite, ct);
    }

    private bool ShouldRaise(Alert alert)
    {
        var key = (alert.DeviceId, alert.Rule);
        var now = alert.Time;
        if (_lastRaised.TryGetValue(key, out var last) && now - last < _cooldown) return false;
        _lastRaised[key] = now;
        return true;
    }

    private async Task SaveAsync(IReadOnlyList<Alert> alerts, CancellationToken ct)
    {
        await using var batch = db.CreateBatch();
        foreach (var a in alerts)
        {
            var cmd = new NpgsqlBatchCommand(
                "INSERT INTO alerts (time, device_id, rule, severity, message) VALUES ($1, $2, $3, $4, $5)");
            cmd.Parameters.AddWithValue(a.Time.ToUniversalTime());
            cmd.Parameters.AddWithValue(a.DeviceId);
            cmd.Parameters.AddWithValue(a.Rule);
            cmd.Parameters.AddWithValue(a.Severity.ToString());
            cmd.Parameters.AddWithValue(a.Message);
            batch.BatchCommands.Add(cmd);
            log.LogInformation("ALERT {Severity} {Device} {Rule}: {Message}", a.Severity, a.DeviceId, a.Rule, a.Message);
        }
        await batch.ExecuteNonQueryAsync(ct);
    }
}
