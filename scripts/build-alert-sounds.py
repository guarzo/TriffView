#!/usr/bin/env python3
"""Build the shipped alert sounds in native/Assets/sounds from their sources.

Run with no arguments to regenerate all four .wav assets in place:

    python3 scripts/build-alert-sounds.py

This replaces the earlier generate-alert-sounds.py, which synthesised chime,
bell and pulse from sine partials. That set was measured against notification
sounds people actually like and lost on every axis that matters (see "What the
measurements said" below), so the sounds are now trimmed and level-matched from
CC BY source recordings in assets-src/alert-sounds rather than synthesised.

The sources are committed alongside the outputs, for the same reason the
generator used to be: so the shipped assets can be rebuilt and retuned
deliberately instead of being swapped as opaque binaries. Nothing in the build
runs this script - the .wav files are committed - so the build needs neither
Python nor numpy.

Attribution
-----------
All four sources are from notificationsounds.com under CC BY 4.0, which
requires naming the source, linking back, and stating that the sound was
modified. This script IS the modification, and THIRD_PARTY_NOTICES.md carries
the credit. Adding a sound here means adding it there too.

What the measurements said
--------------------------
The retired synthesised set was built on two premises that the reference
sounds contradict outright:

* "2-5 kHz is the painful band, so avoid it." Sounds people like put up to 74%
  of their energy there. What separates them is that they are 0.1-0.3 s long
  and decay to -20 dB within 4 ms. Brief energy in that band is fine;
  *sustained* energy in it is not, and the old set conflated the two.
* "A 10-15 ms attack avoids the click that reads as urgency." Nothing in the
  reference set attacks slower than 4.4 ms and the sharp ones are under 1.1 ms.
  The slow attacks made the old sounds mushy without making them less annoying.

The real discriminator was crest factor and decay: the synthesised set held a
near-constant level for hundreds of milliseconds (crest 11.6-16.6 dB, decay to
-20 dB taking 0.39-1.15 s), while the references spike and vanish (crest
16.7-27.2 dB, most decaying in under 0.3 s). A sound that sustains is what
nags, at any pitch.
"""

from __future__ import annotations

import math
import struct
import wave
from pathlib import Path

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parent.parent
SOURCE_DIR = ROOT / "assets-src" / "alert-sounds"
OUTPUT_DIR = ROOT / "native" / "Assets" / "sounds"

# Peak, not loudness, is what these are matched on. All eight sounds surveyed
# peak between -0.1 and -2.6 dBFS while spanning 20.5 dB of integrated
# loudness, so peak-normalisation is the convention this kind of sound is
# mastered to - and it is the only choice that survives the arithmetic. A sound
# with a 1 ms transient has a crest factor near 27 dB; forcing it to the same
# integrated loudness as a sustained chime would need ~7 dB of limiting applied
# to exactly the transient that makes it worth having. The set is therefore
# matched at the peak and ordered by presence instead - see SOUNDS.
#
# -3 rather than the -1 the sources use: the previous synthesised set sat at
# -20 dBFS whole-file RMS, and people already have the in-app volume slider set
# for that. -1 would make every alert audibly louder than before at the same
# slider position, which is a poor surprise to ship to people who complained
# that alerts were irritating.
TARGET_PEAK_DBFS = -3.0

# Loudness is integrated over this window because the ear does the same: a 0.1 s
# tick and a 1 s chime at equal RMS are not equally loud.
LOUDNESS_WINDOW_SECONDS = 0.200

# Everything below this, relative to the file's peak, is treated as lead-in or
# room tail and trimmed off.
SILENCE_FLOOR_DB = -60.0

# Guards against a click at either end: the waveform is forced to start and
# finish at zero regardless of where the source was cut.
EDGE_FADE_SECONDS = 0.005


class Sound:
    """One shipped sound: which source it comes from and how it is cut.

    `sound_id` must match the id used in all three places that already have to
    agree - ALERT_SOUND_OPTIONS in app/src/tools/TriffViewSettings.jsx,
    NormalizeSound in native/TriffAlerts/TriffAlertsService.cs, and
    SoundResourceUri in native/TriffAudio/AlertSoundPlayer.cs. An id that is
    missing from any of those fails silently: it appears in the dropdown and
    then never plays.
    """

    def __init__(self, sound_id: str, source: str, role: str, end: float | None = None):
        self.sound_id = sound_id
        self.source = source
        self.role = role
        # Hard cut, in seconds from the start of the trimmed source, or None to
        # keep the whole thing. Only needed for a source whose tail outlasts
        # what an alert should occupy.
        self.end = end


# Ordered quietest to most present. That ordering is the point: because these
# are peak-matched rather than loudness-matched (see TARGET_PEAK_DBFS), a
# sound's perceived level follows its duration and crest factor, so the four
# form a usable range from "barely there" to "hard to miss" instead of four
# things at one volume. Someone who finds their current choice irritating can
# move down the list rather than switching alerts off.
SOUNDS = [
    Sound("bell", "sly-user-interface-sound", "subtlest - a short, quiet, bright tick"),
    Sound("ding", "come-here-notification", "sharp and brief, a little more present than bell"),
    Sound("chime", "that-was-quick-606", "everyday alerts - musical, mid-range, unhurried"),
    Sound("pulse", "no-problem-notification-sound", "splash alerts - the most noticeable of the four"),
]


