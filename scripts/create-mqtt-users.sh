#!/usr/bin/env bash
# Creates the Mosquitto password file (hashed) using the official image's mosquitto_passwd.
# Add one line per physical device: username must equal the device id (see acl).
set -euo pipefail
cd "$(dirname "$0")/../deploy/mosquitto"

: > passwd
add() {
  docker run --rm -v "$PWD:/m" eclipse-mosquitto:2.0 mosquitto_passwd -b /m/passwd "$1" "$2"
}
add ingestion  "${MQTT_INGESTION_PASSWORD:-ingestion-password}"
add api        "${MQTT_API_PASSWORD:-api-password}"
add simulator  "${MQTT_SIMULATOR_PASSWORD:-simulator-password}"
add trap-001   "${MQTT_DEVICE_PASSWORD:-device-password}"
echo "deploy/mosquitto/passwd created"
