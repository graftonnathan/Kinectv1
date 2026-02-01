# Qwen3-TTS Voice Generator for Maggie
# Creates reusable character voices using VoiceDesign -> VoiceClone workflow

import torch
import soundfile as sf
import os
import json
import hashlib
from pathlib import Path
from typing import Optional, Dict, List
import logging

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

class MaggieVoiceGenerator:
    """
    Creates and manages custom character voices for Maggie using Qwen3-TTS.
    
    Workflow:
    1. VoiceDesign: Create a reference clip with voice description
    2. VoiceClone: Convert reference to reusable voice_clone_prompt
    3. Generate: Use prompt to generate consistent speech
    """
    
    def __init__(self, voices_dir: str = "tts_service/voices"):
        self.voices_dir = Path(voices_dir)
        self.voices_dir.mkdir(parents=True, exist_ok=True)
        
        self.device = "cuda:0" if torch.cuda.is_available() else "cpu"
        self.dtype = torch.float32  # RTX 2080 stability
        
        self.voice_design_model = None
        self.voice_clone_model = None
        
        # Cache of loaded voice prompts
        self.voice_prompts: Dict[str, dict] = {}
        
        logger.info(f"MaggieVoiceGenerator initialized (device: {self.device})")
    
    def _load_voice_design_model(self):
        """Lazy load VoiceDesign model"""
        if self.voice_design_model is None:
            from qwen_tts import Qwen3TTSModel
            logger.info("Loading VoiceDesign model...")
            self.voice_design_model = Qwen3TTSModel.from_pretrained(
                "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign",
                device_map=self.device,
                dtype=self.dtype,
                attn_implementation="eager"
            )
            logger.info("✅ VoiceDesign model loaded")
        return self.voice_design_model
    
    def _load_voice_clone_model(self):
        """Lazy load VoiceClone (Base) model"""
        if self.voice_clone_model is None:
            from qwen_tts import Qwen3TTSModel
            logger.info("Loading VoiceClone (Base) model...")
            self.voice_clone_model = Qwen3TTSModel.from_pretrained(
                "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
                device_map=self.device,
                dtype=self.dtype,
                attn_implementation="eager"
            )
            logger.info("✅ VoiceClone model loaded")
        return self.voice_clone_model
    
    def create_character_voice(
        self,
        name: str,
        description: str,
        ref_text: str = "Hello, I'm Maggie. How can I help you today?",
        language: str = "English"
    ) -> dict:
        """
        Create a new character voice from description.
        
        Args:
            name: Unique name for this voice (e.g., "Maggie_Warm", "Maggie_Professional")
            description: Voice description (e.g., "Warm, gentle female voice, mid-30s...")
            ref_text: Reference text to generate (should be representative)
            language: Language code
        
        Returns:
            dict with voice info and file paths
        """
        logger.info(f"Creating character voice: '{name}'")
        logger.info(f"Description: {description[:60]}...")
        
        # Step 1: Generate reference audio with VoiceDesign
        vd_model = self._load_voice_design_model()
        
        voice_dir = self.voices_dir / name
        voice_dir.mkdir(exist_ok=True)
        
        ref_audio_path = voice_dir / "reference.wav"
        
        logger.info("Step 1/3: Generating reference audio with VoiceDesign...")
        ref_wavs, sr = vd_model.generate_voice_design(
            text=ref_text,
            language=language,
            instruct=description
        )
        sf.write(str(ref_audio_path), ref_wavs[0], sr)
        logger.info(f"✅ Reference audio saved: {ref_audio_path}")
        
        # Step 2: Create reusable voice clone prompt
        base_model = self._load_voice_clone_model()
        
        logger.info("Step 2/3: Creating reusable voice clone prompt...")
        voice_clone_prompt = base_model.create_voice_clone_prompt(
            ref_audio=str(ref_audio_path),
            ref_text=ref_text,
            x_vector_only_mode=False
        )
        
        # Save the prompt
        prompt_path = voice_dir / "voice_prompt.pt"
        torch.save(voice_clone_prompt, str(prompt_path))
        logger.info(f"✅ Voice prompt saved: {prompt_path}")
        
        # Save metadata
        metadata = {
            "name": name,
            "description": description,
            "ref_text": ref_text,
            "language": language,
            "ref_audio": str(ref_audio_path),
            "voice_prompt": str(prompt_path),
            "sample_rate": sr,
            "created": str(torch.cuda.Event(enable_timing=False)) if torch.cuda.is_available() else "cpu"
        }
        
        metadata_path = voice_dir / "metadata.json"
        with open(metadata_path, 'w') as f:
            json.dump(metadata, f, indent=2)
        logger.info(f"✅ Metadata saved: {metadata_path}")
        
        # Cache the prompt
        self.voice_prompts[name] = {
            "prompt": voice_clone_prompt,
            "metadata": metadata
        }
        
        logger.info(f"🎉 Character voice '{name}' created successfully!")
        return metadata
    
    def load_voice(self, name: str) -> dict:
        """Load a previously created voice"""
        if name in self.voice_prompts:
            return self.voice_prompts[name]["metadata"]
        
        voice_dir = self.voices_dir / name
        metadata_path = voice_dir / "metadata.json"
        prompt_path = voice_dir / "voice_prompt.pt"
        
        if not metadata_path.exists() or not prompt_path.exists():
            raise FileNotFoundError(f"Voice '{name}' not found in {voice_dir}")
        
        with open(metadata_path, 'r') as f:
            metadata = json.load(f)
        
        voice_clone_prompt = torch.load(str(prompt_path), map_location=self.device)
        
        self.voice_prompts[name] = {
            "prompt": voice_clone_prompt,
            "metadata": metadata
        }
        
        logger.info(f"✅ Loaded voice: '{name}'")
        return metadata
    
    def generate_speech(
        self,
        voice_name: str,
        text: str,
        language: str = "English",
        output_path: Optional[str] = None
    ) -> str:
        """
        Generate speech using a character voice.
        
        Args:
            voice_name: Name of the voice to use
            text: Text to speak
            language: Language code
            output_path: Where to save (optional, returns path)
        
        Returns:
            Path to generated audio file
        """
        # Load voice if not cached
        if voice_name not in self.voice_prompts:
            self.load_voice(voice_name)
        
        voice_data = self.voice_prompts[voice_name]
        voice_clone_prompt = voice_data["prompt"]
        
        # Generate with VoiceClone model
        base_model = self._load_voice_clone_model()
        
        logger.info(f"Generating speech with voice '{voice_name}': {text[:50]}...")
        
        wavs, sr = base_model.generate_voice_clone(
            text=text,
            language=language,
            voice_clone_prompt=voice_clone_prompt
        )
        
        # Determine output path
        if output_path is None:
            voice_dir = self.voices_dir / voice_name / "generated"
            voice_dir.mkdir(exist_ok=True)
            # Hash text for unique filename
            text_hash = hashlib.md5(text.encode()).hexdigest()[:8]
            output_path = voice_dir / f"{text_hash}.wav"
        
        sf.write(str(output_path), wavs[0], sr)
        logger.info(f"✅ Generated: {output_path}")
        
        return str(output_path)
    
    def list_voices(self) -> List[dict]:
        """List all available character voices"""
        voices = []
        
        for voice_dir in self.voices_dir.iterdir():
            if voice_dir.is_dir():
                metadata_path = voice_dir / "metadata.json"
                if metadata_path.exists():
                    with open(metadata_path, 'r') as f:
                        metadata = json.load(f)
                    voices.append({
                        "name": metadata["name"],
                        "description": metadata["description"],
                        "language": metadata["language"]
                    })
        
        return voices
    
    def delete_voice(self, name: str):
        """Delete a character voice"""
        import shutil
        voice_dir = self.voices_dir / name
        
        if voice_dir.exists():
            shutil.rmtree(voice_dir)
            if name in self.voice_prompts:
                del self.voice_prompts[name]
            logger.info(f"🗑️ Deleted voice: '{name}'")
            return True
        return False


