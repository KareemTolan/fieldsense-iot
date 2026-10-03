using FieldSense.Alerts;
using FieldSense.Domain;
using Npgsql;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(
    builder.Configuration.GetSection("Alerts:Thresholds").Get<AlertThresholds>() ?? new AlertThresholds());
builder.Services.AddSingleton<AlertEvaluator>();

builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Timescale")!));

builder.Services.AddSingleton<IConnection>(_ =>
    new ConnectionFactory
    {
        Uri = new Uri(builder.Configuration.GetConnectionString("RabbitMq")!),
        AutomaticRecoveryEnabled = true,
        DispatchConsumersAsync = true   // required for AsyncEventingBasicConsumer
    }.CreateConnection("fieldsense-alerts"));

builder.Services.AddHostedService<AlertsWorker>();

builder.Build().Run();
