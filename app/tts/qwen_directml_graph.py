"""One compiled GPU graph for SheepGPT's installed Qwen acoustic codebooks."""
from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import onnxruntime as ort
import torch


def load_graph(model_path: Path, name: str, checkpoint: Path, metadata_cpu=False, subfolder="", fixed_queries=None, capture=False):
    graph = model_path / "directml" / subfolder / (name + ".onnx")
    manifest = json.loads(graph.with_suffix(".json").read_text(encoding="utf-8"))
    if manifest["sourceBytes"] != checkpoint.stat().st_size or manifest["sourceModifiedNs"] != checkpoint.stat().st_mtime_ns:
        raise RuntimeError("El gráfico acústico no corresponde a los pesos instalados.")
    with graph.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != manifest["graphSha256"]:
            raise RuntimeError("El gráfico acústico no pasó su verificación de integridad.")
    adapter = int(os.environ["SHEEPGPT_TTS_DML_DEVICE"])
    if adapter < 0 or "DmlExecutionProvider" not in ort.get_available_providers():
        raise RuntimeError("Falta el proveedor DirectML de la RX 580 para la voz.")
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    options.log_severity_level = 3
    if capture:
        options.add_session_config_entry("ep.dml.enable_graph_capture", "1")
    if fixed_queries is not None:
        options.add_free_dimension_override_by_name("queries", fixed_queries)
    if metadata_cpu:
        options.enable_profiling = True
        options.profile_file_prefix = str(model_path / "directml" / f"talker-placement-{os.environ.get('SHEEPGPT_TTS_TARGET', 'rx580-directml')}-{os.getpid()}")
    else:
        options.add_session_config_entry("session.disable_cpu_ep_fallback", "1")
    session = ort.InferenceSession(str(graph), sess_options=options,
        providers=[("DmlExecutionProvider", {"device_id": str(adapter), "disable_metacommands": "false"})])
    if session.get_providers()[0] != "DmlExecutionProvider":
        raise RuntimeError("El gráfico de voz no se asignó a DirectML.")
    print(f"[SheepGPT TTS/DirectML] Gráfico Qwen {name}; {os.environ['SHEEPGPT_TTS_VERIFIED_NAME']} DXGI {adapter}; sin sustitución neuronal por CPU.", file=sys.stderr)
    return session, manifest


class CapturedGpuGraph:
    """Keep neural resources fixed while DirectML replays its command list.

    ONNX Runtime 1.24's Python DML allocator creates adapter zero even when a
    different ordinal is requested. Allocate input buffers through an identity
    transfer session on the verified RX 580 instead. This transport performs no
    neural arithmetic and never allocates on the other GPU.
    """
    def __init__(self, session):
        import onnx
        from onnx import helper, TensorProto
        self.session = session
        self.binding = session.io_binding()
        self.copy_seconds = 0.0
        self.compute_seconds = 0.0
        self.runs = 0
        self.shapes = {item.name: tuple(item.shape) for item in session.get_inputs()}
        supported = {"tensor(float16)": (TensorProto.FLOAT16, np.float16),
                     "tensor(int64)": (TensorProto.INT64, np.int64)}
        if any(item.type not in supported for item in session.get_inputs()) or any(
                any(not isinstance(n, int) or n <= 0 for n in shape) for shape in self.shapes.values()):
            raise RuntimeError("La captura requiere entradas Qwen de tipo y tamaño fijo.")
        types = {item.name: supported[item.type][0] for item in session.get_inputs()}
        self.dtypes = {item.name: supported[item.type][1] for item in session.get_inputs()}
        nodes = [helper.make_node("Identity", [name], ["upload_" + name]) for name in self.shapes]
        inputs = [helper.make_tensor_value_info(name, types[name], shape) for name, shape in self.shapes.items()]
        outputs = [helper.make_tensor_value_info("upload_" + name, types[name], shape) for name, shape in self.shapes.items()]
        graph = helper.make_model(helper.make_graph(nodes, "SheepGPT-RX580-input-transfer", inputs, outputs),
                                  opset_imports=[helper.make_opsetid("", 17)], ir_version=9)
        onnx.checker.check_model(graph)
        options = ort.SessionOptions()
        options.enable_mem_pattern = False
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
        options.log_severity_level = 3
        adapter = int(os.environ["SHEEPGPT_TTS_DML_DEVICE"])
        self.transfer = ort.InferenceSession(graph.SerializeToString(), sess_options=options,
            providers=[("DmlExecutionProvider", {"device_id": str(adapter)})])
        self.transfer_binding = self.transfer.io_binding()
        seed = {name: np.zeros(shape, dtype=self.dtypes[name])
                for name, shape in self.shapes.items()}
        for name, array in seed.items():
            self.transfer_binding.bind_cpu_input(name, array)
            self.transfer_binding.bind_output("upload_" + name, "dml", 0)
        self.transfer.run_with_iobinding(self.transfer_binding)
        self.transfer_binding.synchronize_outputs()
        self.inputs = dict(zip(self.shapes, self.transfer_binding.get_outputs()))
        if any(value.device_name() != "dml" for value in self.inputs.values()):
            raise RuntimeError("Las entradas de voz no quedaron en la RX 580.")
        for name, value in self.inputs.items():
            self.binding.bind_ortvalue_input(name, value)
            self.transfer_binding.bind_ortvalue_output("upload_" + name, value)
        for item in session.get_outputs():
            self.binding.bind_output(item.name, "dml", 0)
        bootstrap = ort.RunOptions()
        bootstrap.add_run_config_entry("gpu_graph_id", "-1")
        session.run_with_iobinding(self.binding, bootstrap)
        self.binding.synchronize_outputs()
        self.outputs = self.binding.get_outputs()
        if any(value.device_name() != "dml" for value in self.outputs):
            raise RuntimeError("Las salidas de voz no quedaron en la RX 580.")
        for item, value in zip(session.get_outputs(), self.outputs):
            self.binding.bind_ortvalue_output(item.name, value)

    def run(self, feeds):
        started = time.monotonic()
        if set(feeds) != set(self.shapes):
            raise RuntimeError("Falta una entrada del gráfico de voz capturado.")
        for name, array in feeds.items():
            if array.shape != self.shapes[name] or array.dtype != self.dtypes[name]:
                raise RuntimeError("Cambió la forma o el tipo de una entrada de voz Qwen.")
            self.transfer_binding.bind_cpu_input(name, array)
        self.transfer.run_with_iobinding(self.transfer_binding)
        self.transfer_binding.synchronize_outputs()
        self.copy_seconds += time.monotonic() - started
        started = time.monotonic()
        self.session.run_with_iobinding(self.binding)
        self.binding.synchronize_outputs()
        values = self.binding.copy_outputs_to_cpu()
        self.compute_seconds += time.monotonic() - started
        self.runs += 1
        return values


