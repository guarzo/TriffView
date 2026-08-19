#!/usr/bin/env python3
"""Synthesise the soft alert sounds shipped in native/Assets/sounds.

Run with no arguments to regenerate chime.wav, bell.wav and pulse.wav in place:

    python3 scripts/generate-alert-sounds.py

The four older sounds (alarm, woop, siren, ding) are not generated here - they
came from upstream as .ogg assets and are only converted, not synthesised.

These three are committed as .wav rather than produced at build time, so the
build needs neither Python nor numpy; this script exists so they can be retuned
deliberately instead of swapped as opaque binaries. Every parameter that shapes
a sound is a named constant below.

Why they sound the way they do
------------------------------
The older sounds peak between 2 kHz and 5 kHz, which is where the ear is most
sensitive and where a repeated alert becomes painful fastest. These sit at
600-900 Hz instead - low enough not to pierce, high enough to stay clear of
EVE's own low-frequency ambience. Attacks are slow enough (10-15 ms) to avoid
the click that reads as urgency, and each sound decays smoothly to silence
rather than being cut off.
"""

from __future__ import annotations

import math
import struct
import wave
from pathlib import Path

import numpy as np

SAMPLE_RATE = 48_000

# Loudness targets. The existing four sounds span roughly 9 dB of RMS
# (siren/woop at about -11 dBFS against alarm/ding at about -18), so one master
# volume setting cannot suit all of them. These three are matched to the quieter
# end of that range, which is also simply a more reasonable level for a sound
# that fires unprompted while the app is in the background.
TARGET_RMS_DBFS = -20.0

# Applied only if RMS normalisation pushes the peak this high. Decaying tones
# have a large crest factor, so this is a guard against clipping on a shape
# change, not something that normally binds.
PEAK_CEILING_DBFS = -3.0

# Guards against a click at either end: the waveform is forced to start and
# finish at zero regardless of where the synthesis left it.
EDGE_FADE_SECONDS = 0.005

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "native" / "Assets" / "sounds"


def _time(duration: float) -> np.ndarray:
    return np.arange(int(round(duration * SAMPLE_RATE)), dtype=np.float64) / SAMPLE_RATE


def _envelope(t: np.ndarray, attack: float, decay: float) -> np.ndarray:
    """Linear attack into an exponential decay.

    The attack is what separates these from the existing set as much as the
    pitch does: an instantaneous onset is heard as a click and reads as alarm,
    while 10-15 ms of ramp is heard as a struck note.

    `decay` is the time constant, so the tail is still faintly audible well past
    it; each sound's length is set to give that tail room to fall to silence.
    """
    attack_samples = max(int(round(attack * SAMPLE_RATE)), 1)
    env = np.exp(-t / decay)
    ramp = np.minimum(np.arange(len(t), dtype=np.float64) / attack_samples, 1.0)
    return env * ramp


def _partial(duration: float, freq: float, amp: float, attack: float, decay: float) -> np.ndarray:
    t = _time(duration)
    return amp * np.sin(2 * math.pi * freq * t) * _envelope(t, attack, decay)


def _place(canvas: np.ndarray, offset: float, signal: np.ndarray) -> None:
    """Mix `signal` into `canvas` starting at `offset` seconds, truncating at the end."""
    start = int(round(offset * SAMPLE_RATE))
    end = min(start + len(signal), len(canvas))
    canvas[start:end] += signal[: end - start]


def make_chime() -> np.ndarray:
    """Two soft notes, A5 down to E5.

    Descending rather than ascending on purpose: a falling interval is heard as
    a statement ("that happened"), a rising one as a question or a summons. This
    is the everyday option, so it should not feel like it is asking for
    anything. The notes overlap slightly so the pair reads as one gesture.
    """
    duration = 0.90
    out = np.zeros(int(round(duration * SAMPLE_RATE)))

    for offset, freq in ((0.00, 880.00), (0.22, 659.25)):
        # Second harmonic well down, purely to keep a bare sine from sounding
        # synthetic. Anything stronger starts to buzz.
        _place(out, offset, _partial(0.60, freq, 1.000, 0.015, 0.180))
        _place(out, offset, _partial(0.60, freq * 2, 0.125, 0.015, 0.120))

    return out


