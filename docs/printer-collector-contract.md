# Printer Collector Contract

This document defines how an external printer collector should push telemetry into WeismanTracker.

## Endpoint

- **Method:** `POST`
- **URL:** `/api/printers/telemetry`
- **Content-Type:** `application/json`
- **Auth header:** `X-Printer-Collector-Key: <collector-api-key>`

Example base URL:

- `http://100.75.86.96:5224/api/printers/telemetry`

## Required auth

The collector must send:

```http
X-Printer-Collector-Key: weismantracker-dev-printer-collector
```

For production, replace that dev key with a real secret in API configuration.

## Payload schema

```json
{
  "collectorId": "printerops-lv426",
  "capturedAtUtc": "2026-04-21T17:05:00Z",
  "printer": {
    "name": "Ricoh Front Office",
    "hostname": "ricoh-front",
    "ipAddress": "10.20.51.204",
    "manufacturer": "Ricoh",
    "model": "MP3353",
    "serialNumber": "RCH123456"
  },
  "status": {
    "state": "online",
    "alert": null
  },
  "usage": {
    "totalPages": 123456,
    "monoPages": 120000,
    "colorPages": 3456
  },
  "consumables": [
    {
      "name": "Black Toner",
      "percentRemaining": 42,
      "status": "ok"
    },
    {
      "name": "Waste Toner",
      "percentRemaining": 12,
      "status": "monitor"
    }
  ]
}
```

## Field notes

### `collectorId`
- string
- identifies the collector instance/site
- useful when multiple offices or pollers exist

### `capturedAtUtc`
- ISO 8601 UTC timestamp
- when the collector actually read the printer

### `printer`
Identity block. WeismanTracker resolves printer identity in this order:

1. `serialNumber`
2. `hostname`
3. `ipAddress`
4. `name`

So if possible, always send a stable serial number.

### `status.state`
Recommended values:

- `online`
- `offline`
- `warning`
- `error`
- `idle`
- `ready`

### `status.alert`
- nullable string
- current device warning/error summary

### `usage`
All values optional:

- `totalPages`
- `monoPages`
- `colorPages`

### `consumables`
List of toner/drum/etc. items.

Each item:
- `name` — required
- `percentRemaining` — nullable decimal
- `status` — nullable string

## Response

Successful POST returns the normalized printer row stored by WeismanTracker.

## Suggested collector behavior

For each printer poll:

1. collect SNMP/vendor telemetry
2. normalize into this JSON payload
3. POST to WeismanTracker
4. log success/failure with printer identity and HTTP status
5. retry transient failures

## Recommended retry policy

- retry on timeout / connection failure / HTTP 5xx
- do not retry on HTTP 401 without fixing the API key
- do not retry on HTTP 400 without fixing payload validation

## Minimal curl example

```bash
curl -X POST http://100.75.86.96:5224/api/printers/telemetry \
  -H 'Content-Type: application/json' \
  -H 'X-Printer-Collector-Key: weismantracker-dev-printer-collector' \
  -d @printer-snapshot.json
```
