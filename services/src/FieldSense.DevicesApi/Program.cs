using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FieldSense.DevicesApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Timescale")!));
builder.Services.AddSingleton<DeviceCommandPublisher>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt["Issuer"],
        ValidAudience = jwt["Audience"],
        IssuerSigningKey = signingKey,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    });

// RBAC: viewers can read; only operators can send commands to devices.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanRead", p => p.RequireRole("viewer", "operator"))
    .AddPolicy("CanCommand", p => p.RequireRole("operator"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

// ---- Auth (demo users from configuration; replace with Entra ID / identity provider in production)
app.MapPost("/auth/token", (LoginRequest req, IConfiguration config) =>
{
    var user = config.GetSection("DemoUsers").GetChildren()
        .FirstOrDefault(u => u["Username"] == req.Username && u["Password"] == req.Password);
    if (user is null) return Results.Unauthorized();

    var token = new JwtSecurityToken(
        issuer: jwt["Issuer"],
        audience: jwt["Audience"],
        claims: [new Claim(ClaimTypes.Name, req.Username), new Claim(ClaimTypes.Role, user["Role"]!)],
        expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresIn = 3600 });
}).AllowAnonymous();

var api = app.MapGroup("/api").RequireAuthorization("CanRead");

// ---- Devices
api.MapGet("/devices", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT device_id, last_seen, firmware_version,
               last_seen > now() - interval '2 minutes' AS online
        FROM devices ORDER BY device_id
        """);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var list = new List<object>();
    while (await r.ReadAsync(ct))
        list.Add(new
        {
            deviceId = r.GetString(0),
            lastSeen = r.GetFieldValue<DateTimeOffset>(1),
            firmware = r.IsDBNull(2) ? null : r.GetString(2),
            online = r.GetBoolean(3)
        });
    return Results.Ok(list);
});

// ---- Telemetry: downsampled with TimescaleDB time_bucket for charts
api.MapGet("/devices/{deviceId}/telemetry", async (
    string deviceId, DateTimeOffset? from, DateTimeOffset? to, int? bucketSeconds,
    NpgsqlDataSource db, CancellationToken ct) =>
{
    var toTime = to ?? DateTimeOffset.UtcNow;
    var fromTime = from ?? toTime.AddHours(-1);
    if (toTime - fromTime > TimeSpan.FromDays(31)) return Results.BadRequest("Range must be 31 days or less");
    var bucket = TimeSpan.FromSeconds(Math.Clamp(bucketSeconds ?? 60, 10, 86_400));

    await using var cmd = db.CreateCommand("""
        SELECT time_bucket($1, time) AS bucket,
               avg(sensor_value)::float8, max(sensor_value),
               avg(battery_v), avg(temperature_c), avg(rssi)::float8
        FROM telemetry
        WHERE device_id = $2 AND time >= $3 AND time < $4
        GROUP BY bucket ORDER BY bucket
        """);
    cmd.Parameters.AddWithValue(bucket);
    cmd.Parameters.AddWithValue(deviceId);
    cmd.Parameters.AddWithValue(fromTime.ToUniversalTime());
    cmd.Parameters.AddWithValue(toTime.ToUniversalTime());

    await using var r = await cmd.ExecuteReaderAsync(ct);
    var points = new List<object>();
    while (await r.ReadAsync(ct))
        points.Add(new
        {
            time = r.GetFieldValue<DateTimeOffset>(0),
            sensorAvg = r.GetDouble(1),
            sensorMax = r.GetInt32(2),
            batteryV = r.GetDouble(3),
            temperatureC = r.GetDouble(4),
            rssi = r.GetDouble(5)
        });
    return Results.Ok(points);
});

// ---- Alerts
api.MapGet("/alerts", async (string? deviceId, int? limit, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT time, device_id, rule, severity, message FROM alerts
        WHERE ($1::text IS NULL OR device_id = $1)
        ORDER BY time DESC LIMIT $2
        """);
    cmd.Parameters.AddWithValue((object?)deviceId ?? DBNull.Value);
    cmd.Parameters.AddWithValue(Math.Clamp(limit ?? 100, 1, 1000));
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var list = new List<object>();
    while (await r.ReadAsync(ct))
        list.Add(new
        {
            time = r.GetFieldValue<DateTimeOffset>(0),
            deviceId = r.GetString(1),
            rule = r.GetString(2),
            severity = r.GetString(3),
            message = r.GetString(4)
        });
    return Results.Ok(list);
});

// ---- Commands (operators only)
api.MapPost("/devices/{deviceId}/commands", async (
    string deviceId, DeviceCommand command, DeviceCommandPublisher publisher, CancellationToken ct) =>
{
    if (command.Cmd is not ("blink" or "set_interval")) return Results.BadRequest("Unknown command");
    if (command.Cmd == "set_interval" && command.Value is null or < 1000 or > 3_600_000)
        return Results.BadRequest("Interval must be between 1000 and 3600000 ms");

    await publisher.SendAsync(deviceId, new { cmd = command.Cmd, value = command.Value }, ct);
    return Results.Accepted();
}).RequireAuthorization("CanCommand");

app.Run();

public sealed record LoginRequest(string Username, string Password);
public sealed record DeviceCommand(string Cmd, long? Value);
