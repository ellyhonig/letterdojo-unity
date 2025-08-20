import os
import time
import pyaudio
import logging
from pocketsphinx import Pocketsphinx, get_model_path

logging.basicConfig(level=logging.DEBUG, format='%(asctime)s [%(levelname)s] %(message)s', filename='phoneme_recognition.log', filemode='w')

custom_dict_content = """
BUH B UH
CUH K AH
DUH D AH
FUH F AH
GUH G AH
LUH L AH
MUH M AH
NUH N AH
PUH P AH
RUH R AH
SUH S AH
TUH T EH
VUH V AH
YUH Y AH
ZUH Z AH
"""

# Adjust paths to point to StreamingAssets
current_dir = os.path.dirname(os.path.abspath(__file__))
streaming_assets_path = os.path.abspath(os.path.join(current_dir, "..", "..", "StreamingAssets"))

if not os.path.exists(streaming_assets_path):
    os.makedirs(streaming_assets_path)

custom_dict_path = os.path.join(streaming_assets_path, "custom_phonemes.dict")
with open(custom_dict_path, 'w') as f:
    f.write(custom_dict_content)
logging.info('Custom phoneme dictionary created.')

jsgf_content = """
#JSGF V1.0;

grammar phonemes;

public <phoneme> = BUH | CUH | DUH | FUH | GUH | LUH | MUH | NUH | PUH | RUH | SUH | YUH | ZUH;
"""

jsgf_path = os.path.join(streaming_assets_path, "phoneme.jsgf")
with open(jsgf_path, 'w') as f:
    f.write(jsgf_content)
logging.info('JSGF grammar file created.')

try:
    ps = Pocketsphinx(
        hmm=os.path.join(get_model_path(), 'en-us'),
        jsgf=jsgf_path,
        dict=custom_dict_path,
    )
    logging.info('PocketSphinx decoder initialized.')
except Exception as e:
    logging.error(f'Failed to initialize decoder: {e}')
    raise e

p = pyaudio.PyAudio()

format = pyaudio.paInt16
channels = 1
rate = 16000
chunk = 1024

try:
    stream = p.open(
        format=format,
        channels=channels,
        rate=rate,
        input=True,
        frames_per_buffer=chunk,
    )
    logging.info('Audio stream opened.')
except Exception as e:
    logging.error(f'Failed to open audio stream: {e}')
    raise e

ps.start_utt()
logging.debug('Decoder utterance started.')

def debounce(last_recognition_time, debounce_time=0.3):
    return time.time() - last_recognition_time > debounce_time

last_recognition_time = 0
min_frames = 88  
utterance_timeout = 2.0
utterance_start_time = time.time()
prob_threshold = 0.6  
last_recognized_phoneme = None 

output_file = os.path.join(streaming_assets_path, "phoneme_output.txt")

try:
    logging.info("Starting phoneme recognition loop...")
    print("Recording for phoneme recognition...")
    last_hypothesis = ""

    while True:
        try:
            buf = stream.read(chunk, exception_on_overflow=False)
            if not buf:
                logging.warning('No data read from stream.')
                continue

            ps.process_raw(buf, False, False)
            hyp = ps.hyp()
            n_frames = ps.n_frames()

            if hyp:
                hyp_str = hyp.hypstr.strip()
                logging.debug(f'Hypothesis: {hyp_str}, Frames: {n_frames}')

                if debounce(last_recognition_time):
                    if hyp_str != last_recognized_phoneme:
                        if n_frames >= min_frames:
                            best_score = hyp.best_score
                            prob = ps.get_logmath().exp(hyp.prob)
                            logging.debug(f'Probability: {prob}, Threshold: {prob_threshold}')

                            if prob > prob_threshold:
                                with open(output_file, 'w') as f:
                                    f.write(hyp_str)

                                print(f"Recognized phoneme: {hyp_str} (frames: {n_frames}, best_score: {best_score}, prob: {prob:.4f})")
                                logging.info(f'Recognized phoneme: {hyp_str}')
                                last_hypothesis = hyp_str
                                last_recognition_time = time.time()
                                last_recognized_phoneme = hyp_str
                            else:
                                logging.info(f"Ignored low-confidence recognition: {hyp_str} (prob: {prob:.4f})")
                        else:
                            logging.debug(f'Not enough frames: {n_frames}')
                        ps.end_utt()
                        ps.start_utt()
                        utterance_start_time = time.time()
            else:
                logging.debug(f'No hypothesis generated. Frames processed: {n_frames}')
                if time.time() - utterance_start_time > utterance_timeout:
                    logging.debug('Utterance timeout reached.')
                    ps.end_utt()
                    ps.start_utt()
                    utterance_start_time = time.time()

        except Exception as e:
            logging.error(f'Error in main loop: {e}', exc_info=True)
            ps.end_utt()
            ps.start_utt()
            continue

except KeyboardInterrupt:
    print("Stopped.")
    logging.info('KeyboardInterrupt received, stopping.')
except Exception as e:
    logging.error(f'Unhandled exception: {e}', exc_info=True)
finally:
    stream.stop_stream()
    stream.close()
    p.terminate()
    ps.end_utt()
    logging.info('Resources cleaned up.')
