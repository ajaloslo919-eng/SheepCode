"""SheepGPT's local emotional voice. JSON lines in/out; model logs go to stderr.

The public Qwen wrapper does not forward stopping criteria, so cancellation is
attached to its underlying talker. A cancelled phrase never becomes audible.
"""
from __future__ import annotations

import contextlib
import json
import os
import queue
import sys
import threading
import time
import traceback
from pathlib import Path
from types import SimpleNamespace

ROOT = Path("E:/SheepGPTAI")
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ.setdefault("HF_HOME", str(ROOT / "models" / "huggingface"))
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
protocol = sys.stdout


def emit(payload: dict) -> None:
    protocol.write(json.dumps(payload, ensure_ascii=False) + "\n")
    protocol.flush()


class SynthesisInterrupted(Exception):
    pass


class Requests:
    """Keep stdin responsive while CUDA is synthesizing a sentence."""

    def __init__(self) -> None:
        self.queue: queue.Queue = queue.Queue()
        self.lock = threading.Lock()
        self.cancelled: set[str] = set()
        self.active_id = ""
        self.stop = threading.Event()
        threading.Thread(target=self.read, name="tts-stdin", daemon=True).start()

    def read(self) -> None:
        try:
            for line in sys.stdin:
                try:
                    request = json.loads(line)
                    if request.get("shutdown"):
                        self.stop.set()
                        self.queue.put(None)
                        return
                    if request.get("cancel"):
                        request_id = str(request.get("id", ""))
                        with self.lock:
                            if len(self.cancelled) > 64:
                                self.cancelled.clear()
                            self.cancelled.add(request_id)
                        continue
                    self.queue.put(request)
                except Exception as exc:
                    emit({"ok": False, "error": str(exc)})
        finally:
            self.stop.set()
            self.queue.put(None)

    def is_cancelled(self) -> bool:
        with self.lock:
            return self.stop.is_set() or self.active_id in self.cancelled


