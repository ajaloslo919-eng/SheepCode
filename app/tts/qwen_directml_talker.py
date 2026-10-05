"""Qwen talker layers and acoustic cache on RX 580; host attention metadata."""
from __future__ import annotations
import json
import os
import sys
import time

import numpy as np
import torch
from transformers.cache_utils import DynamicCache
from transformers.modeling_outputs import BaseModelOutputWithPast
from qwen_directml_graph import load_graph


class VoiceCache(DynamicCache):
    def __init__(self, shape, adapter):
        super().__init__()
        # Let this session allocate GPU resources, so buffers use the same
        # D3D12 device/queue. Independent DirectML allocators are incompatible
        # with some installed AMD drivers, even when the ordinal is identical.
        self.packed = None
        self.seed = np.zeros(shape, dtype=np.float16)
        self.seen = 0

    def get_seq_length(self, layer_idx=0):
        return self.seen

    def get_mask_sizes(self, cache_position, layer_idx=0):
        return self.seen + cache_position.shape[0], 0


class DirectTalker(torch.nn.Module):
    def __init__(self, original, model_path, device):
        super().__init__()
        self.config = original.config
        self.device = device
        self.codec_embedding = original.codec_embedding
        self.text_embedding = original.text_embedding
        # Keep an unused copy in system RAM for reconstruction; no neural forward
        # pass calls these layers on the CPU. The compiled graph owns GPU weights.
        original.layers.to("cpu")
        original.norm.to("cpu")
        original.rotary_emb.to("cpu")
        object.__setattr__(self, "source_weights", original)
        self.session, manifest = load_graph(model_path, "talker-core", model_path / "model.safetensors",
            metadata_cpu=True, subfolder="talker-gpu-cache", fixed_queries=1)
        if not manifest.get("gpuCache") or not manifest.get("textOnlyRope") or not manifest.get("fusedCodecHead"):
            raise RuntimeError("El gráfico no contiene el caché acústico de GPU seleccionado.")
        self.adapter = int(os.environ["SHEEPGPT_TTS_DML_DEVICE"])
        self.capacity = int(manifest["capacity"])
        self.weight_representation = manifest.get("weightRepresentation", "float16")
        self.cache_shape = (2, manifest["layers"], 1, manifest["heads"], self.capacity, manifest["headDim"])
        self.seconds = 0.0
        self.prefill_seconds = 0.0
        self.decode_seconds = 0.0
        self.execution = "fixed-frame-with-prefix-reuse"
        self.prefix = None
        self.reused_tokens = 0
        self.check_cancelled = None
        self.cpu_operations = []
        self.verify_placement()

    def get_input_embeddings(self):
        return self.codec_embedding

    def get_text_embeddings(self):
        return self.text_embedding

    def verify_placement(self):
        # A startup operation checks the active compiled component, not a model
        # candidate or an alternative response. No audio or chat is published.
        cache = VoiceCache(self.cache_shape, self.adapter)
        probe = np.zeros((1, 1, self.config.hidden_size), dtype=np.float16)
        positions = np.zeros((1, 1), dtype=np.int64)
        mask = np.full((1, 1, 1, self.capacity), -65504., dtype=np.float16)
        mask[..., 0] = 0
        self.run_cached(probe, positions, mask, cache)
        self.verify_profile(self.session.end_profiling(), "fixed-frame")

    def verify_profile(self, path, label):
        with open(path, encoding="utf-8") as source:
            events = json.load(source)
        nodes = [item for item in events if item.get("cat") == "Node" and item.get("args", {}).get("provider")]
        forbidden = {"MatMul", "Gemm", "Conv", "ConvTranspose", "Softmax", "LayerNormalization", "RmsNormalization"}
        wrong = [item["name"] for item in nodes if item["args"]["provider"] == "CPUExecutionProvider"
            and (item["args"].get("op_name") in forbidden or
                 any(any(kind in {"float", "float16", "double", "bfloat16"} for kind in entry)
                     for entry in item["args"].get("input_type_shape", [])))]
        if wrong or not any(item["args"]["provider"] == "DmlExecutionProvider" for item in nodes):
            raise RuntimeError(f"Las capas neuronales del generador no quedaron en RX 580: {wrong[:5]}")
        self.cpu_operations = sorted(set(self.cpu_operations) |
            {item["args"].get("op_name", "") for item in nodes if item["args"]["provider"] == "CPUExecutionProvider"})
        print(f"[SheepGPT TTS/DirectML] Capas del generador {label} verificadas en {os.environ['SHEEPGPT_TTS_VERIFIED_NAME']}; operaciones auxiliares CPU: {self.cpu_operations}; perfil: {path}", file=sys.stderr)

    def run_cached(self, hidden, positions, mask, cache, prefill=False, snapshot=False):
        """A single session owns both prefix and acoustic GPU cache resources."""
        if hidden.shape[1] != 1:
            raise RuntimeError("La sesión de voz requiere exactamente un paso por consulta.")
        if self.check_cancelled is not None:
            self.check_cancelled()
        binding = self.session.io_binding()
        binding.bind_cpu_input("hidden", hidden)
        binding.bind_cpu_input("positions", positions)
        binding.bind_cpu_input("mask", mask)
        slots = np.broadcast_to(np.arange(cache.seen, cache.seen + hidden.shape[1], dtype=np.int64)[None, None, :, None],
            (1, self.cache_shape[3], hidden.shape[1], self.cache_shape[5])).copy()
        binding.bind_cpu_input("slots", slots)
        if cache.packed is None:
            binding.bind_cpu_input("packed_cache", cache.seed)
        else:
            binding.bind_ortvalue_input("packed_cache", cache.packed)
        binding.bind_output("states", "cpu")
        binding.bind_output("logits", "cpu")
        # DirectML's session allocator uses logical device 0. The physical
        # RX 580 is selected by the session's verified DXGI ordinal, not this
        # internal memory identifier (which is not the NVIDIA adapter index).
        binding.bind_output("next_cache", "dml", 0)
        started = time.monotonic()
        self.session.run_with_iobinding(binding)
        binding.synchronize_outputs()
        elapsed = time.monotonic() - started
        self.seconds += elapsed
        if prefill:
            self.prefill_seconds += elapsed
        else:
            self.decode_seconds += elapsed
        states, logits, next_cache = binding.get_outputs()
        if next_cache.device_name() != "dml":
            raise RuntimeError("El caché de decodificación de voz abandonó la RX 580.")
        cache.packed, cache.seed = next_cache, None
        if snapshot:
            # Preserve computed prefix data in host RAM. It is immutable and
            # copied once per phrase; acoustic decode never transfers this KV.
            # All embeddings and transformer arithmetic remain on RX 580.
            self.prefix_cache = binding.copy_outputs_to_cpu()[2]
        return states.numpy(), logits.numpy()

    def forward(self, input_ids=None, attention_mask=None, position_ids=None, past_key_values=None,
                inputs_embeds=None, use_cache=True, output_hidden_states=None, **kwargs):
        if inputs_embeds is None:
            raise RuntimeError("El generador Qwen requiere embeddings de la voz instalada.")
        hidden = np.ascontiguousarray(inputs_embeds.detach().cpu().numpy(), dtype=np.float16)
        queries = hidden.shape[1]
        cache = past_key_values if isinstance(past_key_values, VoiceCache) else VoiceCache(self.cache_shape, self.adapter)
        if cache.seen + queries > self.capacity:
            raise RuntimeError("La frase supera la capacidad del caché acústico; reduce el tamaño del segmento de voz.")
        if position_ids is None:
            positions = np.broadcast_to(np.arange(cache.seen, cache.seen + queries, dtype=np.int64)[None, None, :], (3, 1, queries)).copy()
        else:
            positions = np.ascontiguousarray(position_ids.detach().cpu().numpy(), dtype=np.int64)
            if positions.ndim == 2:
                positions = np.repeat(positions[None, :, :], 3, axis=0)
        if not np.array_equal(positions[0], positions[1]) or not np.array_equal(positions[0], positions[2]):
            raise RuntimeError("Este generador conserva MRoPE para texto; recibió posiciones multimodales incompatibles.")
        positions = np.ascontiguousarray(positions[0])
        permitted = np.zeros((queries, self.capacity), dtype=bool)
        permitted[:, :cache.seen] = True
        permitted[:, cache.seen:cache.seen + queries] = np.tril(np.ones((queries, queries), dtype=bool))
        if isinstance(attention_mask, torch.Tensor) and attention_mask.ndim == 2:
            padding = attention_mask.detach().cpu().numpy()[0].astype(bool)
            permitted[:, :cache.seen] &= padding[:cache.seen]
            permitted[:, cache.seen:cache.seen + queries] &= padding[cache.seen:cache.seen + queries]
        mask = np.where(permitted, 0., -65504.).astype(np.float16)[None, None, :, :]
        if cache.seen == 0 and queries > 1:
            reused = 0
            if self.prefix is not None:
                old_hidden, old_positions, old_mask, old_cache = self.prefix
                limit = min(queries - 1, old_hidden.shape[1])
                for index in range(limit):
                    # Reuse only a byte-identical causal prefix. A changed
                    # instruction, emotion, voice, position or padding breaks
                    # the match; no audio/text response is reused or invented.
                    if (not np.array_equal(hidden[:, index], old_hidden[:, index]) or
                        not np.array_equal(positions[:, index], old_positions[:, index]) or
                        not np.array_equal(mask[:, :, index, :index + 1], old_mask[:, :, index, :index + 1])):
                        break
                    reused += 1
                if reused:
                    cache.seed = old_cache
                    cache.seen = reused
            self.reused_tokens += reused
            for index in range(reused, queries):
                states, logits = self.run_cached(hidden[:, index:index + 1], positions[:, index:index + 1],
                    mask[:, :, index:index + 1], cache, prefill=True, snapshot=index == queries - 1)
                cache.seen += 1
            self.prefix = (hidden.copy(), positions.copy(), mask.copy(), self.prefix_cache)
        else:
            states, logits = self.run_cached(hidden, positions, mask, cache)
            cache.seen += queries
        tensor = torch.from_numpy(states)
        result = BaseModelOutputWithPast(last_hidden_state=tensor, past_key_values=cache,
            hidden_states=(tensor,), attentions=None)
        result.codec_logits = torch.from_numpy(logits)
        return result
