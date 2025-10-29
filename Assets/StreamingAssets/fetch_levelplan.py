#!/usr/bin/env python
import requests
import json
import sys
import os

# -------------------------------
# CONFIG: Update these values
# -------------------------------
API_KEY = "AIzaSy...yourKey..."
PROJECT_ID = "homedojo-dashboard"
STUDENT_DOC_ID = "vM6TxmNmsT3cklZctAtK"
OUTPUT_JSON_FILENAME = "LevelPlan.json"

FIRESTORE_URL = (
    f"https://firestore.googleapis.com/v1/projects/{PROJECT_ID}"
    f"/databases/(default)/documents/students/{STUDENT_DOC_ID}?key={API_KEY}"
)

def parse_string_array(array_field):
    results = []
    if not array_field or "arrayValue" not in array_field:
        return results
    values = array_field["arrayValue"].get("values", [])
    for val in values:
        if "stringValue" in val:
            results.append(val["stringValue"])
    return results

def flatten_levelplan(typed_data):
    if not typed_data or "arrayValue" not in typed_data:
        return []
    values = typed_data["arrayValue"].get("values", [])
    if not values:
        return []
    first_entry = values[0]
    fields = first_entry.get("mapValue", {}).get("fields", {})

    letters = parse_string_array(fields.get("Letters"))
    phonemes = parse_string_array(fields.get("Phonemes"))
    modes = parse_string_array(fields.get("Modes"))

    # Sanitize phoneme list so it always aligns 1:1 with the letters we will show.
    normalized_letters = []
    normalized_phonemes = []
    for idx, letter in enumerate(letters):
        cleaned = (letter or "").strip()
        if cleaned:
            cleaned = cleaned[0].lower()
        normalized_letters.append(cleaned)
        if idx < len(phonemes) and phonemes[idx]:
            normalized_phonemes.append(cleaned.lower())
        else:
            normalized_phonemes.append(cleaned.lower())

    return [{
        "Letters": normalized_letters,
        "Phonemes": normalized_phonemes,
        "Modes": modes
    }]

def main():
    try:
        response = requests.get(FIRESTORE_URL)
        if response.status_code != 200:
            print(f"Error fetching document: {response.status_code} {response.text}")
            sys.exit(1)

        document = response.json()
    except Exception as e:
        print(f"Error making Firestore request: {e}")
        sys.exit(1)

    fields = document.get("fields", {})
    if "levelplan" not in fields:
        print("No 'levelplan' field found in the document.")
        sys.exit(0)

    # Flatten the plan
    levelplan_typed = fields["levelplan"]
    flattened_plan = flatten_levelplan(levelplan_typed)

    # Also parse currentLetter
    current_letter = ""
    if "currentLetter" in fields and "stringValue" in fields["currentLetter"]:
        current_letter = fields["currentLetter"]["stringValue"]

    # Construct the final dict to save
    output_data = {
        "levelplan": flattened_plan,
        "currentLetter": current_letter
    }

    # Save to local JSON
    script_dir = os.path.dirname(os.path.abspath(__file__))
    output_path = os.path.join(script_dir, OUTPUT_JSON_FILENAME)

    try:
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(output_data, f, indent=2)
        print(f"Successfully wrote {OUTPUT_JSON_FILENAME} with flattened plan!")
    except Exception as e:
        print(f"Error saving {OUTPUT_JSON_FILENAME}: {e}")

if __name__ == "__main__":
    main()
