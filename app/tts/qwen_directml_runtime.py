"""SheepGPT's RX 580 backend: neural inference on DirectML, host-side masks.

The model, speaker and speech codec remain Qwen3-TTS. Small causal masks and
sampling control use the CPU; this is not a CPU fallback for the neural network.
"""
from __future__ import annotations

import sys
import os
from types import SimpleNamespace

import torch
import torch_directml


def select_rx580():
    target = os.environ.get('SHEEPGPT_TTS_TARGET', 'rx580-directml')
    if target not in ('rx580-directml', 'rtx2060s-directml'):
        raise RuntimeError('El adaptador de voz solicitado no está integrado.')
    if target == 'rtx2060s-directml' and os.environ.get('SHEEPGPT_TTS_DUAL_AUTHORIZED') != '1':
        raise RuntimeError('La voz RTX requiere el modo de dos GPU activado por el usuario.')
    wanted = 'rx 580' if target == 'rx580-directml' else 'rtx 2060 super'
    adapters = [(index, torch_directml.device_name(index).rstrip("\0"))
                for index in range(torch_directml.device_count())]
    for index, name in adapters:
        if wanted in name.lower():
            print(f"[SheepGPT TTS/DirectML] Adaptador {index}: {name}", file=sys.stderr)
            os.environ['SHEEPGPT_TTS_VERIFIED_NAME'] = name
            return torch_directml.device(index), name
    raise RuntimeError(f"No se encontró {wanted} en DirectML: {adapters}")


def causal_mask(*, config, input_embeds, attention_mask=None, cache_position=None,
                past_key_values=None, position_ids=None, sliding=False, **kwargs):
    """Additive mask without vmap or CUDA-only attention mask builders."""
    if attention_mask is not None and attention_mask.dim() == 4:
        return attention_mask
    batch, queries = input_embeds.shape[:2]
    past = past_key_values.get_seq_length() if past_key_values is not None else 0
    positions = (cache_position.detach().cpu() if cache_position is not None else
                 torch.arange(past, past + queries))
    keys = past + queries
    if attention_mask is not None:
        keys = max(keys, attention_mask.shape[-1])
    if past_key_values is not None and getattr(past_key_values, "is_compileable", False):
        keys = max(keys, past_key_values.get_max_cache_shape())
    columns = torch.arange(keys)
    permitted = columns[None, :] <= positions.reshape(-1, 1)
    if sliding:
        window = getattr(config, "sliding_window", None)
        if window:
            permitted = permitted & (columns[None, :] > positions.reshape(-1, 1) - window)
    permitted = permitted[None, None, :, :].expand(batch, 1, queries, keys)
    if attention_mask is not None:
        permitted = permitted & attention_mask.detach().cpu()[:, None, None, :keys].bool()
    mask = torch.zeros((batch, 1, queries, keys), dtype=input_embeds.dtype)
    mask.masked_fill_(~permitted, torch.finfo(input_embeds.dtype).min)
    return mask.to(input_embeds.device)