def make_bell() -> np.ndarray:
    """A single warm struck bell - the least intrusive of the three.

    Bell partials are inharmonic (not integer multiples of the fundamental),
    which is what makes a bell sound struck rather than blown. The ratios below
    are a rounded, deliberately mild version of a real bell's; higher partials
    decay faster, as they do physically, so the sound warms as it fades instead
    of staying bright.
    """
    duration = 1.60
    out = np.zeros(int(round(duration * SAMPLE_RATE)))

    fundamental = 700.0
    for ratio, amp, decay in (
        (1.00, 1.000, 0.520),
        (2.00, 0.320, 0.330),
        (2.76, 0.180, 0.210),
        (5.40, 0.060, 0.110),
    ):
        _place(out, 0.0, _partial(duration, fundamental * ratio, amp, 0.010, decay))

    return out


def make_pulse() -> np.ndarray:
    """Two quick low-mid tones - the most attention-getting of the three.

    Intended for splash alerts, where something is actually happening. Two
    events carry urgency that one does not, but the gap is wide enough (180 ms)
    to read as two distinct taps rather than the fast warble that makes the
    existing 'woop' grating - that one has 30 amplitude bursts in 0.7 s.
    """
    duration = 0.70
    out = np.zeros(int(round(duration * SAMPLE_RATE)))

    for offset in (0.00, 0.18):
        _place(out, offset, _partial(0.40, 600.0, 1.000, 0.010, 0.090))
        _place(out, offset, _partial(0.40, 1200.0, 0.180, 0.010, 0.070))

    return out


def _normalise(samples: np.ndarray) -> np.ndarray:
    rms = float(np.sqrt(np.mean(samples**2)))
    if rms <= 0:
        raise ValueError("refusing to normalise a silent buffer")

    samples = samples * (10 ** (TARGET_RMS_DBFS / 20) / rms)

    peak = float(np.abs(samples).max())
    ceiling = 10 ** (PEAK_CEILING_DBFS / 20)
    if peak > ceiling:
        samples = samples * (ceiling / peak)

    fade = max(int(round(EDGE_FADE_SECONDS * SAMPLE_RATE)), 1)
    samples[:fade] *= np.linspace(0.0, 1.0, fade)
    samples[-fade:] *= np.linspace(1.0, 0.0, fade)
    return samples


def _write_wav(path: Path, samples: np.ndarray) -> None:
    # Rounded rather than truncated, and clipped before the cast: numpy wraps
    # rather than saturating on int16 overflow, which would turn a peak into a
    # full-scale spike of the opposite sign.
    pcm = np.clip(np.round(samples * 32767.0), -32768, 32767).astype(np.int16)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes(struct.pack(f"<{len(pcm)}h", *pcm))


def _describe(name: str, samples: np.ndarray) -> str:
    rms = float(np.sqrt(np.mean(samples**2)))
    peak = float(np.abs(samples).max())
    spectrum = np.abs(np.fft.rfft(samples * np.hanning(len(samples))))
    freqs = np.fft.rfftfreq(len(samples), 1 / SAMPLE_RATE)
    centroid = float((spectrum * freqs).sum() / spectrum.sum())
    return (
        f"{name:>6}.wav  {len(samples) / SAMPLE_RATE:4.2f}s  "
        f"rms={20 * math.log10(rms):6.1f} dBFS  "
        f"peak={20 * math.log10(peak):5.1f} dBFS  "
        f"centroid={centroid:5.0f} Hz"
    )


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for name, build in (("chime", make_chime), ("bell", make_bell), ("pulse", make_pulse)):
        samples = _normalise(build())
        _write_wav(OUTPUT_DIR / f"{name}.wav", samples)
        print(_describe(name, samples))


if __name__ == "__main__":
    main()