def main() -> int:
    configuration = Path(os.environ.get("SHEEPGPT_TTS_CONFIG", str(ROOT / "tts-config.json")))
    settings = json.loads(configuration.read_text(encoding="utf-8-sig"))
    model_path = Path(os.environ["SHEEPGPT_TTS_MODEL"])
    styles = json.loads(Path(__file__).with_name("emotion-styles.json").read_text(encoding="utf-8-sig"))
    if settings.get("model") != "Qwen3-TTS-12Hz-1.7B-CustomVoice":
        raise ValueError("La voz emocional instalada requiere Qwen3-TTS 1.7B CustomVoice.")

    # Libraries sometimes print optional FlashAttention warnings to stdout.
    # Keep stdout exclusively for SheepGPT's worker protocol.
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import soundfile as sf
        import torch
        from qwen_tts import Qwen3TTSModel
        from transformers import StoppingCriteria, StoppingCriteriaList

        if not torch.cuda.is_available():
            raise RuntimeError("CUDA no está disponible para la voz local Qwen3-TTS.")
        torch.set_num_threads(4)
        device = str(settings.get("device", "cuda:0"))
        release_gpu = bool(settings.get("releaseGpuWhenIdle", True))
        keep_warm_seconds = max(0, min(60, int(settings.get('gpuKeepWarmSeconds', 20))))
        model = Qwen3TTSModel.from_pretrained(
            str(model_path),
            device_map="cpu" if release_gpu else device,
            dtype=torch.float16,
            attn_implementation=settings.get("attention", "sdpa"),
            local_files_only=True,
            low_cpu_mem_usage=True,
        )
    if model.model.tts_model_type != "custom_voice" or model.model.tts_model_size == "0b6":
        raise RuntimeError("El modelo cargado no admite la voz emocional por instrucciones.")
    speaker = str(settings.get("speaker", "Ono_Anna"))
    language = str(settings.get("language", "Spanish"))
    if speaker.lower() not in model.get_supported_speakers():
        raise ValueError(f"Voz desconocida: {speaker}")
    if language.lower() not in model.get_supported_languages():
        raise ValueError(f"Idioma desconocido: {language}")
    if not 8 <= int(settings.get('firstAudioFrames', 12)) <= 48 or not 8 <= int(settings.get('audioChunkFrames', 16)) <= 48:
        raise ValueError('El tamaño de los fragmentos de voz debe estar entre 8 y 48 fotogramas.')

    voice_on_gpu = not release_gpu

    def move_voice(target: str) -> None:
        """The speech codec is a wrapper, so move its model explicitly too."""
        destination = torch.device(target)
        nonlocal voice_on_gpu
        model.model.to(destination)
        model.model.speech_tokenizer.model.to(destination)
        model.device = destination
        model.model.speech_tokenizer.device = destination
        voice_on_gpu = destination.type == 'cuda'

    requests = Requests()
    deadline = 0.0

    class InterruptibleSpeech(StoppingCriteria):
        def __call__(self, input_ids, scores, **kwargs):
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if time.monotonic() > deadline:
                raise TimeoutError("Qwen TTS superó el tiempo máximo de generación de la frase.")
            return torch.zeros(input_ids.shape[0], dtype=torch.bool, device=input_ids.device)

    original_generate = model.model.talker.generate
    original_predict = model.model.talker.code_predictor.generate
    predictor_graph = None

    class FastCodePredictor:
        """Replay the same 15 greedy codebooks with fixed CUDA shapes.

        Only the small acoustic predictor is captured. Text, speaker, emotion,
        the talker and codec still use the installed Qwen model normally.
        Discard this graph BEFORE moving weights: its addresses must stay valid.
        """
        def __init__(self, inputs):
            self.predictor = model.model.talker.code_predictor
            self.inputs = torch.empty_like(inputs)
            self.inputs.copy_(inputs)
            self.positions = [torch.arange(2, device=inputs.device)] + [torch.tensor([i + 1], device=inputs.device) for i in range(1, 15)]
            first = torch.full((1, 1, 2, 2), float('-inf'), dtype=inputs.dtype, device=inputs.device).triu(1)
            self.masks = [first] + [torch.zeros((1, 1, 1, i + 2), dtype=inputs.dtype, device=inputs.device) for i in range(1, 15)]
            warmup = torch.cuda.Stream()
            warmup.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(warmup):
                self.run()
                self.run()
            torch.cuda.current_stream().wait_stream(warmup)
            self.graph = torch.cuda.CUDAGraph()
            with torch.cuda.graph(self.graph):
                self.output = self.run()

        def run(self):
            cache, tokens = None, []
            for step in range(15):
                result = self.predictor(
                    inputs_embeds=self.inputs if step == 0 else None,
                    input_ids=tokens[-1] if step else None,
                    past_key_values=cache, use_cache=True, generation_steps=step,
                    cache_position=self.positions[step], position_ids=self.positions[step].unsqueeze(0),
                    attention_mask={'full_attention': self.masks[step]},
                    output_hidden_states=False, return_dict=True,
                )
                cache = result.past_key_values
                tokens.append(result.logits[:, -1].argmax(-1, keepdim=True))
            return torch.cat(tokens, dim=1)

        def predict(self, inputs):
            self.inputs.copy_(inputs)
            self.graph.replay()
            return SimpleNamespace(sequences=self.output.clone())

    def predict_audio_codes(*args, **kwargs):
        nonlocal predictor_graph
        inputs = kwargs.get('inputs_embeds')
        if settings.get('cudaGraphPredictor', True) and not kwargs.get('do_sample') and inputs is not None and inputs.shape[0] == 1:
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if predictor_graph is None:
                predictor_graph = FastCodePredictor(inputs)
                print('[SheepGPT TTS] CUDA graph: acoustic codebooks ready.', file=sys.stderr)
            return predictor_graph.predict(inputs)
        # The official talker only consumes predictor_result.sequences.
        # Avoid allocating hidden-state histories for all 15 sub-codebooks.
        kwargs['output_hidden_states'] = False
        return original_predict(*args, **kwargs)

    model.model.talker.code_predictor.generate = predict_audio_codes

    def generate_with_cancellation(*args, **kwargs):
        criteria = list(kwargs.pop("stopping_criteria", []) or [])
        kwargs["stopping_criteria"] = StoppingCriteriaList(criteria + [InterruptibleSpeech()])
        return original_generate(*args, **kwargs)

    model.model.talker.generate = generate_with_cancellation

    # CustomVoice's public wrapper returns a complete WAV. Collect the actual
    # 16-codebook frames from its talker to decode a causal prefix sooner.
    # This uses the installed model and codec; no substitute voice or download.
    streaming = None

    class AudioStream:
        def __init__(self, output: Path, request_id: str):
            self.output, self.request_id = output, request_id
            self.codes, self.paths = [], []
            self.emitted_samples = 0
            self.first_seconds = None
            self.started = time.monotonic()
            self.first_frames = int(settings.get('firstAudioFrames', 20))
            self.chunk_frames = int(settings.get('audioChunkFrames', 16))
            self.next_frames = self.first_frames

        def frame(self, code):
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if int(code[0, 0]) == model.model.config.talker_config.codec_eos_token_id:
                return
            self.codes.append(code.detach())
            if len(self.codes) >= self.next_frames:
                codes = torch.stack(self.codes, dim=1)[0]
                wavs, sample_rate = model.model.speech_tokenizer.decode([{'audio_codes': codes}])
                self.publish(np.asarray(wavs[0], dtype=np.float32), sample_rate)
                self.next_frames += self.chunk_frames

        def publish(self, audio, sample_rate):
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if audio.size <= self.emitted_samples:
                return
            chunk = audio[self.emitted_samples:]
            if not np.isfinite(chunk).all():
                raise RuntimeError('La voz incremental contiene muestras no válidas.')
            number = len(self.paths)
            path = self.output.with_name(self.output.stem + f'-chunk-{number:03d}.wav')
            sf.write(path, chunk, sample_rate, subtype='PCM_16')
            self.paths.append(path)
            elapsed = round(time.monotonic() - self.started, 3)
            self.first_seconds = self.first_seconds or elapsed
            emit({'chunk': True, 'id': self.request_id, 'index': number, 'output': str(path),
                  'sampleRate': sample_rate, 'offsetSamples': self.emitted_samples,
                  'audioSeconds': round(chunk.size / sample_rate, 3), 'elapsedSeconds': elapsed})
            self.emitted_samples = audio.size

    def collect_audio_frames(module, args, result):
        if streaming is not None and result.hidden_states[-1] is not None:
            streaming.frame(result.hidden_states[-1])

    model.model.talker.register_forward_hook(collect_audio_frames)
    emit({
        "ready": True, "engine": "qwen3-tts", "model": settings["model"],
        "speaker": speaker, "language": language, "device": torch.cuda.get_device_name(0),
        "dtype": "float16", "attention": settings.get("attention", "sdpa"),
        "offline": True, "emotionStyles": len(styles) - 1,
        "streaming": bool(settings.get('streamAudio', True)),
        "idleResidency": "gpu-temporarily-then-system-ram" if release_gpu and keep_warm_seconds else "system-ram" if release_gpu else "gpu",
        "gpuKeepWarmSeconds": keep_warm_seconds,
    })

    while True:
        try:
            request = requests.queue.get(timeout=keep_warm_seconds if release_gpu and voice_on_gpu and keep_warm_seconds else None)
        except queue.Empty:
            predictor_graph = None
            move_voice('cpu')
            torch.cuda.empty_cache()
            continue
        if request is None:
            break
        output = None
        request_id = str(request.get("id", "legacy"))
        requests.active_id = request_id
        try:
            text = str(request.get("text", "")).strip()
            if not text or len(text) > 320:
                raise ValueError("La frase debe tener entre 1 y 320 caracteres.")
            output = Path(request["output"])
            output.parent.mkdir(parents=True, exist_ok=True)
            emotion = str(request.get("emotion", "neutral"))
            if emotion not in styles:
                emotion = "neutral"
            intensity = max(0.0, min(1.0, float(request.get("intensity", 0.5))))
            subtlety = "Keep this emotion subtle." if intensity < 0.4 else "Make this emotion audible, without exaggeration."
            instruction = " ".join((settings["baseInstruction"], styles[emotion], subtlety))
            deadline = time.monotonic() + int(settings.get("maximumGenerationSeconds", 180))
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if not voice_on_gpu:
                move_voice(device)
            started = time.monotonic()
            streaming = AudioStream(output, request_id) if request.get('stream') and settings.get('streamAudio', True) else None
            with contextlib.redirect_stdout(sys.stderr), torch.inference_mode():
                wavs, sample_rate = model.generate_custom_voice(
                    text=text, language=language, speaker=speaker, instruct=instruction,
                    non_streaming_mode=True, max_new_tokens=1024,
                    temperature=0.75, top_p=0.9, repetition_penalty=1.08,
                    subtalker_dosample=False,
                )
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            audio = np.asarray(wavs[0], dtype=np.float32)
            if audio.size < sample_rate // 5 or not np.isfinite(audio).all():
                raise RuntimeError("Qwen TTS no generó una forma de onda válida.")
            if float(np.sqrt(np.mean(audio * audio))) < 0.0001:
                raise RuntimeError("La voz generada está vacía o es silencio.")
            sf.write(output, audio, sample_rate, subtype="PCM_16")
            if streaming is not None:
                streaming.publish(audio, sample_rate)
            emit({"ok": True, "id": request_id, "output": str(output), "emotion": emotion,
                  "speaker": speaker, "sampleRate": sample_rate,
                  "audioSeconds": round(audio.size / sample_rate, 3),
                  "synthesisSeconds": round(time.monotonic() - started, 3),
                  "firstAudioSeconds": streaming.first_seconds if streaming else round(time.monotonic() - started, 3),
                  "chunks": len(streaming.paths) if streaming else 0})
        except SynthesisInterrupted:
            emit({"ok": False, "id": request_id, "cancelled": True})
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            emit({"ok": False, "id": request_id, "error": str(exc)})
        finally:
            if streaming is not None and requests.is_cancelled():
                for path in streaming.paths:
                    path.unlink(missing_ok=True)
            streaming = None
            if release_gpu and not keep_warm_seconds:
                predictor_graph = None
            with requests.lock:
                requests.cancelled.discard(request_id)
            requests.active_id = ""
            # Keep the selected voice warm in RAM while chat/vision or training
            # use the GPU. No inference is performed on the CPU in this mode.
            if release_gpu and not keep_warm_seconds:
                move_voice("cpu")
                torch.cuda.empty_cache()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit({"ready": False, "error": str(error)})
        raise SystemExit(1)