# Predefined character voices for Maggie
MAGGIE_CHARACTER_VOICES = {
    "Maggie_Warm": {
        "description": "Warm, gentle female voice, mid-30s, soft-spoken and caring. American accent with a hint of thoughtfulness. Speak like a helpful friend who genuinely listens.",
        "ref_text": "Hey there. I'm here to help however I can. What would you like to work on today?"
    },
    "Maggie_Professional": {
        "description": "Professional female voice, clear articulation, confident but approachable. American accent. Like a skilled executive assistant who gets things done.",
        "ref_text": "Good day. I've reviewed your schedule and identified three priority items that require attention."
    },
    "Maggie_Playful": {
        "description": "Young, energetic female voice with a playful tone. Slightly faster pace, warm enthusiasm. Like a creative collaborator who brings fresh ideas.",
        "ref_text": "Ooh, this is exciting! Let's figure this out together. I have some ideas!"
    },
    "Maggie_Calm": {
        "description": "Calm, soothing female voice, slow and measured pace. Meditative quality, very gentle. Like a mindfulness guide or gentle narrator.",
        "ref_text": "Take a breath with me. There's no rush. We have plenty of time to work through this."
    },
    "Maggie_Tech": {
        "description": "Clear, precise female voice with technical confidence. Slightly faster, efficient delivery. Like a brilliant engineer explaining complex systems.",
        "ref_text": "Analyzing the system architecture. I've identified the bottleneck in the pipeline and have three optimization strategies."
    }
}


