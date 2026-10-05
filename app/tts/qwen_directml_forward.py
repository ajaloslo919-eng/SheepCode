"""Inference bridge for SheepGPT's three compiled Qwen graphs.

The original Qwen wrapper still constructs the voice/emotion/text prompt on RX
580. Its generation loop retains token penalties, sampling and stopping rules.
Only token control and transport use host tensors; neural embeddings, acoustic
prediction, attention, projection and waveform synthesis execute on DirectML.
"""
from __future__ import annotations

from types import MethodType

import torch
from qwen_tts.core.models.modeling_qwen3_tts import Qwen3TTSTalkerOutputWithPast
from transformers.utils.generic import can_return_tuple


def install_fused_forward(talker, predictor, check_cancelled):
    @can_return_tuple
    def forward(self, input_ids=None, attention_mask=None, position_ids=None,
                past_key_values=None, inputs_embeds=None, labels=None, use_cache=None,
                output_attentions=None, output_hidden_states=None, cache_position=None,
                past_hidden=None, trailing_text_hidden=None, tts_pad_embed=None,
                generation_step=None, subtalker_dosample=None, subtalker_top_p=None,
                subtalker_top_k=None, subtalker_temperature=None, **kwargs):
        if labels is not None:
            raise RuntimeError("El puente de voz es solo para inferencia; el entrenamiento usa su entorno separado.")
        check_cancelled()
        if inputs_embeds is not None and inputs_embeds.shape[1] > 1:
            generation_step, codec_ids = -1, None
        else:
            if subtalker_dosample:
                raise RuntimeError("El predictor acústico integrado requiere selección determinista.")
            text_hidden = (trailing_text_hidden[:, generation_step:generation_step + 1]
                           if generation_step < trailing_text_hidden.shape[1] else tts_pad_embed)
            prediction = predictor.predict(past_hidden, input_ids, text_hidden, check_cancelled)
            codec_ids = torch.cat((input_ids, prediction.sequences), dim=-1)
            inputs_embeds = prediction.acoustic_hidden

        # Qwen's original position bookkeeping, now on host metadata tensors.
        if attention_mask is not None:
            if cache_position is None or cache_position[0] == 0 or self.rope_deltas is None:
                delta0 = (1 - attention_mask).sum(dim=-1).unsqueeze(1)
                position_ids, rope_deltas = self.get_rope_index(attention_mask)
                self.rope_deltas = rope_deltas - delta0
            else:
                batch, length = input_ids.shape
                delta = cache_position[0] + self.rope_deltas if cache_position is not None else 0
                position_ids = torch.arange(length).view(1, -1).expand(batch, -1).add(delta)
                position_ids = position_ids.unsqueeze(0).expand(3, -1, -1)
        outputs = self.model(attention_mask=attention_mask, position_ids=position_ids,
            past_key_values=past_key_values, inputs_embeds=inputs_embeds,
            use_cache=use_cache, output_attentions=output_attentions,
            output_hidden_states=output_hidden_states, cache_position=cache_position, **kwargs)
        return Qwen3TTSTalkerOutputWithPast(loss=None, logits=outputs.codec_logits,
            past_key_values=outputs.past_key_values, hidden_states=(outputs.hidden_states, codec_ids),
            attentions=outputs.attentions, past_hidden=outputs.last_hidden_state,
            generation_step=generation_step + 1, trailing_text_hidden=trailing_text_hidden,
            tts_pad_embed=tts_pad_embed)

    talker.forward = MethodType(forward, talker)
