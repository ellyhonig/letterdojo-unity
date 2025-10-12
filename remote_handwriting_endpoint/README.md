# Remote Handwriting Endpoint

This directory contains the “perfect endpoint” classifier. Run it on your PC (or
host it on a VM) and point the Quest client at it for remote grading.

## Prerequisites

- Python 3.10+ installed
- (Optional) a virtual environment so dependencies stay isolated

## Setup

```powershell
cd remote_handwriting_endpoint
python -m venv .venv         # optional
.\.venv\Scripts\Activate.ps1 # only if you created the venv

pip install --upgrade pip
pip install -r requirements.txt
```

## Launching the server

Use the included script so you can supply an API key:

```powershell
.\run_server.ps1 -Host 0.0.0.0 -Port 8000 -ApiKey "super-secret-key"
```

If you omit `-ApiKey`, the endpoint accepts unauthenticated requests—fine for
localhost, but not recommended once you expose it beyond your machine.

This exposes two FastAPI routes:

- `POST /predict` – full probability dictionary
- `POST /predict/top3` – top three predictions (Unity uses this)

Both expect a multipart form field named `file` with a PNG or JPG. When an API
key is set, clients must include an `x-api-key` header.

## Quick local test

```powershell
$png = "$env:USERPROFILE\Downloads\d.png"
curl -H "x-api-key: super-secret-key" `
     -X POST http://127.0.0.1:8000/predict/top3 `
     -F "file=@$png"
```

Replace the API key string with whatever you passed to `run_server.ps1`.

## Making it reachable from the Quest/off-network

1. **Static LAN IP** – reserve one for this PC on your router.
2. **Windows firewall** – allow inbound TCP on the chosen port (e.g. 8000).
3. **Router port forwarding** – forward an external port (e.g. 8080) to
   `<LAN_IP>:8000`.
4. **Dynamic DNS** – optional but useful if your public IP changes.
5. **TLS / reverse proxy** – wrap behind IIS/Nginx or use a tunnel (Cloudflare,
   Tailscale, WireGuard) before exposing it broadly.

## Unity configuration

In `DictationManager`:

- `Use Remote Classifier` = true
- `Remote Classifier Base Url` = `http://<public-host>:<port>` (or your DNS name)
- `Remote API Key` = the same string passed to the server
- (Optional) disable the local Sentis toggle if you want remote-only grading

Rebuild/deploy to Quest. The Quest will now POST to your remote server when the
user idles after drawing. Watch the server console (`run_server.ps1`) to confirm
requests are coming through.
