import os
import sys
import json
from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
import shutil
from pyannote.audio import Pipeline
import torch

def create_rttm_with_pyannote(audio_file_path, output_folder):
    if not os.path.exists(output_folder):
        os.makedirs(output_folder)

    base_name = os.path.splitext(os.path.basename(audio_file_path))[0]
    output_file = os.path.join(output_folder, f"{base_name}.rttm")

    # Check if the output file already exists
    if os.path.exists(output_file):
        print(f"RTTM file already exists for {audio_file_path}. Skipping processing.")
        return

    # Handle spaces in filename by copying to temp file without spaces
    if ' ' in base_name:
        temp_file_path = os.path.join(output_folder, base_name.replace(' ', '') + os.path.splitext(audio_file_path)[1])
        shutil.copyfile(audio_file_path, temp_file_path)
        file_to_process = temp_file_path
    else:
        temp_file_path = None
        file_to_process = audio_file_path

    # Initialize pipeline
    diarization_pipeline = Pipeline.from_pretrained(
        "pyannote/speaker-diarization-3.1"
    )

    # Use GPU if available
    if torch.cuda.is_available():
        diarization_pipeline.to(torch.device("cuda"))

    # Perform diarization
    diarization = diarization_pipeline(file_to_process)

    # Write RTTM output
    with open(output_file, "w") as rttm:
        diarization.write_rttm(rttm)

    # Clean up temp file
    if temp_file_path:
        os.remove(temp_file_path)

    print(f"Processed {audio_file_path} and saved RTTM to {output_file}")

def transcribe_and_save(audio_file_path, output_folder):
    base_name = os.path.basename(os.path.splitext(audio_file_path)[0])
    text_file_name = os.path.join(output_folder, f"{base_name}.txt")
    json_file_name = os.path.join(output_folder, f"{base_name}.json")

    # Check if output files already exist
    if os.path.exists(text_file_name) and os.path.exists(json_file_name):
        print(f"Transcription files already exist for {audio_file_path}. Skipping processing.")
        return

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    torch_dtype = torch.float16 if torch.cuda.is_available() else torch.float32

    model_id = "openai/whisper-large-v3"
    model = AutoModelForSpeechSeq2Seq.from_pretrained(
        model_id, torch_dtype=torch_dtype, low_cpu_mem_usage=False, use_safetensors=True
    )
    model.to(device)

    processor = AutoProcessor.from_pretrained(model_id)

    pipe = pipeline(
        "automatic-speech-recognition",
        model=model,
        tokenizer=processor.tokenizer,
        feature_extractor=processor.feature_extractor,
        max_new_tokens=128,
        chunk_length_s=30,
        batch_size=16,
        return_timestamps=True,
        torch_dtype=torch_dtype,
        device=device,
    )

    # Process the audio file using the pipe function
    result = pipe(audio_file_path, return_timestamps=True)

    # Write the transcription to a text file with timestamps
    with open(text_file_name, 'w', encoding='utf-8') as text_file:
        for chunk in result["chunks"]:
            start, end = chunk['timestamp']
            timestamp = f"({start}, {end})"
            text_file.write(f"[{timestamp}]: {chunk['text']}\n")

    # Save the transcription as a JSON file
    with open(json_file_name, 'w', encoding='utf-8') as json_file:
        json.dump(result["chunks"], json_file, indent=4)

    print(f"Transcription saved to {text_file_name}")
    print(f"Transcription JSON saved to {json_file_name}")
    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    torch_dtype = torch.float16 if torch.cuda.is_available() else torch.float32

    model_id = "openai/whisper-large-v3"
    model = AutoModelForSpeechSeq2Seq.from_pretrained(
        model_id, torch_dtype=torch_dtype, low_cpu_mem_usage=False, use_safetensors=True
    )
    model.to(device)

    processor = AutoProcessor.from_pretrained(model_id)

    pipe = pipeline(
        "automatic-speech-recognition",
        model=model,
        tokenizer=processor.tokenizer,
        feature_extractor=processor.feature_extractor,
        max_new_tokens=128,
        chunk_length_s=30,
        batch_size=16,
        return_timestamps=True,
        torch_dtype=torch_dtype,
        device=device,
    )

    # Process the audio file using the pipe function
    result = pipe(audio_file_path, return_timestamps=True)

    # Extract the base name without the extension and create new file names
    base_name = os.path.basename(os.path.splitext(audio_file_path)[0])
    text_file_name = os.path.join(output_folder, f"{base_name}.txt")
    json_file_name = os.path.join(output_folder, f"{base_name}.json")

    # Write the transcription to a text file with timestamps
    with open(text_file_name, 'w', encoding='utf-8') as text_file:
        for chunk in result["chunks"]:
            start, end = chunk['timestamp']
            timestamp = f"({start}, {end})"
            text_file.write(f"[{timestamp}]: {chunk['text']}\n")

    # Save the transcription as a JSON file
    with open(json_file_name, 'w', encoding='utf-8') as json_file:
        json.dump(result["chunks"], json_file, indent=4)

    print(f"Transcription saved to {text_file_name}")
    print(f"Transcription JSON saved to {json_file_name}")

