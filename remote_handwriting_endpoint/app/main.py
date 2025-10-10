import io
from pathlib import Path
from typing import Any, Dict, List, Tuple

import torch
from fastapi import FastAPI, File, HTTPException, Request, UploadFile
from fastapi.responses import HTMLResponse
from fastapi.templating import Jinja2Templates
from PIL import Image, UnidentifiedImageError

from app.model_utils import LETTER_CLASSES, load_model, predict

BASE_DIR = Path(__file__).resolve().parent.parent
MODEL_PATH = BASE_DIR / 'models' / 'letter_cnn.pt'
TEMPLATES_DIR = BASE_DIR / 'templates'

app = FastAPI(title='Handwriting Letter Endpoint')
templates = Jinja2Templates(directory=str(TEMPLATES_DIR))


@app.on_event('startup')
async def startup_event() -> None:
    if not MODEL_PATH.exists():
        raise RuntimeError(f'Model weights not found at {MODEL_PATH}')
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    app.state.device = device
    app.state.model = load_model(MODEL_PATH, device)
    print(f'Model loaded on {device}')


@app.get('/', response_class=HTMLResponse)
async def index(request: Request) -> HTMLResponse:
    return templates.TemplateResponse('index.html', {'request': request})


async def _run_inference(file: UploadFile) -> Tuple[Dict[str, float], List[Tuple[str, float]]]:
    if file.content_type not in {'image/png', 'image/jpeg', 'image/jpg'}:
        raise HTTPException(status_code=400, detail='Only PNG or JPG images are supported.')

    contents = await file.read()
    try:
        image = Image.open(io.BytesIO(contents))
    except UnidentifiedImageError as exc:
        raise HTTPException(status_code=400, detail='Uploaded file is not a valid image.') from exc

    model = app.state.model
    device: torch.device = app.state.device
    probabilities = predict(model, image, device)
    sorted_predictions: List[Tuple[str, float]] = sorted(probabilities.items(), key=lambda item: item[1], reverse=True)
    return probabilities, sorted_predictions


@app.post('/predict')
async def predict_endpoint(file: UploadFile = File(...)) -> Dict[str, Any]:
    probabilities, sorted_predictions = await _run_inference(file)
    top_letter, top_confidence = sorted_predictions[0]

    return {
        'top_prediction': {
            'letter': top_letter,
            'confidence': top_confidence,
        },
        'sorted_predictions': sorted_predictions,
        'predictions': probabilities,
        'letters': LETTER_CLASSES,
    }


@app.post('/predict/top3')
async def predict_top3_endpoint(file: UploadFile = File(...)) -> Dict[str, Any]:
    _, sorted_predictions = await _run_inference(file)
    top_predictions = [
        {'letter': letter, 'confidence': confidence}
        for letter, confidence in sorted_predictions[:3]
    ]
    return {'top_predictions': top_predictions}
