# FieldSense IoT Platform

End-to-end IoT platform for remote field sensors (smart traps): ESP32 firmware,
MQTT over TLS, Azure IoT Hub, .NET 8 microservices, RabbitMQ and TimescaleDB.

| Part | Tech | Folder |
|---|---|---|
| Device firmware | C++ (Arduino/PlatformIO), ESP32, PubSubClient, TLS | `firmware/esp32` |
| Device simulator | Python, paho-mqtt, Azure IoT device SDK | `simulator` |
| Ingestion service | .NET 8 worker, MQTTnet (MQTT 5), Azure Event Hubs SDK, Npgsql binary COPY | `services/src/FieldSense.Ingestion` |
| Alerts service | .NET 8 worker, RabbitMQ consumer, rule engine | `services/src/FieldSense.Alerts` |
| Devices API | ASP.NET Core minimal API, JWT, RBAC, Swagger | `services/src/FieldSense.DevicesApi` |
| Storage | TimescaleDB hypertable, compression, retention, continuous aggregates | `deploy/timescale` |
| Broker | Eclipse Mosquitto, TLS, per-device ACL | `deploy/mosquitto` |
| CI | GitHub Actions: .NET build/test, pytest, firmware build, Docker images | `.github/workflows` |

See **[docs/architecture.md](docs/architecture.md)** for the diagram, data flow and security design.

## Run locally (Docker)

Requires Docker and OpenSSL (Linux, macOS, or Windows with WSL / Git Bash).

```bash
./scripts/generate-certs.sh 192.168.1.10   # your PC's LAN IP, so the ESP32 can connect
./scripts/create-mqtt-users.sh
docker compose -f deploy/docker-compose.yml up --build -d
```

Send simulated telemetry:

```bash
cd simulator
pip install -r requirements.txt
python simulator.py mqtt --devices 20 --interval 2
```

Use the API (Swagger at http://localhost:8080/swagger):

```bash
TOKEN=$(curl -s -X POST localhost:8080/auth/token -H 'Content-Type: application/json' \
  -d '{"username":"operator","password":"operator123"}' | jq -r .accessToken)

curl -H "Authorization: Bearer $TOKEN" localhost:8080/api/devices
curl -H "Authorization: Bearer $TOKEN" "localhost:8080/api/devices/sim-001/telemetry?bucketSeconds=60"
curl -H "Authorization: Bearer $TOKEN" localhost:8080/api/alerts
curl -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"cmd":"blink"}' localhost:8080/api/devices/trap-001/commands
```

RabbitMQ dashboard: http://localhost:15672 (fieldsense / fieldsense).

## Real ESP32 device

```bash
cd firmware/esp32
cp include/config.example.h include/config.h   # set Wi-Fi, broker IP, paste certs/ca.crt
pio run -t upload && pio device monitor
```

Wire a sensor to GPIO 34 (analog) and a 2:1 battery divider to GPIO 35.
Without a sensor the node still sends readings (floating ADC), which is enough to test the pipeline.

## Azure IoT Hub mode

```bash
az login && az extension add --name azure-iot
./scripts/azure-iothub-setup.sh            # free F1 hub + one device, prints connection strings

python simulator/simulator.py azure --connection-string "<device connection string>"

INGESTION_SOURCE=AzureIoTHub IOTHUB_EVENTHUB_CONNECTION_STRING="<event hub endpoint>" \
  docker compose -f deploy/docker-compose.yml up -d ingestion
```

## Tests

```bash
cd services && dotnet test tests/FieldSense.Domain.Tests
cd simulator && pytest -q
```

## Roadmap

- [ ] Device provisioning with X.509 certificates (Azure DPS)
- [ ] Over-the-air (OTA) firmware updates
- [ ] Grafana dashboard on TimescaleDB
- [ ] CoAP gateway for constrained devices
- [ ] Deploy to Azure Container Apps with Bicep