def install_compatibility():
    # Patch only this worker process, not package files or training environments.
    from qwen_tts.core.models import modeling_qwen3_tts as talker
    from qwen_tts.core.tokenizer_12hz import modeling_qwen3_tts_tokenizer_v2 as codec
    from transformers.generation.logits_process import LogitsProcessorList
    from transformers.generation.stopping_criteria import StoppingCriteriaList
    def sliding_mask(**kwargs):
        return causal_mask(**kwargs, sliding=True)
    for module in (talker, codec):
        module.create_causal_mask = causal_mask
        module.create_sliding_window_causal_mask = sliding_mask
    original_logits = LogitsProcessorList.__call__
    def process_logits(processors, input_ids, scores, **kwargs):
        if scores.device.type == "privateuseone":
            # Vocabulary penalties and token choice are small host-side controls;
            # neural forward passes and the waveform decoder stay on the RX 580.
            for processor in processors:
                for name, value in vars(processor).items():
                    if isinstance(value, torch.Tensor) and value.device.type == "privateuseone":
                        setattr(processor, name, value.cpu())
            return original_logits(processors, input_ids.cpu(), scores.cpu(), **kwargs).to(scores.device)
        return original_logits(processors, input_ids, scores, **kwargs)
    LogitsProcessorList.__call__ = process_logits
    original_multinomial = torch.multinomial
    def sample_tokens(probabilities, *args, **kwargs):
        if probabilities.device.type == "privateuseone":
            return original_multinomial(probabilities.cpu(), *args, **kwargs).to(probabilities.device)
        return original_multinomial(probabilities, *args, **kwargs)
    torch.multinomial = sample_tokens
    original_stopping = StoppingCriteriaList.__call__
    def stop_tokens(criteria, input_ids, scores, **kwargs):
        if input_ids.device.type == "privateuseone":
            for criterion in criteria:
                for name, value in vars(criterion).items():
                    if isinstance(value, torch.Tensor) and value.device.type == "privateuseone":
                        setattr(criterion, name, value.cpu())
            return original_stopping(criteria, input_ids.cpu(), scores, **kwargs).to(input_ids.device)
        return original_stopping(criteria, input_ids, scores, **kwargs)
    StoppingCriteriaList.__call__ = stop_tokens
    original_cat = torch.cat
    def concatenate(tensors, dim=0, *, out=None):
        items = list(tensors)
        if out is None and items and any(t.device.type == "privateuseone" and t.numel() == 0 for t in items):
            # HF begins generation from embeddings with an empty token history.
            # DirectML cannot concatenate an axis of length zero. Dropping that
            # empty operand is exact and keeps all non-empty tensors on the GPU.
            axis = dim % items[0].dim()
            reference = items[0].shape
            if all(t.dim() == len(reference) and all(t.shape[d] == reference[d] for d in range(len(reference)) if d != axis) for t in items):
                nonempty = [t for t in items if t.shape[axis] != 0]
                if len(nonempty) == 1:
                    return nonempty[0].clone()
                if nonempty:
                    return original_cat(nonempty, dim=dim)
        return original_cat(items, dim=dim, out=out)
    torch.cat = concatenate
    def snake_beta(module, hidden_states):
        # Use multiplication for sin²: it is defined for negative sine values,
        # unlike the generic floating Pow path on this DirectML device.
        dtype = hidden_states.dtype
        values = hidden_states.float()
        alpha = module.alpha.float().exp()[None, :, None]
        beta = module.beta.float().exp()[None, :, None]
        sine = (values * alpha).sin()
        return (values + (sine * sine) / (beta + module.no_div_by_zero)).to(dtype)
    codec.SnakeBeta.forward = snake_beta
    original_conv1d = torch.nn.Conv1d.forward
    def conv1d(module, values):
        if values.device.type != "privateuseone":
            return original_conv1d(module, values)
        if module.padding_mode != "zeros":
            raise RuntimeError("El códec DirectML requiere padding cero en la convolución.")
        # Represent a 1D convolution as an equivalent 2D convolution with a
        # height of one. This avoids DirectML's broken dilated Conv1d path.
        return torch.nn.functional.conv2d(values.unsqueeze(2), module.weight.unsqueeze(2), module.bias,
            stride=(1, module.stride[0]), padding=(0, module.padding[0]),
            dilation=(1, module.dilation[0]), groups=module.groups).squeeze(2)
    torch.nn.Conv1d.forward = conv1d
    original_transpose1d = torch.nn.ConvTranspose1d.forward
    def transpose1d(module, values, output_size=None):
        if values.device.type != "privateuseone":
            return original_transpose1d(module, values, output_size)
        if output_size is not None:
            raise RuntimeError("El códec DirectML usa el tamaño de salida fijado por los pesos.")
        return torch.nn.functional.conv_transpose2d(values.unsqueeze(2), module.weight.unsqueeze(2), module.bias,
            stride=(1, module.stride[0]), padding=(0, module.padding[0]),
            output_padding=(0, module.output_padding[0]), groups=module.groups,
            dilation=(1, module.dilation[0])).squeeze(2)
    torch.nn.ConvTranspose1d.forward = transpose1d
    print(f"[SheepGPT TTS/DirectML] Atención eager; máscaras causales auxiliares en CPU; pesos y códec en {os.environ['SHEEPGPT_TTS_VERIFIED_NAME']}.", file=sys.stderr)


