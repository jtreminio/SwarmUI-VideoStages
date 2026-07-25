"""Pure math: linear Rec.709 HDR light -> PQ-encoded Rec.2020 (SMPTE ST 2084)."""

from __future__ import annotations

import torch
from torch import Tensor

# Linear Rec.709 -> Rec.2020 primaries (ITU-R BT.2087, shared D65 white point).
_REC709_TO_REC2020 = (
    (0.6274039, 0.3292830, 0.0433131),
    (0.0690973, 0.9195404, 0.0113623),
    (0.0163914, 0.0880132, 0.8955953),
)

# SMPTE ST 2084 (PQ) inverse-EOTF constants.
_PQ_M1 = 0.1593017578125
_PQ_M2 = 78.84375
_PQ_C1 = 0.8359375
_PQ_C2 = 18.8515625
_PQ_C3 = 18.6875

_PQ_PEAK_NITS = 10000.0


def _pq_oetf(luminance_nits: Tensor) -> Tensor:
    """Encode absolute luminance (nits) to a PQ signal in [0, 1]."""
    y = (luminance_nits / _PQ_PEAK_NITS).clamp(0.0, 1.0)
    y_m1 = torch.pow(y, _PQ_M1)
    return torch.pow((_PQ_C1 + _PQ_C2 * y_m1) / (1.0 + _PQ_C3 * y_m1), _PQ_M2)


def linear709_to_pq2020(hdr_linear: Tensor, diffuse_white_nits: float = 100.0) -> Tensor:
    """Map linear Rec.709 HDR light [..., 3] to PQ Rec.2020 in [0, 1].

    `diffuse_white_nits` sets how bright linear 1.0 (SDR white) becomes on the HDR
    display; the highlights the model produces above 1.0 scale up from there.
    """
    lin = hdr_linear.float().clamp(min=0.0)
    matrix = torch.tensor(_REC709_TO_REC2020, dtype=lin.dtype, device=lin.device)
    lin2020 = torch.matmul(lin, matrix.transpose(0, 1)).clamp(min=0.0)
    return _pq_oetf(lin2020 * diffuse_white_nits)
