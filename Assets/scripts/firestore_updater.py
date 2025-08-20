import firebase_admin
from firebase_admin import credentials, firestore
import os
import time
import logging
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

# -------------------- Configuration --------------------

SERVICE_ACCOUNT_PATH = r'X:\homedojoDash\homedojo-dashboard\homedojo-dashboard-firebase-adminsdk-gxb7g-a46a4cb39b.json'
LOG_FILE_PATH = r'Z:\steamVRkickboxer\steamVRkickboxer\Assets\StreamingAssets\trace_completed.txt'
STUDENT_ID = 'o8Kg1JAjXfpi2UQ4NZQ0'

# -------------------- Logging Setup --------------------

logger = logging.getLogger()
logger.setLevel(logging.DEBUG)

fh = logging.FileHandler('firestore_updater.log', mode='w')
fh.setLevel(logging.DEBUG)
formatter = logging.Formatter('%(asctime)s [%(levelname)s] %(message)s')
fh.setFormatter(formatter)
logger.addHandler(fh)

ch = logging.StreamHandler()
ch.setLevel(logging.DEBUG)
ch.setFormatter(formatter)
logger.addHandler(ch)

# -------------------- Firebase Initialization --------------------

if not os.path.exists(SERVICE_ACCOUNT_PATH):
    logger.error(f"Service account key file not found at {SERVICE_ACCOUNT_PATH}")
    exit(1)

try:
    cred = credentials.Certificate(SERVICE_ACCOUNT_PATH)
    firebase_admin.initialize_app(cred)
    logger.info("Firebase initialized successfully.")
except Exception as e:
    logger.error(f"Failed to initialize Firebase: {e}")
    exit(1)

db = firestore.client()
students_ref = db.collection('students')

# -------------------- Firestore Pre-population --------------------

def ensure_level_exists(student_id, level_index=0):
    student_doc = students_ref.document(student_id)
    doc = student_doc.get()
    if not doc.exists:
        logger.error(f"Student document '{student_id}' does not exist.")
        return False

    data = doc.to_dict()
    levels = data.get('Levels', [])

    if not isinstance(levels, list):
        logger.error("'Levels' field is not a list. Resetting to an empty list.")
        levels = []

    while len(levels) <= level_index:
        level_data = {
            "accuracy": 0,
            "completed": False,
            "level_name": f"Lesson {len(levels) + 1}",
            "mistakes": [],
            "new_letter": chr(65 + len(levels))  # 'A', 'B', 'C', etc.
        }
        levels.append(level_data)

    student_doc.set({"Levels": levels}, merge=True)
    logger.info(f"Ensured Level {level_index} exists for student '{student_id}'.")
    return True

def mark_next_incomplete_level_completed():
    try:
        student_doc = students_ref.document(STUDENT_ID)
        doc = student_doc.get()

        if not doc.exists:
            logger.error(f"Student document '{STUDENT_ID}' does not exist.")
            return

        data = doc.to_dict()
        levels = data.get('Levels', [])

        incomplete_index = None
        for i, level in enumerate(levels):
            if not level.get('completed', False):
                incomplete_index = i
                break

        if incomplete_index is None:
            logger.info("All lessons are completed.")
            return

        logger.info(f"Marking Level {incomplete_index + 1} as completed.")
        current_level = levels[incomplete_index]
        current_level['completed'] = True
        current_level['accuracy'] = 100  # Set accuracy to 100 since trace is done

        student_doc.update({'Levels': levels})
        logger.info(f"Firestore updated. Level {incomplete_index + 1} marked as completed.")
    except Exception as e:
        logger.error(f"Error marking next incomplete level complete: {e}")

# -------------------- File Monitoring --------------------

class TraceCompleteHandler(FileSystemEventHandler):
    def __init__(self, file_path):
        super().__init__()
        self.file_path = os.path.abspath(file_path)
        self.last_position = 0
        if os.path.exists(self.file_path):
            self.last_position = os.path.getsize(self.file_path)
            logger.info(f"Initial log file size: {self.last_position} bytes.")
        else:
            logger.info(f"File '{self.file_path}' does not exist yet.")

    def on_modified(self, event):
        if os.path.abspath(event.src_path) == self.file_path:
            logger.debug(f"Detected modification in '{self.file_path}'.")
            self.check_for_trace_completed()

    def on_created(self, event):
        if os.path.abspath(event.src_path) == self.file_path:
            logger.debug(f"Detected creation of '{self.file_path}'.")
            self.check_for_trace_completed()

    def check_for_trace_completed(self):
        try:
            with open(self.file_path, 'r') as f:
                data = f.read().strip()
                if data == "TRACE_COMPLETED":
                    logger.info("TRACE_COMPLETED detected in file.")
                    mark_next_incomplete_level_completed()
                else:
                    logger.debug("File read but does not indicate trace completion.")
        except Exception as e:
            logger.error(f"Error reading file: {e}")

def start_monitoring(log_file_path):
    if not ensure_level_exists(STUDENT_ID, level_index=0):
        logger.error("Cannot proceed without Level 1 pre-populated.")
        exit(1)

    event_handler = TraceCompleteHandler(log_file_path)
    observer = Observer()
    observer.schedule(event_handler, path=os.path.dirname(os.path.abspath(log_file_path)), recursive=False)
    observer.start()
    logger.info(f"Started monitoring '{log_file_path}' for trace completion.")

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        observer.stop()
        logger.info("Stopping file monitoring.")
    observer.join()

if __name__ == "__main__":
    if not os.path.exists(LOG_FILE_PATH):
        open(LOG_FILE_PATH, 'a').close()
        logger.info(f"Created file at '{LOG_FILE_PATH}'.")
    start_monitoring(LOG_FILE_PATH)