def create_default_maggie_voices():
    """Create the default set of Maggie character voices"""
    generator = MaggieVoiceGenerator()
    
    created = []
    for name, config in MAGGIE_CHARACTER_VOICES.items():
        try:
            voice_dir = generator.voices_dir / name
            if voice_dir.exists():
                logger.info(f"Voice '{name}' already exists, skipping...")
                continue
            
            logger.info(f"\n{'='*60}")
            metadata = generator.create_character_voice(
                name=name,
                description=config["description"],
                ref_text=config["ref_text"]
            )
            created.append(metadata)
            logger.info(f"{'='*60}\n")
            
        except Exception as e:
            logger.error(f"Failed to create voice '{name}': {e}")
    
    return created


if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description="Maggie Voice Generator")
    parser.add_argument("--create-defaults", action="store_true", help="Create default Maggie voices")
    parser.add_argument("--list", action="store_true", help="List available voices")
    parser.add_argument("--create", help="Create new voice (provide name)")
    parser.add_argument("--description", help="Voice description for new voice")
    parser.add_argument("--ref-text", default="Hello, I'm Maggie. How can I help you?", help="Reference text")
    parser.add_argument("--generate", help="Generate speech with voice (provide voice name)")
    parser.add_argument("--text", help="Text to generate")
    
    args = parser.parse_args()
    
    if args.create_defaults:
        create_default_maggie_voices()
    elif args.list:
        gen = MaggieVoiceGenerator()
        voices = gen.list_voices()
        print(f"\nAvailable voices ({len(voices)}):")
        for v in voices:
            print(f"  • {v['name']}: {v['description'][:60]}...")
    elif args.create and args.description:
        gen = MaggieVoiceGenerator()
        metadata = gen.create_character_voice(
            name=args.create,
            description=args.description,
            ref_text=args.ref_text
        )
        print(f"\n✅ Created voice: {metadata['name']}")
        print(f"   Reference: {metadata['ref_audio']}")
        print(f"   Prompt: {metadata['voice_prompt']}")
    elif args.generate and args.text:
        gen = MaggieVoiceGenerator()
        output = gen.generate_speech(args.generate, args.text)
        print(f"\n✅ Generated: {output}")
    else:
        parser.print_help()
