# Qwen3-TTS Service for Maggie
# Provides HTTP API for text-to-speech generation

from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse, FileResponse
from pydantic import BaseModel
from typing import Optional, List
import torch
import soundfile as sf
import io
import os
import sys
import logging
import numpy as np

# Setup logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Maggie TTS Service", version="1.0")

# Global model instance
tts_model = None
current_speaker = "Serena"  # Default feminine voice for Maggie

# Speaker options with descriptions
SPEAKERS = {
    "Serena": "Warm, gentle young female voice (Chinese native)",
    "Vivian": "Bright, slightly edgy young female voice (Chinese native)",
    "Ono_Anna": "Playful Japanese female voice with light, nimble timbre",
    "Sohee": "Warm Korean female voice with rich emotion",
    "Ryan": "Dynamic male voice with strong rhythmic drive (English)",
    "Aiden": "Sunny American male voice with clear midrange"
}

class TTSRequest(BaseModel):
    text: str
    speaker: Optional[str] = None
    language: Optional[str] = "English"
    instruct: Optional[str] = None  # e.g., "Speak happily", "Speak softly"
    speed: Optional[float] = 1.0

class TTSResponse(BaseModel):
    success: bool
    speaker: str
    sample_rate: int
    audio_format: str = "wav"
    message: Optional[str] = None

class VoiceInfo(BaseModel):
    name: str
    description: str
    language: str

@app.on_event("startup")
async def load_model():
    """Load Qwen3-TTS model on startup"""
    global tts_model
    
    try:
        from qwen_tts import Qwen3TTSModel
        
        model_name = os.getenv("QWEN_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice")
        device = os.getenv("QWEN_TTS_DEVICE", "cuda:0" if torch.cuda.is_available() else "cpu")
        
        logger.info(f"Loading Qwen3-TTS model: {model_name}")
        logger.info(f"Using device: {device}")
        
        tts_model = Qwen3TTSModel.from_pretrained(
            model_name,
            device_map=device,
            dtype=torch.bfloat16 if torch.cuda.is_available() else torch.float32,
            attn_implementation="eager"  # Safer default
        )
        
        logger.info("Qwen3-TTS model loaded successfully")
        logger.info(f"Available speakers: {list(SPEAKERS.keys())}")
        
    except Exception as e:
        logger.error(f"Failed to load Qwen3-TTS model: {e}")
        logger.error("TTS will be unavailable until model is loaded")
        tts_model = None

@app.get("/health")
async def health():
    """Health check endpoint"""
    return {
        "status": "ok" if tts_model is not None else "degraded",
        "model_loaded": tts_model is not None,
        "current_speaker": current_speaker,
        "available_speakers": list(SPEAKERS.keys())
    }

@app.get("/voices", response_model=List[VoiceInfo])
async def list_voices():
    """List available voices"""
    voices = []
    for name, desc in SPEAKERS.items():
        lang = "Chinese" if "Chinese" in desc else ("Japanese" if "Japanese" in desc else ("Korean" if "Korean" in desc else "English"))
        voices.append(VoiceInfo(name=name, description=desc, language=lang))
    return voices

@app.post("/tts")
async def text_to_speech(request: TTSRequest):
    """Generate speech from text, returns WAV file"""
    global tts_model, current_speaker
    
    if tts_model is None:
        raise HTTPException(status_code=503, detail="TTS model not loaded")
    
    if not request.text or not request.text.strip():
        raise HTTPException(status_code=400, detail="Text cannot be empty")
    
    speaker = request.speaker or current_speaker
    if speaker not in SPEAKERS:
        raise HTTPException(status_code=400, detail=f"Unknown speaker: {speaker}")
    
    try:
        logger.info(f"Generating TTS: speaker={speaker}, text='{request.text[:50]}...'")
        
        # Generate audio
        wavs, sr = tts_model.generate_custom_voice(
            text=request.text,
            language=request.language,
            speaker=speaker,
            instruct=request.instruct or ""
        )
        
        # Convert to bytes
        audio_data = wavs[0]
        
        # Apply speed adjustment if needed
        if request.speed != 1.0 and request.speed > 0:
            # Simple resampling for speed change
            indices = np.linspace(0, len(audio_data) - 1, int(len(audio_data) / request.speed))
            audio_data = np.interp(indices, np.arange(len(audio_data)), audio_data)
        
        # Write to memory buffer
        buffer = io.BytesIO()
        sf.write(buffer, audio_data, sr, format='WAV')
        buffer.seek(0)
        
        return StreamingResponse(
            buffer,
            media_type="audio/wav",
            headers={
                "X-Speaker": speaker,
                "X-Sample-Rate": str(sr),
                "Content-Disposition": "attachment; filename=speech.wav"
            }
        )
        
    except Exception as e:
        logger.error(f"TTS generation failed: {e}")
        raise HTTPException(status_code=500, detail=f"TTS generation failed: {str(e)}")

@app.post("/tts/stream")
async def text_to_speech_stream(request: TTSRequest):
    """Stream audio chunks for real-time playback"""
    global tts_model
    
    if tts_model is None:
        raise HTTPException(status_code=503, detail="TTS model not loaded")
    
    speaker = request.speaker or current_speaker
    
    async def generate_audio_stream():
        """Generate audio in chunks for streaming"""
        try:
            # For now, generate full audio and chunk it
            # In future, can use actual streaming generation
            wavs, sr = tts_model.generate_custom_voice(
                text=request.text,
                language=request.language,
                speaker=speaker,
                instruct=request.instruct or ""
            )
            
            audio_data = wavs[0]
            chunk_size = sr * 2  # 2 seconds per chunk
            
            # Send header with sample rate
            yield f"SR:{sr}\n".encode()
            
            # Stream chunks
            for i in range(0, len(audio_data), chunk_size):
                chunk = audio_data[i:i + chunk_size]
                # Convert to int16 bytes
                chunk_int16 = (chunk * 32767).astype(np.int16)
                yield chunk_int16.tobytes()
                
        except Exception as e:
            logger.error(f"Streaming error: {e}")
            yield b"ERROR"
    
    return StreamingResponse(
        generate_audio_stream(),
        media_type="audio/pcm-stream"
    )

@app.post("/set_voice")
async def set_default_voice(speaker: str):
    """Set the default voice for Maggie"""
    global current_speaker
    
    if speaker not in SPEAKERS:
        raise HTTPException(status_code=400, detail=f"Unknown speaker: {speaker}")
    
    current_speaker = speaker
    logger.info(f"Default voice changed to: {speaker}")
    return {"success": True, "speaker": speaker, "description": SPEAKERS[speaker]}

@app.get("/speak")
async def speak_get(text: str, speaker: Optional[str] = None):
    """Simple GET endpoint for quick speech generation"""
    request = TTSRequest(text=text, speaker=speaker)
    return await text_to_speech(request)

if __name__ == "__main__":
    import uvicorn
    
    host = os.getenv("TTS_HOST", "0.0.0.0")
    port = int(os.getenv("TTS_PORT", "7860"))
    
    logger.info(f"Starting Maggie TTS Service on {host}:{port}")
    uvicorn.run(app, host=host, port=port)
