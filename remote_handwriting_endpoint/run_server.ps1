param(
    [string]$Host = "0.0.0.0",
    [int]$Port = 8000
)

$ErrorActionPreference = "Stop"

Write-Host "Starting handwriting endpoint on $Host:$Port..."
python -m uvicorn app.main:app --host $Host --port $Port
