# Remote Handwriting Endpoint

This folder contains a self-contained copy of the desktop handwriting classifier
that powers the on-device experiments. Run it on your PC and point the Quest
client at it to get the “perfect endpoint” results.

## Prerequisites

- Python 3.10+ on the host machine
- (Optional but recommended) a virtual environment so packages do not leak into
  the system Python

## Setup

```powershell
cd remote_handwriting_endpoint
# Optional: create / activate a venv
python -m venv .venv
.\.venv\Scripts\Activate.ps1

pip install --upgrade pip
pip install -r requirements.txt
```

## Running the server

```powershell
# From remote_handwriting_endpoint (and with the venv activated if you use one)
python -m uvicorn app.main:app --host 0.0.0.0 --port 8000
```

This exposes two FastAPI endpoints:

- `POST /predict` – full probability dictionary
- `POST /predict/top3` – top three predictions (used by Unity)

Both expect a multipart form field named `file` containing a PNG (or JPG).

## Quick test (PowerShell / curl)

```powershell
$png = "$env:USERPROFILE\Downloads\d.png"
curl -X POST http://127.0.0.1:8000/predict/top3 -F "file=@$png"
```

You should see a JSON payload with the top guesses. The Unity client now points
at `http://127.0.0.1:8000` by default; change the serialized `remoteClassifierBaseUrl`
in `DictationManager` if you host it elsewhere or want a different port.
