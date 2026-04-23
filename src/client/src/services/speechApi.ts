import { API_BASE_URL } from '../config/apiConfig';

export interface TranscribeAudioParams {
  audioBlob: Blob;
  language?: string;
}

export interface TranscriptionResult {
  text: string;
  durationSeconds: number;
}

export async function transcribeAudio(params: TranscribeAudioParams): Promise<TranscriptionResult> {
  const formData = new FormData();
  
  const mimeType = params.audioBlob.type || 'audio/webm';
  const extension = getExtensionFromMimeType(mimeType);
  const fileName = `recording.${extension}`;
  
  formData.append('audio', params.audioBlob, fileName);
  
  if (params.language) {
    formData.append('language', params.language);
  }

  const response = await fetch(`${API_BASE_URL}/speech/transcribe`, {
    method: 'POST',
    body: formData,
  });

  if (!response.ok) {
    let errorMessage = 'Transcription failed';
    try {
      const errorData = await response.json();
      errorMessage = errorData.error || errorData.message || errorMessage;
    } catch {
      // Use default message if JSON parsing fails
    }
    
    throw new Error(errorMessage);
  }

  return response.json();
}

function getExtensionFromMimeType(mimeType: string): string {
  const mimeToExt: Record<string, string> = {
    'audio/webm': 'webm',
    'audio/webm;codecs=opus': 'webm',
    'audio/ogg': 'ogg',
    'audio/ogg;codecs=opus': 'ogg',
    'audio/wav': 'wav',
    'audio/mp3': 'mp3',
    'audio/mpeg': 'mp3',
    'audio/mp4': 'm4a',
    'audio/aac': 'aac',
    'audio/flac': 'flac',
    'audio/opus': 'opus',
  };
  
  return mimeToExt[mimeType] || 'webm';
}
