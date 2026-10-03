// FieldSense ESP32 node
// - Connects to Wi-Fi and to the MQTT broker over TLS (port 8883)
// - Publishes JSON telemetry to   fieldsense/devices/<id>/telemetry   (QoS 0)
// - Publishes online/offline state with a retained Last Will message
// - Receives commands on          fieldsense/devices/<id>/commands
//   supported: {"cmd":"set_interval","value":5000} and {"cmd":"blink"}

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include "config.h"

static WiFiClientSecure tlsClient;
static PubSubClient mqtt(tlsClient);

static String topicTelemetry;
static String topicCommands;
static String topicStatus;

static unsigned long telemetryInterval = TELEMETRY_INTERVAL_MS;
static unsigned long lastPublish = 0;
static uint32_t sequence = 0;

static void connectWifi() {
  if (WiFi.status() == WL_CONNECTED) return;
  Serial.printf("[wifi] connecting to %s", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print('.');
  }
  Serial.printf("\n[wifi] connected, ip=%s\n", WiFi.localIP().toString().c_str());
}

static void blink(int times) {
  for (int i = 0; i < times; i++) {
    digitalWrite(LED_PIN, HIGH); delay(150);
    digitalWrite(LED_PIN, LOW);  delay(150);
  }
}

static void onMessage(char *topic, byte *payload, unsigned int length) {
  JsonDocument doc;
  if (deserializeJson(doc, payload, length)) {
    Serial.println("[mqtt] invalid command payload");
    return;
  }
  const char *cmd = doc["cmd"] | "";
  if (strcmp(cmd, "set_interval") == 0) {
    unsigned long value = doc["value"] | 0UL;
    if (value >= 1000 && value <= 3600000) {
      telemetryInterval = value;
      Serial.printf("[cmd] interval set to %lu ms\n", telemetryInterval);
    }
  } else if (strcmp(cmd, "blink") == 0) {
    blink(3);
  } else {
    Serial.printf("[cmd] unknown command: %s\n", cmd);
  }
}

static void connectMqtt() {
  while (!mqtt.connected()) {
    Serial.printf("[mqtt] connecting to %s:%d\n", MQTT_HOST, MQTT_PORT);
    // Last Will: broker publishes "offline" (retained) if the device drops.
    bool ok = mqtt.connect(DEVICE_ID, MQTT_USER, MQTT_PASSWORD,
                           topicStatus.c_str(), 1, true, "offline");
    if (ok) {
      mqtt.publish(topicStatus.c_str(), "online", true);
      mqtt.subscribe(topicCommands.c_str(), 1);
      Serial.println("[mqtt] connected");
    } else {
      Serial.printf("[mqtt] failed rc=%d, retrying in 5s\n", mqtt.state());
      delay(5000);
    }
  }
}

static float readBatteryVolts() {
  // 2:1 voltage divider, 12-bit ADC, 3.3V reference
  return analogRead(BATTERY_PIN) / 4095.0f * 3.3f * 2.0f;
}

static void publishTelemetry() {
  int raw = analogRead(SENSOR_PIN);

  JsonDocument doc;
  doc["deviceId"]    = DEVICE_ID;
  doc["seq"]         = ++sequence;
  doc["uptimeMs"]    = millis();
  doc["sensorValue"] = raw;
  doc["batteryV"]    = readBatteryVolts();
  doc["rssi"]        = WiFi.RSSI();
  doc["temperatureC"] = temperatureRead();   // ESP32 internal sensor
  doc["fw"]          = FW_VERSION;

  char buffer[512];
  size_t n = serializeJson(doc, buffer, sizeof(buffer));
  if (mqtt.publish(topicTelemetry.c_str(), (const uint8_t *)buffer, n, false)) {
    Serial.printf("[telemetry] %s\n", buffer);
  } else {
    Serial.println("[telemetry] publish failed");
  }
}

void setup() {
  Serial.begin(115200);
  pinMode(LED_PIN, OUTPUT);
  analogReadResolution(12);

  topicTelemetry = String("fieldsense/devices/") + DEVICE_ID + "/telemetry";
  topicCommands  = String("fieldsense/devices/") + DEVICE_ID + "/commands";
  topicStatus    = String("fieldsense/devices/") + DEVICE_ID + "/status";

  tlsClient.setCACert(MQTT_CA_CERT);   // verify the broker certificate
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setCallback(onMessage);
  mqtt.setKeepAlive(30);
}

void loop() {
  connectWifi();
  connectMqtt();
  mqtt.loop();

  unsigned long now = millis();
  if (now - lastPublish >= telemetryInterval) {
    lastPublish = now;
    publishTelemetry();
  }
}