def _load_mono(path: Path) -> tuple[np.ndarray, int]:
    samples, rate = sf.read(str(path), always_2d=True)
    # Every current source is already mono; averaging is a guard for a future
    # stereo source rather than something that fires today.
    return samples.mean(axis=1), rate


def _envelope(samples: np.ndarray, rate: int, window: float = 0.005) -> np.ndarray:
    width = max(int(round(window * rate)), 1)
    return np.sqrt(np.convolve(samples**2, np.ones(width) / width, mode="same"))


def _trim(samples: np.ndarray, rate: int) -> np.ndarray:
    """Drop lead-in silence and the inaudible tail.

    Lossy sources routinely carry 100-200 ms of encoder lead-in, which would
    otherwise become latency between the alert firing and the user hearing it.
    """
    env = _envelope(samples, rate)
    floor = env.max() * 10 ** (SILENCE_FLOOR_DB / 20)
    audible = np.where(env > floor)[0]
    if len(audible) == 0:
        raise ValueError("source is silent")
    return samples[audible[0] : audible[-1] + 1]


def _loudness(samples: np.ndarray, rate: int) -> float:
    """Loudest 200 ms of the sound, as linear RMS.

    Whole-file RMS is the wrong target for a set that mixes a 0.12 s tick with
    a 1.14 s two-gesture tone. Normalising both to the same whole-file RMS
    makes the tick far too loud, because its RMS is computed over only the
    window where it is actually sounding, while the longer sound averages its
    own decay tail in. Integrating over a fixed window instead means a short
    sound is measured including the silence around it, which is much closer to
    how loud it actually seems.
    """
    width = int(round(LOUDNESS_WINDOW_SECONDS * rate))
    if len(samples) <= width:
        # Shorter than the window: pad with the silence the ear would hear.
        padded = np.zeros(width)
        padded[: len(samples)] = samples
        return float(np.sqrt(np.mean(padded**2)))
    power = np.convolve(samples**2, np.ones(width) / width, mode="valid")
    return float(np.sqrt(power.max()))


def _normalise(samples: np.ndarray, rate: int) -> np.ndarray:
    peak = float(np.abs(samples).max())
    if peak <= 0:
        raise ValueError("refusing to normalise a silent buffer")

    samples = samples * (10 ** (TARGET_PEAK_DBFS / 20) / peak)

    fade = max(int(round(EDGE_FADE_SECONDS * rate)), 1)
    samples = samples.copy()
    samples[:fade] *= np.linspace(0.0, 1.0, fade)
    samples[-fade:] *= np.linspace(1.0, 0.0, fade)
    return samples


def _write_wav(path: Path, samples: np.ndarray, rate: int) -> None:
    # Rounded rather than truncated, and clipped before the cast: numpy wraps
    # rather than saturating on int16 overflow, which would turn a peak into a
    # full-scale spike of the opposite sign.
    pcm = np.clip(np.round(samples * 32767.0), -32768, 32767).astype(np.int16)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(struct.pack(f"<{len(pcm)}h", *pcm))


def _db(value: float) -> float:
    return 20 * math.log10(max(value, 1e-12))


def _describe(sound: Sound, samples: np.ndarray, rate: int) -> str:
    """Report the properties the redesign was actually aiming at.

    Duration, crest factor and decay are here because they are what separated
    the liked references from the retired synthesised set; the 2-5 kHz share is
    here because the previous design treated it as the thing to minimise and
    the measurements say it is not.
    """
    peak = float(np.abs(samples).max())
    loudness = _loudness(samples, rate)

    env = _envelope(samples, rate)
    apex = int(np.argmax(env))
    target = env[apex] * 10 ** (-20 / 20)
    decay_end = apex
    while decay_end < len(env) and env[decay_end] > target:
        decay_end += 1
    decay = (decay_end - apex) / rate

    spectrum = np.abs(np.fft.rfft(samples * np.hanning(len(samples)))) ** 2
    freqs = np.fft.rfftfreq(len(samples), 1 / rate)
    mid_high = spectrum[(freqs >= 2000) & (freqs < 5000)].sum() / spectrum.sum()

    flag = ""
    return (
        f"{sound.sound_id:>6}.wav  {len(samples) / rate:4.2f}s  "
        f"loudness {_db(loudness):6.1f} dBFS  peak {_db(peak):5.1f}  "
        f"crest {_db(peak) - _db(loudness):4.1f} dB  "
        f"decay(-20dB) {decay:5.3f}s  2-5kHz {mid_high * 100:4.1f}%{flag}"
    )


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for sound in SOUNDS:
        source = SOURCE_DIR / f"{sound.source}.ogg"
        samples, rate = _load_mono(source)
        samples = _trim(samples, rate)
        if sound.end is not None:
            samples = samples[: int(round(sound.end * rate))]
        samples = _normalise(samples, rate)
        _write_wav(OUTPUT_DIR / f"{sound.sound_id}.wav", samples, rate)
        print(_describe(sound, samples, rate))


if __name__ == "__main__":
    main()
