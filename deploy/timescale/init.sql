-- TimescaleDB schema. Runs automatically on first start of the database container.
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS devices (
    device_id        TEXT PRIMARY KEY,
    last_seen        TIMESTAMPTZ NOT NULL,
    firmware_version TEXT,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS telemetry (
    time          TIMESTAMPTZ      NOT NULL,
    device_id     TEXT             NOT NULL,
    seq           BIGINT           NOT NULL,
    sensor_value  INTEGER          NOT NULL,
    battery_v     DOUBLE PRECISION NOT NULL,
    temperature_c DOUBLE PRECISION NOT NULL,
    rssi          INTEGER          NOT NULL
);

-- Hypertable: automatic time partitioning into 1-day chunks
SELECT create_hypertable('telemetry', 'time', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);
CREATE INDEX IF NOT EXISTS ix_telemetry_device_time ON telemetry (device_id, time DESC);

-- Compress chunks older than 7 days, drop raw data after 90 days
ALTER TABLE telemetry SET (timescaledb.compress, timescaledb.compress_segmentby = 'device_id');
SELECT add_compression_policy('telemetry', INTERVAL '7 days', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry', INTERVAL '90 days', if_not_exists => TRUE);

-- Hourly rollup kept long-term for dashboards and reports
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry_hourly
WITH (timescaledb.continuous) AS
SELECT time_bucket('1 hour', time) AS bucket,
       device_id,
       avg(sensor_value)  AS sensor_avg,
       max(sensor_value)  AS sensor_max,
       min(battery_v)     AS battery_min,
       avg(temperature_c) AS temperature_avg,
       count(*)           AS samples
FROM telemetry
GROUP BY bucket, device_id
WITH NO DATA;

SELECT add_continuous_aggregate_policy('telemetry_hourly',
    start_offset => INTERVAL '3 hours', end_offset => INTERVAL '1 hour',
    schedule_interval => INTERVAL '30 minutes', if_not_exists => TRUE);

CREATE TABLE IF NOT EXISTS alerts (
    id        BIGSERIAL PRIMARY KEY,
    time      TIMESTAMPTZ NOT NULL,
    device_id TEXT        NOT NULL,
    rule      TEXT        NOT NULL,
    severity  TEXT        NOT NULL,
    message   TEXT        NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_alerts_time ON alerts (time DESC);
CREATE INDEX IF NOT EXISTS ix_alerts_device ON alerts (device_id, time DESC);
