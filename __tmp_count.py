from pathlib import Path
text = Path(r"Assets/scripts/KeyFrame/DictationManager.cs").read_text(encoding="utf-8", errors="ignore")
print(text.count('{'), text.count('}'))
