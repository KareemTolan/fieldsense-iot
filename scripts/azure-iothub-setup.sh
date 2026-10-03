#!/usr/bin/env bash
# Provisions Azure IoT Hub (free tier) and one device, then prints the connection strings.
# Requires: Azure CLI logged in (az login) and the IoT extension (az extension add --name azure-iot)
set -euo pipefail

RG="${RG:-fieldsense-rg}"
LOCATION="${LOCATION:-westeurope}"
HUB="${HUB:-fieldsense-hub-$RANDOM}"
DEVICE="${DEVICE:-trap-001}"

az group create -n "$RG" -l "$LOCATION" -o none
az iot hub create -g "$RG" -n "$HUB" --sku F1 --partition-count 2 -o none
az iot hub device-identity create --hub-name "$HUB" -d "$DEVICE" -o none

echo
echo "Device connection string (simulator --connection-string):"
az iot hub device-identity connection-string show --hub-name "$HUB" -d "$DEVICE" -o tsv
echo
echo "Event Hub-compatible endpoint (IOTHUB_EVENTHUB_CONNECTION_STRING for the Ingestion service):"
az iot hub connection-string show -n "$HUB" --default-eventhub -o tsv
echo
echo "Delete everything later with: az group delete -n $RG --yes"