class GraphCodePredictor:
    def __init__(self, model_path: Path, device):
        self.session, manifest = load_graph(model_path, "code-predictor", model_path / "model.safetensors", capture=True)
        if not manifest.get("fusedEmbeddingSum"):
            raise RuntimeError("Falta el predictor integrado con embeddings de la voz instalada.")
        self.device = device
        self.seconds = 0.0
        self.captured = CapturedGpuGraph(self.session)
        self.execution = "fixed-address-graph-replay"

    def predict(self, past_hidden, first_code, text_hidden, check_cancelled):
        check_cancelled()
        feeds = {"past_hidden": np.ascontiguousarray(past_hidden.numpy(), dtype=np.float16),
                 "first_code": np.ascontiguousarray(first_code.numpy(), dtype=np.int64),
                 "text_hidden": np.ascontiguousarray(text_hidden.numpy(), dtype=np.float16)}
        started = time.monotonic()
        codes, hidden = self.captured.run(feeds)
        self.seconds += time.monotonic() - started
        check_cancelled()
        return SimpleNamespace(sequences=torch.from_numpy(codes.astype(np.int64, copy=False)),
                               acoustic_hidden=torch.from_numpy(hidden))


class GraphVoiceCodec:
    def __init__(self, model_path: Path):
        # Prioritize the first audible packet in immediate playback. This
        # shorter graph retains the installed full-precision neural decoder,
        # speaker and 25-frame causal context; it does not change the voice.
        self.session, manifest = load_graph(model_path, "waveform-codec", model_path / "speech_tokenizer" / "model.safetensors",
            subfolder="waveform-short", capture=True)
        self.frames = int(manifest["frames"])
        self.samples_per_frame = int(manifest["samplesPerFrame"])
        self.sample_rate = int(manifest["sampleRate"])
        bulk_session, bulk_manifest = load_graph(model_path, "waveform-codec",
            model_path / "speech_tokenizer" / "model.safetensors", capture=True)
        self.bulk_frames = int(bulk_manifest["frames"])
        if (self.frames != 32 or self.bulk_frames != 64 or
                bulk_manifest["samplesPerFrame"] != self.samples_per_frame or
                bulk_manifest["sampleRate"] != self.sample_rate):
            raise RuntimeError("Los bloques del códec instalado no conservan el formato de voz.")
        self.short = CapturedGpuGraph(self.session)
        self.bulk = CapturedGpuGraph(bulk_session)
        self.execution = "adaptive-block-graph-replay"
        self.long_text_optimization = True
        self.check_cancelled = None
        self.seconds = 0.0
        self.short_runs = 0
        self.bulk_runs = 0
        self.code_sha256 = ""

    @staticmethod
    def host_codes(codes):
        host = codes.detach().cpu().numpy() if isinstance(codes, torch.Tensor) else np.asarray(codes)
        if host.ndim != 2 or host.shape[1] != 16 or host.shape[0] == 0:
            raise RuntimeError("Los códigos de la voz Qwen no tienen la forma esperada.")
        return host

    def decode_range(self, host, start, end=None):
        """Decode only new frames, using Qwen's 25-frame causal left context."""
        end = host.shape[0] if end is None else end
        chunks = []
        while start < end:
            if self.check_cancelled is not None:
                self.check_cancelled()
            context = min(25, start)
            use_bulk = self.long_text_optimization and end - start > self.frames - context
            frames = self.bulk_frames if use_bulk else self.frames
            count = min(frames - context, end - start)
            padded = np.zeros((1, 16, frames), dtype=np.int64)
            part = host[start - context:start + count].T
            padded[0, :, :part.shape[1]] = part
            started = time.monotonic()
            audio = (self.bulk if use_bulk else self.short).run({"codes": padded})[0][0, 0]
            self.seconds += time.monotonic() - started
            if use_bulk:
                self.bulk_runs += 1
            else:
                self.short_runs += 1
            if self.check_cancelled is not None:
                self.check_cancelled()
            if not np.isfinite(audio).all():
                raise RuntimeError("El gráfico del códec produjo muestras no válidas.")
            chunks.append(audio[context * self.samples_per_frame:(context + count) * self.samples_per_frame].copy())
            start += count
        return np.concatenate(chunks).astype(np.float32, copy=False) if chunks else np.empty(0, dtype=np.float32)

    def decode(self, encoded):
        waves = []
        for item in encoded:
            host = self.host_codes(item["audio_codes"])
            self.code_sha256 = hashlib.sha256(np.ascontiguousarray(host, dtype='<i8').tobytes()).hexdigest()
            waves.append(self.decode_range(host, 0))
        return waves, self.sample_rate
