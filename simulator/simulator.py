"""FieldSense device simulator.

Simulates N smart-trap devices sending telemetry, either to the local
MQTT broker (Mosquitto over TLS) or directly to Azure IoT Hub.

  python simulator.py mqtt  --devices 20 --interval 2
  python simulator.py azure --connection-string "HostName=...;DeviceId=...;SharedAccessKey=..."
"""
from __future__ import annotations

import argparse
import json
import random
import ssl
import time
from dataclasses import dataclass, field


@dataclass
class SimulatedDevice:
    device_id: str
    seq: int = 0
    battery_v: float = field(default_factory=lambda: random.uniform(3.9, 4.2))
    sensor_base: int = field(default_factory=lambda: random.randint(200, 600))

    def next_reading(self) -> dict:
        """Produce one telemetry message with realistic drift and rare spikes."""
        self.seq += 1
        self.battery_v = max(3.0, self.battery_v - random.uniform(0.0, 0.002))
        spike = random.random() < 0.03  # ~3% of readings simulate a trap trigger
        sensor = self.sensor_base + random.randint(-25, 25) + (2500 if spike else 0)
        return {
            "deviceId": self.device_id,
            "seq": self.seq,
            "timestamp": int(time.time() * 1000),
            "sensorValue": min(sensor, 4095),
            "batteryV": round(self.battery_v, 3),
            "temperatureC": round(random.uniform(22.0, 41.0), 1),
            "rssi": random.randint(-90, -45),
            "fw": "sim-1.0.0",
        }


def telemetry_topic(device_id: str) -> str:
    return f"fieldsense/devices/{device_id}/telemetry"


def run_mqtt(args: argparse.Namespace) -> None:
    import paho.mqtt.client as mqtt

    devices = [SimulatedDevice(f"sim-{i:03d}") for i in range(1, args.devices + 1)]
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="fieldsense-simulator")
    if args.user:
        client.username_pw_set(args.user, args.password)
    if args.ca_cert:
        client.tls_set(ca_certs=args.ca_cert, tls_version=ssl.PROTOCOL_TLS_CLIENT)
    client.connect(args.host, args.port, keepalive=30)
    client.loop_start()

    print(f"Sending telemetry for {len(devices)} devices to {args.host}:{args.port}")
    try:
        while True:
            for device in devices:
                payload = json.dumps(device.next_reading())
                client.publish(telemetry_topic(device.device_id), payload, qos=1)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        pass
    finally:
        client.loop_stop()
        client.disconnect()


def run_azure(args: argparse.Namespace) -> None:
    from azure.iot.device import IoTHubDeviceClient, Message

    client = IoTHubDeviceClient.create_from_connection_string(args.connection_string)
    client.connect()
    device_id = args.connection_string.split("DeviceId=")[1].split(";")[0]
    device = SimulatedDevice(device_id)

    def on_message(message):  # cloud-to-device commands
        print(f"C2D command received: {message.data.decode()}")

    client.on_message_received = on_message
    print(f"Sending telemetry to Azure IoT Hub as {device_id}")
    try:
        while True:
            msg = Message(json.dumps(device.next_reading()))
            msg.content_type = "application/json"
            msg.content_encoding = "utf-8"
            client.send_message(msg)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        pass
    finally:
        client.shutdown()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="mode", required=True)

    m = sub.add_parser("mqtt", help="publish to the local MQTT broker")
    m.add_argument("--host", default="localhost")
    m.add_argument("--port", type=int, default=8883)
    m.add_argument("--user", default="simulator")
    m.add_argument("--password", default="simulator-password")
    m.add_argument("--ca-cert", default="../deploy/mosquitto/certs/ca.crt")
    m.add_argument("--devices", type=int, default=10)
    m.add_argument("--interval", type=float, default=2.0)

    a = sub.add_parser("azure", help="publish to Azure IoT Hub")
    a.add_argument("--connection-string", required=True)
    a.add_argument("--interval", type=float, default=5.0)

    args = parser.parse_args()
    run_mqtt(args) if args.mode == "mqtt" else run_azure(args)


if __name__ == "__main__":
    main()
