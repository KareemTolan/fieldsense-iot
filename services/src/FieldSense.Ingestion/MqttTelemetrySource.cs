using System.Security.Cryptography.X509Certificates;
using FieldSense.Domain;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;

namespace FieldSense.Ingestion;

/// <summary>Subscribes to device telemetry on the MQTT broker (TLS + username/password, MQTT 5).</summary>
public sealed class MqttTelemetrySource(IOptions<IngestionOptions> options, ILogger<MqttTelemetrySource> log)
    : ITelemetrySource
{
    private readonly MqttSourceOptions _o = options.Value.Mqtt;

    public async Task RunAsync(Func<IncomingTelemetry, ValueTask> onTelemetry, CancellationToken ct)
    {
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var topicDeviceId = TelemetryValidator.DeviceIdFromTopic(e.ApplicationMessage.Topic);
            var message = TelemetryMessage.TryParse(e.ApplicationMessage.PayloadSegment);
            if (topicDeviceId is null || message is null)
            {
                log.LogWarning("Dropped unparseable message on {Topic}", e.ApplicationMessage.Topic);
                return;
            }
            var errors = TelemetryValidator.Validate(message, topicDeviceId);
            if (errors.Count > 0)
            {
                log.LogWarning("Rejected telemetry from {Device}: {Errors}", topicDeviceId, string.Join("; ", errors));
                return;
            }
            var now = DateTimeOffset.UtcNow;
            await onTelemetry(new IncomingTelemetry(message, message.ResolveTime(now)));
        };

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_o.Host, _o.Port)
            .WithClientId($"ingestion-{Environment.MachineName}")
            .WithCredentials(_o.Username, _o.Password)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession();

        if (_o.UseTls)
        {
            var ca = string.IsNullOrEmpty(_o.CaCertPath) ? null : new X509Certificate2(_o.CaCertPath);
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls();
                if (ca is not null)
                    tls.WithCertificateValidationHandler(args => ValidateAgainstCa(args.Certificate, ca));
            });
        }

        var mqttOptions = builder.Build();
        var subscribe = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_o.Topic).WithAtLeastOnceQoS())
            .Build();

        // Reconnect loop: survives broker restarts and network drops.
        while (!ct.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                try
                {
                    await client.ConnectAsync(mqttOptions, ct);
                    await client.SubscribeAsync(subscribe, ct);
                    log.LogInformation("Connected to MQTT {Host}:{Port}, subscribed to {Topic}", _o.Host, _o.Port, _o.Topic);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("MQTT connect failed: {Error}. Retrying in 5s", ex.Message);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { });
        }

        if (client.IsConnected) await client.DisconnectAsync();
    }

    /// <summary>Accepts the broker certificate only if it chains to our own CA.</summary>
    private static bool ValidateAgainstCa(X509Certificate? serverCert, X509Certificate2 ca)
    {
        if (serverCert is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(serverCert));
    }
}
