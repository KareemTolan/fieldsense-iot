using FieldSense.Ingestion;
using Npgsql;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));

builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Timescale")!));

builder.Services.AddSingleton<IConnection>(_ =>
    new ConnectionFactory
    {
        Uri = new Uri(builder.Configuration.GetConnectionString("RabbitMq")!),
        AutomaticRecoveryEnabled = true
    }.CreateConnection("fieldsense-ingestion"));

// Pick the telemetry source from configuration: local MQTT broker or Azure IoT Hub.
var source = builder.Configuration["Ingestion:Source"] ?? "Mqtt";
if (source.Equals("AzureIoTHub", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ITelemetrySource, AzureIoTHubTelemetrySource>();
else
    builder.Services.AddSingleton<ITelemetrySource, MqttTelemetrySource>();

builder.Services.AddHostedService<IngestionWorker>();

builder.Build().Run();
