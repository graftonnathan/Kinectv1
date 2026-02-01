#!/usr/bin/env python3
"""
Maggie Latency Optimizer Tool

Manage latency optimization settings for Maggie AI.
Usage:
    python3 latency_optimizer.py status      - Show current optimization status
    python3 latency_optimizer.py low         - Enable low-latency mode
    python3 latency_optimizer.py normal      - Restore normal mode
    python3 latency_optimizer.py benchmark   - Run latency benchmark
"""

import json
import sys
import os
import time
import requests
from pathlib import Path
from dataclasses import dataclass
from typing import Optional, Dict, Any


@dataclass
class LatencySettings:
    low_latency_mode: bool
    vector_memory_enabled: bool
    embedding_cache_enabled: bool
    hot_context_token_limit: int
    memory_context_budget: int
    chunk_size_tokens: int
    vector_search_top_k: int
    conversation_max_tokens: int


class LatencyOptimizer:
    def __init__(self, maggie_root: Optional[Path] = None):
        self.maggie_root = maggie_root or Path.home() / "workspace" / "maggie"
        self.settings_path = self.maggie_root / "Settings" / "default.json"
        self.user_settings_path = Path.home() / ".config" / "maggie" / "user.json"
        
    def load_settings(self) -> Dict[str, Any]:
        """Load current Maggie settings."""
        if self.user_settings_path.exists():
            try:
                with open(self.user_settings_path, 'r') as f:
                    return json.load(f)
            except Exception as e:
                print(f"⚠️  Could not load user settings: {e}")
        
        try:
            with open(self.settings_path, 'r') as f:
                return json.load(f)
        except Exception as e:
            print(f"❌ Could not load settings: {e}")
            sys.exit(1)
    
    def save_user_settings(self, settings: Dict[str, Any]):
        """Save settings to user overrides."""
        # Ensure directory exists
        self.user_settings_path.parent.mkdir(parents=True, exist_ok=True)
        
        # Create overrides-only JSON (just the changed values)
        overrides = {"schemaVersion": 2}
        
        if "ollama" in settings:
            overrides["ollama"] = settings["ollama"]
        
        try:
            with open(self.user_settings_path, 'w') as f:
                json.dump(overrides, f, indent=2)
            print(f"✓ Settings saved to {self.user_settings_path}")
        except Exception as e:
            print(f"❌ Could not save settings: {e}")
            sys.exit(1)
    
    def get_current_settings(self) -> LatencySettings:
        """Extract latency-relevant settings."""
        settings = self.load_settings()
        ollama = settings.get("ollama", {})
        
        return LatencySettings(
            low_latency_mode=ollama.get("lowLatencyMode", False),
            vector_memory_enabled=ollama.get("vectorMemoryEnabled", True),
            embedding_cache_enabled=ollama.get("embeddingCacheEnabled", True),
            hot_context_token_limit=ollama.get("hotContextTokenLimit", 3000),
            memory_context_budget=ollama.get("memoryContextBudget", 800),
            chunk_size_tokens=ollama.get("chunkSizeTokens", 400),
            vector_search_top_k=ollama.get("vectorSearchTopK", 3),
            conversation_max_tokens=ollama.get("conversationMaxTokens", 8000),
        )
    
    def show_status(self):
        """Display current optimization status."""
        settings = self.get_current_settings()
        
        print("╔══════════════════════════════════════════════════════════╗")
        print("║         🚀 Maggie Latency Optimization Status            ║")
        print("╚══════════════════════════════════════════════════════════╝")
        print()
        
        # Low latency mode
        if settings.low_latency_mode:
            print("⚡ Mode:              LOW LATENCY (fastest)")
        else:
            print("🔄 Mode:              NORMAL (balanced)")
        
        print()
        print("Settings:")
        print(f"  • Vector Memory:    {'✓ Enabled' if settings.vector_memory_enabled else '✗ Disabled'}")
        print(f"  • Embedding Cache:  {'✓ Enabled' if settings.embedding_cache_enabled else '✗ Disabled'}")
        print(f"  • Hot Context:      {settings.hot_context_token_limit} tokens")
        print(f"  • Memory Budget:    {settings.memory_context_budget} tokens")
        print(f"  • Chunk Size:       {settings.chunk_size_tokens} tokens")
        print(f"  • Vector Search:    Top-{settings.vector_search_top_k}")
        print(f"  • Max Tokens:       {settings.conversation_max_tokens}")
        print()
        
        # Estimate latency
        if settings.low_latency_mode:
            print("Expected Latency:     2000-4000ms (fastest)")
        elif not settings.vector_memory_enabled:
            print("Expected Latency:     1500-3000ms (no memory)")
        else:
            print("Expected Latency:     2500-5000ms (with memory)")
        
        print()
        
        # Check cache stats if Maggie is running
        self._show_cache_stats()
    
    def _show_cache_stats(self):
        """Try to get cache stats from running Maggie instance."""
        try:
            # This would require an API endpoint on Maggie
            # For now, just show that cache is enabled
            settings = self.get_current_settings()
            if settings.embedding_cache_enabled:
                print("📊 Embedding Cache:   Enabled (100 entries, 10min TTL)")
            else:
                print("📊 Embedding Cache:   Disabled")
        except:
            pass
    
    def set_low_latency_mode(self, enabled: bool = True):
        """Enable or disable low-latency mode."""
        settings = self.load_settings()
        
        if "ollama" not in settings:
            settings["ollama"] = {}
        
        settings["ollama"]["lowLatencyMode"] = enabled
        
        if enabled:
            # Optimize for speed
            settings["ollama"]["hotContextTokenLimit"] = 2000
            settings["ollama"]["memoryContextBudget"] = 400
            settings["ollama"]["chunkSizeTokens"] = 500
            settings["ollama"]["vectorSearchTopK"] = 2
            settings["ollama"]["conversationMaxTokens"] = 4000
            print("⚡ Enabling low-latency mode...")
        else:
            # Restore balanced settings
            settings["ollama"]["hotContextTokenLimit"] = 3000
            settings["ollama"]["memoryContextBudget"] = 800
            settings["ollama"]["chunkSizeTokens"] = 400
            settings["ollama"]["vectorSearchTopK"] = 3
            settings["ollama"]["conversationMaxTokens"] = 8000
            print("🔄 Restoring normal mode...")
        
        self.save_user_settings(settings)
        
        if enabled:
            print()
            print("✅ Low-latency mode enabled!")
            print("   Expected latency: 2000-4000ms")
            print("   Trade-offs: Lower quality memory summaries")
        else:
            print()
            print("✅ Normal mode restored!")
            print("   Expected latency: 2500-5000ms")
            print("   Benefits: Better memory quality")
    
    def benchmark(self):
        """Run a latency benchmark against Maggie."""
        print("╔══════════════════════════════════════════════════════════╗")
        print("║              🧪 Maggie Latency Benchmark                 ║")
        print("╚══════════════════════════════════════════════════════════╝")
        print()
        
        # Check if Maggie is running
        try:
            response = requests.get("http://localhost:18790/api/status", timeout=2)
            if response.status_code != 200:
                print("❌ Maggie API not responding. Is Maggie running?")
                print("   Start Maggie with: ./start-maggie.sh")
                return
        except requests.exceptions.ConnectionError:
            print("❌ Cannot connect to Maggie at http://localhost:18790")
            print("   Is Maggie running? Start with: ./start-maggie.sh")
            return
        except Exception as e:
            print(f"❌ Error checking Maggie status: {e}")
            return
        
        print("✓ Maggie is running")
        print()
        
        # Test queries
        queries = [
            ("Simple query", "Hello, how are you?"),
            ("Memory query", "What did we discuss yesterday?"),
            ("Complex query", "Explain quantum computing in simple terms"),
        ]
        
        results = []
        
        for name, query in queries:
            print(f"Testing: {name}...")
            
            latencies = []
            for i in range(3):  # 3 iterations
                start = time.time()
                try:
                    response = requests.post(
                        "http://localhost:18790/api/chat",
                        json={"message": query},
                        timeout=60
                    )
                    elapsed = (time.time() - start) * 1000  # Convert to ms
                    latencies.append(elapsed)
                    print(f"  Run {i+1}: {elapsed:.0f}ms")
                except Exception as e:
                    print(f"  Run {i+1}: ERROR - {e}")
            
            if latencies:
                avg = sum(latencies) / len(latencies)
                min_lat = min(latencies)
                max_lat = max(latencies)
                results.append((name, avg, min_lat, max_lat))
            
            print()
        
        # Summary
        print("Results Summary:")
        print("-" * 50)
        for name, avg, min_lat, max_lat in results:
            print(f"{name:20s}  avg:{avg:6.0f}ms  (min:{min_lat:.0f} / max:{max_lat:.0f})")
        print("-" * 50)
        
        # Overall average
        if results:
            overall = sum(r[1] for r in results) / len(results)
            print(f"{'Overall Average':20s}  {overall:6.0f}ms")
        
        print()
        print("Note: First query may be slower due to model warm-up.")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    
    command = sys.argv[1].lower()
    optimizer = LatencyOptimizer()
    
    if command == "status":
        optimizer.show_status()
    elif command == "low":
        optimizer.set_low_latency_mode(True)
    elif command == "normal":
        optimizer.set_low_latency_mode(False)
    elif command == "benchmark":
        optimizer.benchmark()
    else:
        print(f"Unknown command: {command}")
        print(__doc__)
        sys.exit(1)


if __name__ == "__main__":
    main()
