"""SheepGPT's local emotional voice. JSON lines in/out; model logs go to stderr.

The public Qwen wrapper does not forward stopping criteria, so cancellation is
attached to its underlying talker. Cancellation stops any further audio and
keeps the warm worker's protocol aligned for the next request.
"""
from __future__ import annotations

import contextlib
import hashlib
import json
import os
import queue
import sys
import threading
import time
import traceback
from pathlib import Path

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


class IncompleteVoice(Exception):
    """The selected voice reached its acoustic limit without an end token."""


class VoicePrefixPrepared(Exception):
    """End a startup prefix preparation before creating any acoustic output."""


class Requests:
    """Keep stdin responsive while DirectML is synthesizing a sentence."""

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
        # DirectML needs tensor version counters even during forward inference.
        # Disable autograd without PyTorch's incompatible inference-tensor mode.
        # Apply before importing Qwen so its decorators use the same safe mode.
        torch.inference_mode = torch.no_grad
        from qwen_tts import Qwen3TTSModel
        from transformers import StoppingCriteria, StoppingCriteriaList

        from qwen_directml_runtime import select_rx580, install_compatibility, move_voice_inputs
        if settings.get("device") != "rx580-directml":
            raise RuntimeError("La voz emocional de SheepGPT está reservada para la RX 580.")
        if os.environ.get('SHEEPGPT_TTS_TARGET') == 'rtx2060s-directml' and not settings.get('dualGpuVoice', False):
            raise RuntimeError('El modo de voz con dos GPU está desactivado.')
        device, device_name = select_rx580()
        install_compatibility()
        torch.set_num_threads(4)

        release_gpu = bool(settings.get("releaseGpuWhenIdle", False))
        if release_gpu or any(settings.get(key) != "onnx-directml"
                              for key in ("codecBackend", "predictorBackend", "talkerBackend")):
            raise RuntimeError("La voz usa sus tres gráficos DirectML y permanece preparada en RX 580.")
        keep_warm_seconds = max(0, min(60, int(settings.get('gpuKeepWarmSeconds', 20))))
        model = Qwen3TTSModel.from_pretrained(
            str(model_path),
            device_map="cpu",
            dtype=torch.float16,
            attn_implementation="eager",
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

    move_voice_inputs(model, device)
    if settings.get("codecBackend") != "onnx-directml":
        raise RuntimeError("El códec Qwen de SheepGPT requiere su gráfico compatible con la RX 580.")
    from qwen_directml_graph import GraphVoiceCodec
    compiled_codec = GraphVoiceCodec(model_path)
    if settings.get("talkerBackend") != "onnx-directml":
        raise RuntimeError("El generador emocional requiere su backend DirectML en RX 580.")
    from qwen_directml_talker import DirectTalker
    model.model.talker.model = DirectTalker(model.model.talker.model, model_path, device)
    voice_on_gpu = True
    if os.environ.get("SHEEPGPT_TTS_DEBUG") == "1":
        decoder = model.model.speech_tokenizer.model.decoder
        def codec_statistics(label):
            def record(module, inputs, result):
                tensor = result.last_hidden_state if hasattr(result, "last_hidden_state") else result
                host = tensor.detach().float().cpu()
                print(f"[TTS/codec-debug] {label}: device={tensor.device} shape={list(tensor.shape)} range={host.min().item():.3g}/{host.max().item():.3g} rms={host.square().mean().sqrt().item():.3g}", file=sys.stderr)
            return record
        decoder.pre_conv.register_forward_hook(codec_statistics("pre_conv"))
        decoder.pre_transformer.register_forward_hook(codec_statistics("pre_transformer"))
        for i, blocks in enumerate(decoder.upsample):
            for j, block in enumerate(blocks):
                block.register_forward_hook(codec_statistics(f"upsample.{i}.{j}"))
        for i, block in enumerate(decoder.decoder):
            block.register_forward_hook(codec_statistics(f"decoder.{i}"))
        for i, block in enumerate(decoder.decoder[1].block):
            block.register_forward_hook(codec_statistics(f"decoder.1.block.{i}"))

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
    from qwen_directml_graph import GraphCodePredictor
    predictor_graph = GraphCodePredictor(model_path, device)

    def check_cancelled():
        if requests.is_cancelled():
            raise SynthesisInterrupted()
        if time.monotonic() > deadline:
            raise TimeoutError("Qwen TTS superó el tiempo máximo de generación de la frase.")

    model.model.talker.model.check_cancelled = check_cancelled
    compiled_codec.check_cancelled = check_cancelled
    from qwen_directml_forward import install_fused_forward
    install_fused_forward(model.model.talker, predictor_graph, check_cancelled)

    def generate_with_cancellation(*args, **kwargs):
        criteria = list(kwargs.pop("stopping_criteria", []) or [])
        kwargs["stopping_criteria"] = StoppingCriteriaList(criteria + [InterruptibleSpeech()])
        # Each phrase gets a fresh positional state, including after interruption.
        model.model.talker.rope_deltas = None
        # Prefix embeddings were computed on RX 580 above. The HF loop handles
        # only tokens, penalties and cancellation; each neural forward goes
        # through the selected DirectML graphs, avoiding GPU dispatch for these
        # small control tensors and fifteen separate embedding lookups.
        for key, value in list(kwargs.items()):
            if isinstance(value, torch.Tensor):
                kwargs[key] = value.detach().cpu()
        # HF otherwise creates an empty token history on self.device (the GPU
        # of the prompt embeddings), even when inputs_embeds is already on CPU.
        kwargs['input_ids'] = torch.empty((kwargs['inputs_embeds'].shape[0], 0), dtype=torch.int64)
        result = original_generate(*args, **kwargs)
        check_cancelled()
        if int(result.sequences[0, -1].detach().cpu()) != model.model.config.talker_config.codec_eos_token_id:
            raise IncompleteVoice("El generador de voz agotó el límite acústico sin terminar la frase; se detiene la salida y no se publica más audio.")
        return result

    model.model.talker.generate = generate_with_cancellation

    # CustomVoice's public wrapper returns a complete WAV. Collect the actual
    # 16-codebook frames from its talker to decode a causal prefix sooner.
    # This uses the installed model and codec; no substitute voice or download.
    streaming = None
    preparing_voice_prefix = False
    debug_frames = 0

    def decode_voice(encoded):
        if streaming is not None and len(encoded) == 1:
            return [streaming.finish(encoded[0]["audio_codes"])], compiled_codec.sample_rate
        return compiled_codec.decode(encoded)

    model.model.speech_tokenizer.decode = decode_voice

    def validate_audio(audio):
        if not np.isfinite(audio).all():
            raise RuntimeError("El códec de voz produjo valores no válidos.")
        if float(np.mean(np.abs(audio) >= .999)) > .05:
            raise RuntimeError("El códec de voz saturó el audio; no se reproducirá esa salida.")

    class AudioStream:
        def __init__(self, output: Path, request_id: str, buffered: bool, first_frames: int):
            self.output, self.request_id = output, request_id
            self.buffered = buffered
            self.codes, self.paths = [], []
            self.waves = []
            self.pending_packets = []
            self.published = False
            self.decoded_frames = 0
            self.emitted_samples = 0
            self.first_seconds = None
            self.started = time.monotonic()
            self.first_frames = max(8, min(48, first_frames))
            self.chunk_frames = int(settings.get('audioChunkFrames', 16))
            self.next_frames = self.first_frames

        def frame(self, code):
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if int(code[0, 0]) == model.model.config.talker_config.codec_eos_token_id:
                return
            self.codes.append(code.detach().cpu().numpy()[0].copy())
            # Buffered playback cannot expose these prefixes yet. Decode the
            # validated, complete codes once instead of repeating left context
            # every sixteen frames and holding the resulting WAVs anyway.
            if self.buffered:
                return
            if len(self.codes) >= self.next_frames:
                self.decode_pending(np.stack(self.codes))
                self.next_frames += self.chunk_frames

        def decode_pending(self, codes):
            if codes.shape[0] > self.decoded_frames:
                self.waves.append(compiled_codec.decode_range(codes, self.decoded_frames))
                self.decoded_frames = codes.shape[0]
                self.publish(np.concatenate(self.waves), compiled_codec.sample_rate)

        def finish(self, codes):
            host = compiled_codec.host_codes(codes)
            compiled_codec.code_sha256 = hashlib.sha256(np.ascontiguousarray(host, dtype='<i8').tobytes()).hexdigest()
            collected = np.stack(self.codes) if self.codes else np.empty((0, 16), dtype=np.int64)
            if host.shape != collected.shape or not np.array_equal(host, collected):
                raise RuntimeError("Los códigos finales no corresponden a los fragmentos generados; se detuvo la voz.")
            self.decode_pending(host)
            return np.concatenate(self.waves)

        def publish(self, audio, sample_rate):
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            if audio.size <= self.emitted_samples:
                return
            chunk = audio[self.emitted_samples:]
            validate_audio(chunk)
            number = len(self.paths)
            path = self.output.with_name(self.output.stem + f'-chunk-{number:03d}.wav')
            sf.write(path, chunk, sample_rate, subtype='PCM_16')
            self.paths.append(path)
            elapsed = round(time.monotonic() - self.started, 3)
            self.first_seconds = self.first_seconds or elapsed
            packet = {'chunk': True, 'id': self.request_id, 'index': number, 'output': str(path),
                  'sampleRate': sample_rate, 'offsetSamples': self.emitted_samples,
                  'audioSeconds': round(chunk.size / sample_rate, 3), 'elapsedSeconds': elapsed}
            if self.buffered:
                self.pending_packets.append(packet)
            else:
                self.published = True
                emit(packet)
            self.emitted_samples = audio.size

        def flush(self):
            check_cancelled()
            self.published = bool(self.pending_packets) or self.published
            for packet in self.pending_packets:
                emit(packet)
            self.pending_packets.clear()

        def discard(self):
            if self.published:
                raise RuntimeError("Una frase ya enviada no puede reintentarse sin repetir audio.")
            for path in self.paths:
                path.unlink(missing_ok=True)

    def collect_audio_frames(module, args, result):
        nonlocal debug_frames
        if preparing_voice_prefix:
            if result.hidden_states[-1] is not None:
                raise RuntimeError("La preparación inicial alcanzó la generación acústica.")
            raise VoicePrefixPrepared()
        if os.environ.get("SHEEPGPT_TTS_DEBUG") == "1" and debug_frames < 5:
            scores = result.logits[:, -1].float().cpu()
            print(f"[TTS/talker-debug] frame={debug_frames} finite={bool(torch.isfinite(scores).all())} range={scores.min().item():.3f}/{scores.max().item():.3f} top={scores.topk(5).indices.tolist()} codes={result.hidden_states[-1]}", file=sys.stderr)
            debug_frames += 1
        if streaming is not None and result.hidden_states[-1] is not None:
            streaming.frame(result.hidden_states[-1])

    model.model.talker.register_forward_hook(collect_audio_frames)
    # Prepare the same installed voice's invariant instruction and speaker
    # prefix before advertising readiness. Only byte-identical causal rows
    # can later be reused; every requested phrase generates fresh audio and
    # still needs a real EOS and validated waveform before publication.
    preparing_voice_prefix = True
    warm_emotion = os.environ.get("SHEEPGPT_TTS_WARM_EMOTION", "neutral")
    if warm_emotion not in styles:
        warm_emotion = "neutral"
    warm_intensity = max(0., min(1., float(os.environ.get("SHEEPGPT_TTS_WARM_INTENSITY", ".5"))))
    warm_subtlety = "Keep this emotion subtle." if warm_intensity < .4 else "Make this emotion audible, without exaggeration."
    deadline = time.monotonic() + int(settings.get("maximumGenerationSeconds", 180))
    try:
        with contextlib.redirect_stdout(sys.stderr), torch.inference_mode():
            model.generate_custom_voice(text="Hola.", language=language, speaker=speaker,
                instruct=" ".join((settings["baseInstruction"], styles[warm_emotion], warm_subtlety)),
                non_streaming_mode=True, max_new_tokens=2, do_sample=False, subtalker_dosample=False)
    except VoicePrefixPrepared:
        print(f"[SheepGPT TTS/DirectML] Prefijo de voz preparado en {device_name}; sin audio publicado.", file=sys.stderr)
    finally:
        preparing_voice_prefix = False
    emit({
        "ready": True, "engine": "qwen3-tts", "model": settings["model"],
        "target": os.environ.get('SHEEPGPT_TTS_TARGET', 'rx580-directml'),
        "dxgiAdapter": int(os.environ['SHEEPGPT_TTS_DML_DEVICE']),
        "speaker": speaker, "language": language, "device": device_name + " / DirectML",
        "dtype": "float16", "codecDtype": "float32", "codecBackend": "onnx-directml", "predictorBackend": settings["predictorBackend"], "talkerBackend": "onnx-directml", "attention": "eager", "backend": "sheepgpt-directml", "weightsVerified": True,
        "talkerWeights": model.model.talker.model.weight_representation,
        "talkerExecution": model.model.talker.model.execution,
        "predictorExecution": predictor_graph.execution,
        "codecExecution": compiled_codec.execution,
        "auxiliaryCpu": ["attention-metadata", "tensor-shapes", "tokenization", "sampling-control", "cache-and-code-transfer", "wav-writing"],
        "offline": True, "emotionStyles": len(styles) - 1,
        "streaming": bool(settings.get('streamAudio', True)),
        "idleResidency": "gpu-temporarily-then-system-ram" if release_gpu and keep_warm_seconds else "system-ram" if release_gpu else "gpu",
        "gpuKeepWarmSeconds": keep_warm_seconds,
    })

    while True:
        request = requests.queue.get()
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
            started = time.monotonic()
            debug_frames = 0
            predictor_started_seconds = getattr(predictor_graph, "seconds", 0.0)
            codec_started_seconds = compiled_codec.seconds
            codec_short_started = compiled_codec.short_runs
            codec_bulk_started = compiled_codec.bulk_runs
            compiled_codec.long_text_optimization = bool(request.get('longTextOptimization',
                settings.get('longTextOptimization', True)))
            talker_started_seconds = model.model.talker.model.seconds
            prefill_started_seconds = model.model.talker.model.prefill_seconds
            decode_started_seconds = model.model.talker.model.decode_seconds
            reused_started_tokens = model.model.talker.model.reused_tokens
            predictor_copy_started = predictor_graph.captured.copy_seconds
            predictor_replay_started = predictor_graph.captured.compute_seconds
            predictor_frames_started = predictor_graph.captured.runs
            buffered = bool(request.get('bufferedPlayback', settings.get('bufferedPlayback', True)))
            attempts = 3 if buffered else 1
            for attempt in range(attempts):
                check_cancelled()
                streaming = AudioStream(output, request_id, buffered,
                    int(request.get('firstAudioFrames', settings.get('firstAudioFrames', 12)))) if request.get('stream') and settings.get('streamAudio', True) else None
                try:
                    short_phrase = len(text) <= 40
                    # Acoustic EOS, not a text-size estimate, decides completion.
                    # The old 96-frame cap could end an expressive short phrase at
                    # eight seconds. This bounded allowance keeps the same voice
                    # and starts streaming just as early, leaving room for its tail.
                    frame_limit = min(640, max(192, len(text) * 2 + 96))
                    # Companion turns need expressive sampling; greedy Spanish
                    # phrases can repeat acoustic codes until the cap without EOS.
                    # Keep the same installed weights, speaker and DirectML adapter.
                    expressive_dialogue = bool(request.get('expressiveDialogue', False))
                    sample = expressive_dialogue or short_phrase or attempt > 0
                    seed_attempt = attempt + 1 if short_phrase else attempt
                    seed = int.from_bytes(hashlib.sha256(f'{text}|{emotion}|{seed_attempt}'.encode('utf-8')).digest()[:4], 'little')
                    torch.manual_seed(seed)
                    with contextlib.redirect_stdout(sys.stderr), torch.inference_mode():
                        wavs, sample_rate = model.generate_custom_voice(
                            text=text, language=language, speaker=speaker, instruct=instruction,
                            non_streaming_mode=True, max_new_tokens=frame_limit,
                            do_sample=sample, temperature=0.65 if sample else 1.0,
                            top_p=0.9 if sample else 1.0, top_k=50, repetition_penalty=1.08,
                            subtalker_dosample=False,
                        )
                    break
                except IncompleteVoice:
                    if streaming is not None and streaming.published:
                        raise  # Preserve the actual failure; repeating emitted speech is forbidden.
                    if streaming is not None:
                        streaming.discard()
                    if attempt + 1 == attempts:
                        raise
                    print(f'[SheepGPT TTS/DirectML] Reintento {attempt + 1}: la frase no terminó; mismos pesos, voz y RX 580, sin audio publicado.', file=sys.stderr)
            if requests.is_cancelled():
                raise SynthesisInterrupted()
            audio = np.asarray(wavs[0], dtype=np.float32)
            validate_audio(audio)
            if audio.size < sample_rate // 5 or not np.isfinite(audio).all():
                raise RuntimeError("Qwen TTS no generó una forma de onda válida.")
            if float(np.sqrt(np.mean(audio * audio))) < 0.0001:
                raise RuntimeError("La voz generada está vacía o es silencio.")
            sf.write(output, audio, sample_rate, subtype="PCM_16")
            if streaming is not None:
                streaming.publish(audio, sample_rate)
                streaming.flush()
            print(f"[SheepGPT TTS/tiempos] caracteres={len(text)} audio={audio.size / sample_rate:.3f}s "
                  f"total={time.monotonic() - started:.3f}s "
                  f"prefijo={model.model.talker.model.prefill_seconds - prefill_started_seconds:.3f}s "
                  f"prefijoReutilizado={model.model.talker.model.reused_tokens - reused_started_tokens} "
                  f"decode={model.model.talker.model.decode_seconds - decode_started_seconds:.3f}s "
                  f"predictor={getattr(predictor_graph, 'seconds', 0.0) - predictor_started_seconds:.3f}s "
                  f"codec={compiled_codec.seconds - codec_started_seconds:.3f}s", file=sys.stderr)
            emit({"ok": True, "id": request_id, "output": str(output), "emotion": emotion,
                  "samplingMode": "seeded-expressive" if expressive_dialogue else "default",
                  "speaker": speaker, "sampleRate": sample_rate,
                  "audioSeconds": round(audio.size / sample_rate, 3),
                  "synthesisSeconds": round(time.monotonic() - started, 3),
                  "predictorSeconds": round(getattr(predictor_graph, "seconds", 0.0) - predictor_started_seconds, 3),
                  "predictorCopySeconds": round(predictor_graph.captured.copy_seconds - predictor_copy_started, 3),
                  "predictorReplaySeconds": round(predictor_graph.captured.compute_seconds - predictor_replay_started, 3),
                  "predictorFrames": predictor_graph.captured.runs - predictor_frames_started,
                  "attempts": attempt + 1,
                  "acousticFrameLimit": frame_limit, "completed": True,
                  "codecSeconds": round(compiled_codec.seconds - codec_started_seconds, 3),
                  "codecShortBlocks": compiled_codec.short_runs - codec_short_started,
                  "codecBulkBlocks": compiled_codec.bulk_runs - codec_bulk_started,
                  "acousticCodesSha256": compiled_codec.code_sha256,
                  "talkerSeconds": round(model.model.talker.model.seconds - talker_started_seconds, 3),
                  "prefillSeconds": round(model.model.talker.model.prefill_seconds - prefill_started_seconds, 3),
                  "decodeSeconds": round(model.model.talker.model.decode_seconds - decode_started_seconds, 3),
                  "reusedPrefixTokens": model.model.talker.model.reused_tokens - reused_started_tokens,
                  "firstAudioSeconds": streaming.first_seconds if streaming else round(time.monotonic() - started, 3),
                  "chunks": len(streaming.paths) if streaming else 0})
        except SynthesisInterrupted:
            emit({"ok": False, "id": request_id, "cancelled": True})
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            emit({"ok": False, "id": request_id, "error": str(exc)})
        finally:
            if streaming is not None and requests.is_cancelled() and not streaming.published:
                for path in streaming.paths:
                    path.unlink(missing_ok=True)
            streaming = None
            with requests.lock:
                requests.cancelled.discard(request_id)
            requests.active_id = ""
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit({"ready": False, "error": str(error)})
        raise SystemExit(1)
