import sys
import os
import subprocess

def main():
    if len(sys.argv) != 2:
        print("Usage: python display_filename.py <file_path>")
        sys.exit(1)
    file_path = sys.argv[1]
    file_name = os.path.basename(file_path)
    print(f"File name: {file_name}")

    # Correct output directory path
    output_dir = '/app/shared/content/audio-input'
    os.makedirs(output_dir, exist_ok=True)
    base_name = os.path.splitext(file_name)[0]
    output_file = os.path.join(output_dir, base_name + '.wav')

    # Extract audio using ffmpeg with -y to overwrite
    command = ['ffmpeg', '-y', '-i', file_path, '-vn', '-acodec', 'pcm_s16le', '-ar', '44100', '-ac', '2', output_file]
    try:
        result = subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        print(f"Audio extracted and saved to: {output_file}")
        # Check if file exists
        if os.path.exists(output_file):
            print("Confirmed: audio file exists after extraction.")
        else:
            print("Warning: audio file does not exist after extraction.")
    except subprocess.CalledProcessError as e:
        print(f"Failed to extract audio. ffmpeg stderr output:\n{e.stderr.decode()}")

if __name__ == "__main__":
    main()
