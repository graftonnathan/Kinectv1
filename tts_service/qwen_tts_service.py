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
    voice_description: Optional[str] = None  # Free-form voice design description

# Default custom voice description (editable)
DEFAULT_VOICE_DESCRIPTION = "Speak in a cheery relaxing female voice"
current_voice_description = DEFAULT_VOICE_DESCRIPTION

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
        
        # Use VoiceDesign model for custom voice creation, fallback to CustomVoice
        model_name = os.getenv("QWEN_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-VoiceDesign")
        
        # Check CUDA availability
        cuda_available = torch.cuda.is_available()
        cuda_device_count = torch.cuda.device_count() if cuda_available else 0
        
        if cuda_available:
            device = os.getenv("QWEN_TTS_DEVICE", "cuda:0")
            # RTX 2080 needs float32 for stability
            # float16/bfloat16 can cause CUDA errors
            dtype = torch.float32
            logger.info(f"✅ CUDA is available! Found {cuda_device_count} GPU(s)")
            logger.info(f"   Using device: {device}")
            logger.info(f"   GPU: {torch.cuda.get_device_name(0)}")
            logger.info(f"   dtype: {dtype} (float32 for stability)")
        else:
            device = "cpu"
            dtype = torch.float32
            logger.warning("⚠️  CUDA not available, using CPU (will be slow)")
        
        logger.info(f"Loading Qwen3-TTS model: {model_name}")
        
        tts_model = Qwen3TTSModel.from_pretrained(
            model_name,
            device_map=device,
            dtype=dtype,
            attn_implementation="eager"  # Safer default
        )
        
        logger.info("✅ Qwen3-TTS model loaded successfully")
        logger.info(f"Available speakers: {list(SPEAKERS.keys())}")
        
    except Exception as e:
        logger.error(f"❌ Failed to load Qwen3-TTS model: {e}")
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
    
    # Check if using custom voice description
    voice_desc = request.voice_description or current_voice_description
    use_voice_design = voice_desc is not None and voice_desc.strip() != ""
    
    try:
        if use_voice_design:
            # Use voice design mode with free-form description
            logger.info(f"Generating TTS with voice design: '{voice_desc[:50]}...', text='{request.text[:50]}...'")
            
            # Use voice design mode - creates voice purely from description
            # This uses the model's voice design capability without a base speaker
            combined_instruct = f"{voice_desc}. {request.instruct or ''}".strip()
            logger.info(f"Voice design: description='{voice_desc[:40]}...'")
            
            try:
                # Try pure voice design first (no base speaker)
                wavs, sr = tts_model.generate_voice_design(
                    text=request.text,
                    instruct=combined_instruct,
                    language=request.language
                )
            except Exception as e:
                logger.warning(f"Pure voice design failed ({e}), falling back to base voice + description")
                # Fallback: use base voice with description as instruct
                base_speaker = request.speaker if request.speaker and request.speaker in SPEAKERS else "Serena"
                wavs, sr = tts_model.generate_custom_voice(
                    text=request.text,
                    language=request.language,
                    speaker=base_speaker,
                    instruct=combined_instruct
                )
        else:
            # Use predefined speaker
            speaker = request.speaker or current_speaker
            if speaker not in SPEAKERS:
                raise HTTPException(status_code=400, detail=f"Unknown speaker: {speaker}")
            
            logger.info(f"Generating TTS: speaker={speaker}, text='{request.text[:50]}...'")
            
            # Generate audio with predefined speaker
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
        
        # Determine speaker name for headers
        header_speaker = "Custom" if use_voice_design else (request.speaker or current_speaker)
        
        return StreamingResponse(
            buffer,
            media_type="audio/wav",
            headers={
                "X-Speaker": header_speaker,
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
    global current_speaker, current_voice_description
    
    if speaker not in SPEAKERS:
        raise HTTPException(status_code=400, detail=f"Unknown speaker: {speaker}")
    
    current_speaker = speaker
    current_voice_description = None  # Clear custom description when using preset
    logger.info(f"Default voice changed to: {speaker}")
    return {"success": True, "speaker": speaker, "description": SPEAKERS[speaker]}

@app.get("/voice_description")
async def get_voice_description():
    """Get the current custom voice description"""
    return {
        "current_description": current_voice_description,
        "default_description": DEFAULT_VOICE_DESCRIPTION,
        "using_custom": current_voice_description is not None
    }

@app.post("/voice_description")
async def set_voice_description(description: str):
    """Set a custom voice description for voice design mode"""
    global current_voice_description
    
    if not description or not description.strip():
        raise HTTPException(status_code=400, detail="Description cannot be empty")
    
    current_voice_description = description.strip()
    logger.info(f"Voice description changed to: {current_voice_description}")
    return {
        "success": True,
        "description": current_voice_description,
        "message": f"Voice description updated. Maggie will now speak with this voice style."
    }

@app.post("/reset_voice_description")
async def reset_voice_description():
    """Reset to default voice description"""
    global current_voice_description
    current_voice_description = DEFAULT_VOICE_DESCRIPTION
    logger.info(f"Voice description reset to default: {DEFAULT_VOICE_DESCRIPTION}")
    return {
        "success": True,
        "description": current_voice_description,
        "message": "Voice description reset to default."
    }

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
