// Copy this file to config.h and fill in your values.
// config.h is git-ignored so secrets never reach the repository.
#pragma once

#define WIFI_SSID        "your-wifi"
#define WIFI_PASSWORD    "your-password"

#define DEVICE_ID        "trap-001"

// Local broker (docker compose) uses TLS on 8883 with username/password.
// The MQTT username MUST equal DEVICE_ID: the broker ACL only lets a device
// publish to fieldsense/devices/<its-own-username>/...
#define MQTT_HOST        "192.168.1.10"
#define MQTT_PORT        8883
#define MQTT_USER        DEVICE_ID
#define MQTT_PASSWORD    "device-password"

// CA certificate that signed the broker certificate (deploy/mosquitto/certs/ca.crt)
static const char *MQTT_CA_CERT = R"EOF(
-----BEGIN CERTIFICATE-----
PASTE ca.crt CONTENT HERE
-----END CERTIFICATE-----
)EOF";

#define TELEMETRY_INTERVAL_MS 10000UL
#define SENSOR_PIN            34   // analog input (e.g. trap weight / IR sensor)
#define BATTERY_PIN           35   // battery voltage divider
#define LED_PIN               2
