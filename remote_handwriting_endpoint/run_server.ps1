param(
    [string]$ListenHost = "0.0.0.0",
    [int]$ListenPort = 8000,
    [string]$ApiKey = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey) -and -not $env:HANDWRITING_API_KEY) {
    Write-Warning "No HANDWRITING_API_KEY supplied. Endpoint will accept requests without authentication."
} elseif (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $env:HANDWRITING_API_KEY = $ApiKey
    Write-Host "HANDWRITING_API_KEY set for this session."
}

Write-Host "Starting handwriting endpoint on ${ListenHost}:${ListenPort}..."
python -m uvicorn app.main:app --host $ListenHost --port $ListenPort