def read_json(json_file):
    with open(json_file, 'r', encoding='utf-8') as file:
        return json.load(file)

def read_rttm(rttm_file):
    rttm_data = []
    with open(rttm_file, 'r', encoding='utf-8') as file:
        for line in file:
            parts = line.strip().split()
            rttm_data.append({
                "turn_onset": float(parts[3]),
                "duration": float(parts[4]),
                "speaker": parts[7]
            })
    return rttm_data

def seconds_to_hms(seconds):
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = seconds % 60
    return f"{h:02}:{m:02}:{s:06.3f}"

def match_speaker(json_start, json_end, rttm_data):
    best_match = None
    max_overlap = -1
    
    for entry in rttm_data:
        rttm_start = entry["turn_onset"]
        rttm_end = rttm_start + entry["duration"]

        overlap_start = max(json_start, rttm_start)
        overlap_end = min(json_end, rttm_end)
        overlap_duration = max(0, overlap_end - overlap_start)
        
        if overlap_duration > max_overlap:
            max_overlap = overlap_duration
            best_match = entry["speaker"]

    return best_match if best_match else "Unknown"

def create_final_file(json_file, rttm_file, output_file):
    json_data = read_json(json_file)
    rttm_data = read_rttm(rttm_file)
    
    with open(output_file, 'w', encoding='utf-8') as text_file:
        previous_end = None  # Track the end time of the previous item
        for i, chunk in enumerate(json_data):
            start = chunk['timestamp'][0]
            if len(chunk['timestamp']) > 1:
                end = chunk['timestamp'][1]
            
            if end == None:
                if i == len(json_data) - 1 and previous_end is not None:  # Last item with no end time
                    end = previous_end + 5  # Assuming a reasonable default duration
                elif i == len(json_data) - 1 and previous_end is None:  # Single item list
                    end = start + 5  # Default duration for single item lists
                else:
                    continue  # If not the last item and no end time, skip
            
            previous_end = end  # Update the previous end time
            speaker = match_speaker(start, end, rttm_data)
            formatted_timestamp = f"({seconds_to_hms(start)}, {seconds_to_hms(end)})"
            text_file.write(f"[{speaker}] : [{formatted_timestamp}] : {chunk['text']}\n")

if __name__ == '__main__':
    if len(sys.argv) != 2:
        print("Usage: python transcribe_whisper.py <audio_file_path>")
        sys.exit(1)

    audio_file_path = sys.argv[1]
    output_folder = "/app/shared/content/transcription-input"

    if not os.path.exists(output_folder):
        os.makedirs(output_folder)

    transcribe_and_save(audio_file_path, output_folder)
    create_rttm_with_pyannote(audio_file_path, output_folder)

    base_name = os.path.basename(os.path.splitext(audio_file_path)[0])
    json_file = os.path.join(output_folder, f"{base_name}.json")
    rttm_file = os.path.join(output_folder, f"{base_name}.rttm")
    output_file = os.path.join(output_folder, f"{base_name}.transcribed.txt")

    create_final_file(json_file, rttm_file, output_file)
    print(f"Final transcription with speaker information saved to {output_file}")