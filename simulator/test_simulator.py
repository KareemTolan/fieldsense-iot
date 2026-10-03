from simulator import SimulatedDevice, telemetry_topic


def test_reading_has_expected_fields():
    reading = SimulatedDevice("sim-001").next_reading()
    assert reading["deviceId"] == "sim-001"
    assert {"seq", "timestamp", "sensorValue", "batteryV", "temperatureC", "rssi"} <= reading.keys()


def test_sequence_increments_and_values_stay_in_range():
    device = SimulatedDevice("sim-002")
    for expected_seq in range(1, 501):
        r = device.next_reading()
        assert r["seq"] == expected_seq
        assert 0 <= r["sensorValue"] <= 4095
        assert 3.0 <= r["batteryV"] <= 4.2


def test_topic_format():
    assert telemetry_topic("trap-9") == "fieldsense/devices/trap-9/telemetry"