def move_voice_inputs(model, destination):
    """Place the active PyTorch prompt input layers on the selected GPU.

    The compiled DirectML graphs own the main neural layers and waveform codec.
    Their unused PyTorch source copies stay in RAM and never run a forward pass.
    """
    talker = model.model.talker
    components = (talker.model.codec_embedding, talker.model.text_embedding, talker.text_projection)
    for component in components:
        component.to(destination)
        if any(parameter.device != destination for parameter in component.parameters()):
            raise RuntimeError("Una capa activa de entrada/salida de voz no quedó en la RX 580.")
    model.device = destination
    model.model.speech_tokenizer.device = destination


def move_model(model, destination):
    # Qwen forwards dtype/device to its separate codec, but consumes the talker's
    # attention option before loading it. Set the codec's nested configurations
    # explicitly so DirectML does not enter SDPA's unsupported fused path.
    for module in model.model.speech_tokenizer.model.modules():
        if hasattr(module, "config"):
            module.config._attn_implementation = "eager"
    model.model.to(destination)
    # The waveform decoder's periodic Snake activations need float32 accuracy
    # on this DirectML device. Only the codec uses FP32; the large talker stays
    # FP16 and both components still execute on the RX 580.
    model.model.speech_tokenizer.model.float().to(destination)
    model.device = destination
    model.model.speech_tokenizer.device = destination
    if destination.type != "cpu":
        for label, component in (("voz", model.model), ("códec", model.model.speech_tokenizer.model)):
            misplaced = [name for name, parameter in component.named_parameters()
                         if parameter.device != destination]
            if misplaced:
                raise RuntimeError(f"Los pesos de {label} no quedaron en RX 580: {misplaced[:5]}")


class DirectCodePredictor:
    """The installed model's 15 greedy codebooks, without CUDA graphs."""
    def __init__(self, predictor, device, dtype):
        self.predictor = predictor
        self.debug_calls = 0
        self.positions = [torch.arange(2, device=device)] + [torch.tensor([i + 1], device=device) for i in range(1, 15)]
        first = torch.tensor([[0., float("-inf")], [0., 0.]], dtype=dtype).reshape(1, 1, 2, 2).to(device)
        self.masks = [first] + [torch.zeros((1, 1, 1, i + 2), dtype=dtype, device=device) for i in range(1, 15)]

    def predict(self, inputs, check_cancelled):
        cache, tokens = None, []
        for step in range(15):
            check_cancelled()
            result = self.predictor(
                inputs_embeds=inputs if step == 0 else None,
                input_ids=tokens[-1] if step else None,
                past_key_values=cache, use_cache=True, generation_steps=step,
                cache_position=self.positions[step], position_ids=self.positions[step].unsqueeze(0),
                attention_mask={"full_attention": self.masks[step]},
                output_hidden_states=False, return_dict=True,
            )
            cache = result.past_key_values
            token = result.logits[:, -1].argmax(-1, keepdim=True)
            if os.environ.get("SHEEPGPT_TTS_DEBUG") == "1" and self.debug_calls < 2 and step < 2:
                host = result.logits[:, -1].float().cpu()
                print(f"[TTS/code-debug] step={step} range={host.min().item():.3f}/{host.max().item():.3f} gpu={token.cpu().tolist()} cpu={host.argmax(-1).tolist()}", file=sys.stderr)
            tokens.append(token)
        self.debug_calls += 1
        return SimpleNamespace(sequences=torch.cat(tokens, dim=1))
