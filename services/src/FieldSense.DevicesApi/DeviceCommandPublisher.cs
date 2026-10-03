using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace FieldSense.DevicesApi;

/// <summary>Sends cloud-to-device commands to fieldsense/devices/{id}/commands.</summary>
public sealed class DeviceCommandPublisher(IConfiguration config, ILogger<DeviceCommandPublisher> log) : IAsyncDisposable
{
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SendAsync(string deviceId, object command, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"fieldsense/devices/{deviceId}/commands")
            .WithPayload(JsonSerializer.SerializeToUtf8Bytes(command))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(message, ct);
        log.LogInformation("Command sent to {Device}", deviceId);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_client.IsConnected) return;
            var s = config.GetSection("Mqtt");
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(s["Host"], s.GetValue("Port", 8883))
                .WithClientId($"devices-api-{Environment.MachineName}")
                .WithCredentials(s["Username"], s["Password"]);
            if (s.GetValue("UseTls", true))
            {
                // Trust only the broker certificate signed by our own CA.
                var caPath = s["CaCertPath"];
                var ca = string.IsNullOrEmpty(caPath) ? null : new X509Certificate2(caPath);
                builder.WithTlsOptions(t =>
                {
                    t.UseTls();
                    if (ca is not null)
                        t.WithCertificateValidationHandler(args =>
                        {
                            if (args.Certificate is null) return false;
                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(ca);
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            return chain.Build(new X509Certificate2(args.Certificate));
                        });
                });
            }
            await _client.ConnectAsync(builder.Build(), ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected) await _client.DisconnectAsync();
        _client.Dispose();
    }
}
