import warnings
from pathlib import Path
from typing import Dict, List

import numpy as np
import torch
from PIL import Image, ImageOps, ImageStat
from torch import nn
from torchvision import transforms

LETTER_CLASSES: List[str] = [chr(ord('a') + i) for i in range(26)]


def emnist_transform() -> transforms.Compose:
    from torchvision.transforms import functional as F

    return transforms.Compose([
        transforms.Lambda(lambda img: F.rotate(img, -90)),
        transforms.Lambda(lambda img: F.hflip(img)),
        transforms.ToTensor(),
        transforms.Normalize((0.1307,), (0.3081,)),
    ])


def inference_transform() -> transforms.Compose:
    return transforms.Compose([
        transforms.Resize((28, 28)),
        transforms.ToTensor(),
        transforms.Normalize((0.1307,), (0.3081,)),
    ])


class LetterCNN(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.features = nn.Sequential(
            nn.Conv2d(1, 32, kernel_size=3, padding=1),
            nn.ReLU(inplace=True),
            nn.BatchNorm2d(32),
            nn.MaxPool2d(2),
            nn.Conv2d(32, 64, kernel_size=3, padding=1),
            nn.ReLU(inplace=True),
            nn.BatchNorm2d(64),
            nn.MaxPool2d(2),
            nn.Conv2d(64, 128, kernel_size=3, padding=1),
            nn.ReLU(inplace=True),
            nn.BatchNorm2d(128),
            nn.AdaptiveAvgPool2d((1, 1)),
        )
        self.classifier = nn.Sequential(
            nn.Flatten(),
            nn.Dropout(0.3),
            nn.Linear(128, 64),
            nn.ReLU(inplace=True),
            nn.Dropout(0.3),
            nn.Linear(64, 26),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        x = self.features(x)
        return self.classifier(x)


def load_model(model_path: Path, device: torch.device) -> nn.Module:
    model = LetterCNN()
    with warnings.catch_warnings():
        warnings.simplefilter('ignore', category=FutureWarning)
        checkpoint = torch.load(model_path, map_location=device)
    model.load_state_dict(checkpoint['model_state_dict'])
    model.to(device)
    model.eval()
    return model


def prepare_image(image: Image.Image) -> Image.Image:
    grayscale = image.convert('L')
    stat = ImageStat.Stat(grayscale)
    if stat.mean[0] > 127:
        grayscale = ImageOps.invert(grayscale)

    np_img = np.array(grayscale)
    mask = np_img > 32
    if mask.any():
        coords = np.argwhere(mask)
        y0, x0 = coords.min(axis=0)
        y1, x1 = coords.max(axis=0) + 1
        pad_y = max(int(0.05 * (y1 - y0)), 2)
        pad_x = max(int(0.05 * (x1 - x0)), 2)
        y0 = max(y0 - pad_y, 0)
        x0 = max(x0 - pad_x, 0)
        y1 = min(y1 + pad_y, np_img.shape[0])
        x1 = min(x1 + pad_x, np_img.shape[1])
        cropped = grayscale.crop((x0, y0, x1, y1))
    else:
        cropped = grayscale

    max_side = max(cropped.size)
    padded = ImageOps.pad(cropped, size=(max_side, max_side), color=0, centering=(0.5, 0.5))
    return padded


def predict(model: nn.Module, image: Image.Image, device: torch.device) -> Dict[str, float]:
    image = prepare_image(image)
    transform = inference_transform()
    tensor = transform(image).unsqueeze(0).to(device)

    with torch.no_grad():
        logits = model(tensor)
        probabilities = torch.softmax(logits, dim=1).squeeze(0)

    return {letter: float(probabilities[idx]) for idx, letter in enumerate(LETTER_CLASSES)}
