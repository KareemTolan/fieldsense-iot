#!/usr/bin/env bash
# Creates a private CA and a broker certificate for Mosquitto.
# Usage: ./scripts/generate-certs.sh [extra-ip]    e.g. your PC's LAN IP for the ESP32
set -euo pipefail
cd "$(dirname "$0")/../deploy/mosquitto"
mkdir -p certs && cd certs

EXTRA_IP="${1:-127.0.0.1}"

openssl req -x509 -new -nodes -newkey rsa:2048 -days 3650 \
  -keyout ca.key -out ca.crt -subj "/CN=FieldSense Dev CA"

openssl req -new -nodes -newkey rsa:2048 \
  -keyout server.key -out server.csr -subj "/CN=mosquitto"

cat > server.ext <<EOF
subjectAltName = DNS:mosquitto, DNS:localhost, IP:127.0.0.1, IP:${EXTRA_IP}
extendedKeyUsage = serverAuth
EOF

openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out server.crt -days 825 -sha256 -extfile server.ext

rm -f server.csr server.ext
chmod 644 ca.crt server.crt server.key
echo "Certificates written to deploy/mosquitto/certs (copy ca.crt into the ESP32 config.h)"
