# Wormhole Splash Audio Alerts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Alert the user when a wormhole is splashed near any of their EVE clients, by capturing each client's audio and detecting the wormhole activation sound.

**Architecture:** A new `native/TriffAudio/` subsystem captures per-EVE-process audio via WASAPI process loopback into a 30-second ring buffer, scores 2-second windows against splash templates with normalised cross-correlation over 32 log-spaced spectral bands, and routes detections through a single new public entry point on the existing `TriffAlertsService` so cooldown, history, preview flash and tray notification are shared rather than duplicated.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WASAPI process loopback via hand-rolled COM interop, React + Vite for the settings UI, xunit for tests.

**Spec:** `docs/superpowers/specs/2026-08-19-wormhole-splash-audio-alerts-design.md`

## Global Constraints

- **Windows-only.** The project cannot be built or run from WSL. Build via `powershell.exe`. From this worktree the repo scripts do **not** work (`build-native.ps1:4` resolves the root one level up); invoke the SDK explicitly — see "Building from this worktree" below.
- **Detector constants are fixed by measurement.** `sampleRate=16000`, `nperseg=1024`, `hop=85`, `bands=32` over `geomspace(80, 7600)`, `window=2.0s (376 frames)`, `context=30s`, `madFloor=1e-3`, `defaultThreshold=0.35`. Changing any of these invalidates the 11 shipped templates.
- **`SplashDetector.cs` and `SplashTemplate.cs` must stay Windows-free** — no `System.Windows.Forms`, no WPF, no P/Invoke, no `TriffViewSubsystem` types. `System.Drawing.Primitives`, `System.Numerics`, `System.Text.Json` and `System.IO` are fine. They are linked into `tests/TriffView.Tests` by `<Compile Include>`.
- **Event type string `wormhole_splash` is permanent.** `TriffAlertsSettings.Normalize()` never prunes unknown `Events` keys, so a rename leaves permanent residue in every user's settings file.
- **Web message `type` strings must be unique across all subsystems** — the dispatch chain is first-handler-wins.
- **Never validate a preview position against `Screen.AllScreens` bounds** (CLAUDE.md).
- Fork-only work. Do not target `fix/*` or upstream.

### Building from this worktree

```powershell
powershell.exe -NoProfile -Command "
$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
$env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
$env:APPDATA='C:\dev\TriffView\.appdata'
$env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\TriffView\.claude\worktrees\splash-audio-alerts\native\TriffView.csproj' -c Release
"
```

Swap `build` for `test` and point at the test csproj to run tests. **Do not do runtime file work in the same invocation** — the `APPDATA` override redirects it.

---

### Task 1: Pure spectral feature extraction

**Files:**
- Create: `native/TriffAudio/SplashFeatures.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj` (add `<Compile Include>`)
- Test: `tests/TriffView.Tests/SplashFeaturesTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  namespace TriffView.Audio;
  public static class SplashFeatures
  {
      public const int SampleRate = 16000;
      public const int FftSize = 1024;
      public const int HopSize = 85;
      public const int BandCount = 32;
      public const double BandMinHz = 80.0;
      public const double BandMaxHz = 7600.0;
      public const int WindowFrames = 376;          // 2.0 s
      public const int ContextFrames = 5647;        // 30 s
      public const double MadFloor = 1e-3;

      public static float[,] ComputeBands(ReadOnlySpan<float> samples);           // [BandCount, frames]
      public static void ComputeContextStats(float[,] bands, int endFrame, out float[] median, out float[] mad);
      public static float[] BuildPatch(float[,] bands, int startFrame, float[] median, float[] mad);
  }
  ```

- [ ] **Step 1: Write the failing test**

```csharp
using TriffView.Audio;
using Xunit;

public class SplashFeaturesTests
{
    [Fact]
    public void ComputeBands_ReturnsExpectedShape()
    {
        var samples = new float[SplashFeatures.SampleRate];      // 1 s of silence
        var bands = SplashFeatures.ComputeBands(samples);
        Assert.Equal(SplashFeatures.BandCount, bands.GetLength(0));
        Assert.InRange(bands.GetLength(1), 180, 200);            // ~188 frames at hop 85
    }

    [Fact]
    public void ComputeBands_PutsToneEnergyInTheRightBand()
    {
        var samples = new float[SplashFeatures.SampleRate];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 1000.0 * i / SplashFeatures.SampleRate);
        var bands = SplashFeatures.ComputeBands(samples);

        // band index containing 1000 Hz, using the same geometric spacing
        var ratio = Math.Log(1000.0 / SplashFeatures.BandMinHz)
                  / Math.Log(SplashFeatures.BandMaxHz / SplashFeatures.BandMinHz);
        var expected = (int)(ratio * SplashFeatures.BandCount);

        var mid = bands.GetLength(1) / 2;
        var loudest = 0;
        for (var b = 1; b < SplashFeatures.BandCount; b++)
            if (bands[b, mid] > bands[loudest, mid]) loudest = b;
        Assert.InRange(loudest, expected - 1, expected + 1);
    }

    [Fact]
    public void BuildPatch_IsZeroMeanUnitNorm()
    {
        var rng = new Random(1);
        var samples = new float[SplashFeatures.SampleRate * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() - 0.5);
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);
        var patch = SplashFeatures.BuildPatch(bands, 0, med, mad);

        Assert.Equal(SplashFeatures.BandCount * SplashFeatures.WindowFrames, patch.Length);
        Assert.Equal(0.0, patch.Sum(), 3);
        Assert.Equal(1.0, Math.Sqrt(patch.Sum(v => (double)v * v)), 3);
    }

    [Fact]
    public void ComputeContextStats_FloorsMadForEmptyBands()
    {
        var bands = new float[SplashFeatures.BandCount, 100];   // all identical -> MAD would be 0
        SplashFeatures.ComputeContextStats(bands, 100, out _, out var mad);
        Assert.All(mad, m => Assert.True(m >= SplashFeatures.MadFloor));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run the test csproj per "Building from this worktree", `--filter FullyQualifiedName~SplashFeaturesTests`.
Expected: FAIL — `SplashFeatures` does not exist.

- [ ] **Step 3: Implement `SplashFeatures`**

Real-input FFT (implement a radix-2 Cooley-Tukey in the file; no dependency), Hann window, magnitude, geometric band edges, `20*log10(mean + 1e-10)`. `ComputeContextStats` takes the trailing `ContextFrames` ending at `endFrame`, computes per-band median and median-absolute-deviation, and floors MAD at `MadFloor`. `BuildPatch` computes `(band - median) / mad` over `WindowFrames` starting at `startFrame`, flattens row-major, subtracts the mean, and divides by the L2 norm (returning the zero vector if the norm is below 1e-9).

- [ ] **Step 4: Run tests to verify they pass**

Expected: PASS, all four.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAudio/SplashFeatures.cs tests/TriffView.Tests/SplashFeaturesTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "feat(audio): pure spectral feature extraction for splash detection"
```

---

### Task 2: Template model and on-disk format

**Files:**
- Create: `native/TriffAudio/SplashTemplate.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj`
- Test: `tests/TriffView.Tests/SplashTemplateTests.cs`

**Interfaces:**
- Consumes: `SplashFeatures`
- Produces:
  ```csharp
  namespace TriffView.Audio;
  public sealed class SplashTemplate
  {
      public string Id { get; init; }           // filename stem, e.g. "splash-01"
      public string Name { get; init; }
      public bool BuiltIn { get; init; }
      public float[] Patch { get; init; }       // normalised, BandCount*WindowFrames

      public static SplashTemplate? FromWav(string id, string name, bool builtIn,
                                            ReadOnlySpan<byte> wav, ReadOnlySpan<byte> statsJson);
      public static byte[] WriteWav(ReadOnlySpan<float> samples, out double gain);  // 16 kHz mono 16-bit
      public static byte[] WriteStatsJson(double gain, float[] median, float[] mad);
      public static float[] ReadWavMono16(ReadOnlySpan<byte> wav);         // returns -1..1 samples
      // NOTE: callers MUST divide samples by the stats file's `gain` before feature
      // extraction. Omitting it was measured to destroy class separation entirely.
  }
  ```

- [ ] **Step 1: Write the failing test**

```csharp
using TriffView.Audio;
using Xunit;

public class SplashTemplateTests
{
    private static float[] Ramp(int n)
    {
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)Math.Sin(2 * Math.PI * 440.0 * i / 16000.0);
        return s;
    }

    [Fact]
    public void WavRoundTripsWithinQuantisationError()
    {
        var src = Ramp(16000);
        var wav = SplashTemplate.WriteWav(src, out var gain);
        var round = SplashTemplate.ReadWavMono16(wav);
        Assert.Equal(src.Length, round.Length);
        for (var i = 0; i < src.Length; i++)
            Assert.InRange(round[i] / gain - src[i], -0.001f, 0.001f);   // undo the write gain
    }

    [Fact]
    public void FromWav_ProducesNormalisedPatch()
    {
        var samples = Ramp(32000);                               // exactly 2 s
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);

        var wav = SplashTemplate.WriteWav(samples, out var gain);
        var t = SplashTemplate.FromWav("splash-01", "test", true,
                    wav, SplashTemplate.WriteStatsJson(gain, med, mad));

        Assert.NotNull(t);
        Assert.Equal(SplashFeatures.BandCount * SplashFeatures.WindowFrames, t!.Patch.Length);
        Assert.Equal(1.0, Math.Sqrt(t.Patch.Sum(v => (double)v * v)), 3);
    }

    [Fact]
    public void FromWav_ReturnsNullForUnknownVersion()
    {
        var samples = Ramp(32000);
        var bad = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":99,\"sampleRate\":16000,\"gain\":1.0,\"median\":[],\"mad\":[]}");
        Assert.Null(SplashTemplate.FromWav("x", "x", false,
            SplashTemplate.WriteWav(samples, out _), bad));
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `SplashTemplate` does not exist.

- [ ] **Step 3: Implement.** Parse RIFF chunks properly (do not assume the `data` chunk is at a fixed offset — walk the chunk list). Reject anything that is not 16 kHz mono 16-bit PCM. Reject `version != 1`, a missing or zero `gain`, or `median`/`mad` arrays whose length is not `BandCount`, by returning `null` — never throwing.

`FromWav` **must divide the decoded samples by `gain`** before computing bands. Clips are peak-normalised on write so quiet ones survive 16-bit quantisation; skipping the division was measured to destroy class separation entirely (quiet negatives get amplified ~200x and score as high as real splashes).

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAudio/SplashTemplate.cs tests/TriffView.Tests/SplashTemplateTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "feat(audio): splash template model and on-disk format"
```

---

### Task 3: The detector, verified against the real shipped templates

**Files:**
- Create: `native/TriffAudio/SplashDetector.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj`
- Test: `tests/TriffView.Tests/SplashDetectorTests.cs`

**Interfaces:**
- Consumes: `SplashFeatures`, `SplashTemplate`
- Produces:
  ```csharp
  namespace TriffView.Audio;
  public sealed class SplashDetector
  {
      public SplashDetector(IReadOnlyList<SplashTemplate> templates);
      public int TemplateCount { get; }
      public double Score(ReadOnlySpan<float> samples);                    // derives context from the buffer
      public double Score(ReadOnlySpan<float> samples, float[] median, float[] mad);  // caller-supplied context
      public IReadOnlyList<(double Score, double OffsetSeconds)> RankWindows(
          ReadOnlySpan<float> samples, int maxResults, double minSeparationSeconds);
  }
  ```

`RankWindows` is what the template-capture UI uses; it must **not** apply any threshold.

- [ ] **Step 1: Write the failing test**

```csharp
using TriffView.Audio;
using Xunit;

public class SplashDetectorTests
{
    [Fact]
    public void ScoresAShippedTemplateAgainstItselfNearOne()
    {
        var t = TestTemplates.LoadShipped();
        var det = new SplashDetector(t);
        Assert.Equal(11, det.TemplateCount);

        var samples = TestTemplates.LoadShippedSamples(0);
        Assert.True(det.Score(samples) > 0.95, "a template must match itself");
    }

    [Fact]
    public void ScoresSilenceFarBelowThreshold()
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        Assert.True(det.Score(new float[SplashFeatures.SampleRate * 3]) < 0.35);
    }

    [Fact]
    public void RankWindowsReturnsSeparatedCandidatesWithoutThresholding()
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        var rng = new Random(7);
        var buf = new float[SplashFeatures.SampleRate * 30];
        for (var i = 0; i < buf.Length; i++) buf[i] = (float)(rng.NextDouble() - 0.5) * 0.01f;

        var top = det.RankWindows(buf, 3, 2.0);
        Assert.Equal(3, top.Count);                                   // never thresholded
        for (var i = 1; i < top.Count; i++)
            Assert.True(Math.Abs(top[i].OffsetSeconds - top[i - 1].OffsetSeconds) >= 2.0);
        Assert.True(top[0].Score >= top[1].Score);
    }
}
```

`TestTemplates` is a small helper in the test project that reads the committed
files under `native/TriffAudio/Assets/splash-templates/` from disk.

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `SplashDetector` does not exist.

- [ ] **Step 3: Implement.** Score is the max dot product of the query patch against every template patch. Compute context stats once per call over the trailing 30 s and reuse across windows. `RankWindows` evaluates every 250 ms, sorts descending, and greedily takes candidates at least `minSeparationSeconds` apart.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAudio/SplashDetector.cs tests/TriffView.Tests/SplashDetectorTests.cs tests/TriffView.Tests/TestTemplates.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "feat(audio): splash detector with template matching"
```

---

### Task 4: Accuracy regression test

**Files:**
- Create: `tests/TriffView.Tests/SplashAccuracyTests.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj` (copy fixtures to the output directory)

**Interfaces:**
- Consumes: `SplashDetector`
- Produces: nothing

This locks in the measured accuracy so a future change to the DSP cannot silently degrade it.

The fixtures are **already committed** at `tests/TriffView.Tests/fixtures/splash-accuracy/` — eight 3-second clips (four confirmed splashes, four confirmed non-splashes) at 16 kHz mono 16-bit, each with a `.json` carrying `gain`, `median` and `mad`. Every label was confirmed by the user listening to the clip. Verified scores: splashes 0.62-0.71, non-splashes 0.06-0.30.

A 3-second clip has no 30 s context of its own, so the fixture's stored statistics **must** be used rather than recomputed from the clip. Recomputing collapses the separation — measured.

- [ ] **Step 1: Make the fixtures reachable from the test binary**

Tests run from the output directory, which does not contain the fixtures unless the csproj copies them. Add to `tests/TriffView.Tests/TriffView.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Without this the test throws `FileNotFoundException` rather than failing meaningfully.

- [ ] **Step 2: Write the test**

```csharp
using System.Text.Json;
using TriffView.Audio;
using Xunit;

public class SplashAccuracyTests
{
    [Theory]
    [InlineData("splash-a.wav", true)]
    [InlineData("splash-b.wav", true)]
    [InlineData("splash-c.wav", true)]
    [InlineData("splash-d.wav", true)]
    [InlineData("warp-a.wav", false)]
    [InlineData("warp-b.wav", false)]
    [InlineData("weapons-a.wav", false)]
    [InlineData("ambient-a.wav", false)]
    public void ClassifiesFixturesCorrectlyAtDefaultThreshold(string file, bool isSplash)
    {
        var det = new SplashDetector(TestTemplates.LoadShipped());
        var dir = Path.Combine("fixtures", "splash-accuracy");

        // A 3 s fixture has no 30 s context of its own. Its stored statistics MUST be
        // used; recomputing them from the clip collapses the separation (measured).
        using var meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, Path.ChangeExtension(file, ".json"))));
        var root = meta.RootElement;
        var gain = root.GetProperty("gain").GetDouble();
        var median = root.GetProperty("median").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var mad = root.GetProperty("mad").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();

        var samples = SplashTemplate.ReadWavMono16(File.ReadAllBytes(Path.Combine(dir, file)));
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(samples[i] / gain);

        var score = det.Score(samples, median, mad);
        if (isSplash) Assert.True(score >= 0.35, $"{file} scored {score:F3}, expected >= 0.35");
        else          Assert.True(score <  0.35, $"{file} scored {score:F3}, expected < 0.35");
    }
}
```

- [ ] **Step 3: Run tests.** Expected: PASS, all eight. Reference scores from the validated implementation, for comparison if a case fails: splashes 0.62, 0.69, 0.69, 0.71; non-splashes 0.06, 0.15, 0.19, 0.30.

If a fixture fails, do **not** move the threshold to make it pass — report it. It means the C# port differs from the validated implementation, and the threshold is the one thing that must not absorb that difference.

- [ ] **Step 4: Commit**

```bash
git add tests/TriffView.Tests/SplashAccuracyTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "test(audio): accuracy regression test against committed fixtures"
```

---

### Task 5: WASAPI process loopback capture

**Files:**
- Create: `native/TriffAudio/WasapiProcessCapture.cs`
- Create: `native/TriffAudio/AudioRingBuffer.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj` (ring buffer only — the capture class is Windows-only and is **not** linked)
- Test: `tests/TriffView.Tests/AudioRingBufferTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  namespace TriffView.Audio;

  public sealed class AudioRingBuffer                      // PURE, testable
  {
      public AudioRingBuffer(int capacitySamples);
      public void Write(ReadOnlySpan<float> samples);
      public int Read(Span<float> destination);            // most recent N samples, oldest first
      public long TotalWritten { get; }
      public long NonZeroSamplesInLastWrite { get; }
  }

  internal sealed class WasapiProcessCapture : IDisposable  // Windows-only
  {
      // ReadOnlySpan<T> is a ref struct and cannot be a generic type argument on
      // .NET 8, so Action<ReadOnlySpan<float>> does not compile. Use a custom delegate.
      public delegate void SampleCallback(ReadOnlySpan<float> samples);
      public WasapiProcessCapture(uint processId, SampleCallback onSamples);
      public bool Start(out string? error);                 // false on failure; never throws
      public DateTime LastPacketUtc { get; }
      public void Dispose();
  }
  ```

`WasapiProcessCapture` resamples the device mix format down to 16 kHz mono before
invoking the callback, so nothing downstream deals with device formats.

- [ ] **Step 1: Write the failing ring buffer test**

```csharp
using TriffView.Audio;
using Xunit;

public class AudioRingBufferTests
{
    [Fact]
    public void ReadsMostRecentSamplesOldestFirst()
    {
        var rb = new AudioRingBuffer(4);
        rb.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        var dst = new float[4];
        Assert.Equal(4, rb.Read(dst));
        Assert.Equal(new float[] { 3, 4, 5, 6 }, dst);
    }

    [Fact]
    public void ReadsFewerWhenNotYetFull()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 1, 2, 3 });
        var dst = new float[8];
        Assert.Equal(3, rb.Read(dst));
    }

    [Fact]
    public void TracksNonZeroSamplesForSilenceDetection()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 0, 0, 0, 0 });
        Assert.Equal(0, rb.NonZeroSamplesInLastWrite);
        rb.Write(new float[] { 0, 0.5f, 0, 0 });
        Assert.Equal(1, rb.NonZeroSamplesInLastWrite);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `AudioRingBuffer` does not exist.

- [ ] **Step 3: Implement `AudioRingBuffer`,** then `WasapiProcessCapture`.

The interop is already proven working against six live EVE clients; reproduce it exactly. Non-obvious points, each confirmed by measurement during the spike — do not "simplify" them away:
- Activate with `ActivateAudioInterfaceAsync` on device path `VAD\Process_Loopback`, passing `AUDIOCLIENT_ACTIVATION_PARAMS` inside a `VT_BLOB` (`0x0041`) `PROPVARIANT`, with `ActivationType=1`, `ProcessLoopbackMode=0` (include process tree).
- The virtual device returns a bare `IAudioClient`; querying for `IAudioClient2` gives `E_NOINTERFACE`.
- `Initialize` needs `LOOPBACK|EVENTCALLBACK` (`0x00020000|0x00040000`) and an **explicit** format — `GetMixFormat` and `AutoConvertPcm` are unsupported here. Request 48 kHz stereo float32 and resample.
- The completion handler must also implement `IAgileObject`.
- **Activation succeeding tells you nothing about whether audio will arrive.** A process with no render session streams zeros forever without error.
- A muted process delivers zero-filled buffers with `AUDCLNT_BUFFERFLAGS_SILENT` **never set**. Silence must be detected by counting non-zero samples, not by the flag.

`Start` returns `false` with a message rather than throwing — one client failing must not affect the others.

- [ ] **Step 4: Run ring buffer tests.** Expected: PASS. The capture class has no automated test; it is exercised in Task 10.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAudio/AudioRingBuffer.cs native/TriffAudio/WasapiProcessCapture.cs tests/TriffView.Tests/AudioRingBufferTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "feat(audio): per-process WASAPI loopback capture and ring buffer"
```

---

### Task 6: Template store

**Files:**
- Create: `native/TriffAudio/SplashTemplateStore.cs`
- Modify: `native/TriffView.csproj` (embed the shipped templates)
- Test: `native/TriffView.Tests/SplashTemplateStoreTests.cs`

**Interfaces:**
- Consumes: `SplashTemplate`
- Produces:
  ```csharp
  namespace TriffView.Audio;
  internal sealed class SplashTemplateStore
  {
      public SplashTemplateStore(string userTemplateDirectory);
      public IReadOnlyList<SplashTemplate> Templates { get; }
      public void Reload();
      public string Save(string name, ReadOnlySpan<float> samples, float[] median, float[] mad);  // returns id
      public bool Delete(string id);                       // built-in templates cannot be deleted
  }
  ```

- [ ] **Step 1: Embed the shipped templates**

In `native/TriffView.csproj`, alongside the existing conditional `overlay-dist.zip` entry:

```xml
<EmbeddedResource Include="TriffAudio\Assets\splash-templates\*.wav" />
<EmbeddedResource Include="TriffAudio\Assets\splash-templates\*.json" />
```

- [ ] **Step 2: Write the failing test**

```csharp
using TriffView.Audio;
using Xunit;

public class SplashTemplateStoreTests
{
    [Fact]
    public void LoadsElevenBuiltInTemplates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        Assert.Equal(11, store.Templates.Count(t => t.BuiltIn));
    }

    [Fact]
    public void SavedTemplateSurvivesReloadAndCanBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        var samples = new float[SplashFeatures.SampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 300.0 * i / SplashFeatures.SampleRate);
        var bands = SplashFeatures.ComputeBands(samples);
        SplashFeatures.ComputeContextStats(bands, bands.GetLength(1), out var med, out var mad);

        var id = store.Save("mine", samples, med, mad);
        store.Reload();
        Assert.Contains(store.Templates, t => t.Id == id && !t.BuiltIn);

        Assert.True(store.Delete(id));
        store.Reload();
        Assert.DoesNotContain(store.Templates, t => t.Id == id);
    }

    [Fact]
    public void DeleteRefusesBuiltInTemplates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        var store = new SplashTemplateStore(dir);
        var builtIn = store.Templates.First(t => t.BuiltIn);
        Assert.False(store.Delete(builtIn.Id));
    }

    [Fact]
    public void CorruptUserTemplateIsSkippedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "broken.wav"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(dir, "broken.json"), "not json");
        var store = new SplashTemplateStore(dir);          // must not throw
        Assert.Equal(11, store.Templates.Count);
    }
}
```

- [ ] **Step 3: Run to verify it fails.** Expected: FAIL — `SplashTemplateStore` does not exist.

- [ ] **Step 4: Implement.** Read embedded resources by name prefix; read user templates from the directory, pairing `*.wav` with the matching `*.json`. Skip any pair that fails to load, writing one diagnostics line each. Create the user directory lazily on first save.

- [ ] **Step 5: Run tests.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add native/TriffAudio/SplashTemplateStore.cs native/TriffView.csproj native/TriffView.Tests/SplashTemplateStoreTests.cs
git commit -m "feat(audio): template store with embedded and user templates"
```

---

### Task 7: `RaiseExternalAlert` on TriffAlertsService

**Files:**
- Modify: `native/TriffAlerts/TriffAlertsService.cs` (add the public method; add `wormhole_splash` to `CreateDefaultEvents()` around :79; add settings fields to `TriffAlertsSettings` at :10 and clamps in `Normalize()` at :28)
- Test: `tests/TriffView.Tests/ExternalAlertTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public void RaiseExternalAlert(string type, string characterName, string source, string message);
  // on TriffAlertsSettings:
  public bool SplashDetectionEnabled { get; set; }    // default false
  public double SplashThreshold { get; set; }         // default 0.35, clamped 0.10-0.90
  ```

- [ ] **Step 1: Write the failing test**

```csharp
using TriffView.Alerts;
using Xunit;

public class ExternalAlertTests
{
    private static TriffAlertsService NewService(out TriffAlertsSettings settings)
    {
        settings = new TriffAlertsSettings { Enabled = true };
        settings.Normalize();
        var svc = new TriffAlertsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        svc.UpdateSettings(settings);
        return svc;
    }

    // AlertTriggered is raised from a thread-pool drain (TriffAlertsService.cs:873-885),
    // never synchronously. Every test here must wait rather than assert immediately.
    private static bool Wait(ManualResetEventSlim gate) => gate.Wait(TimeSpan.FromSeconds(5));

    [Fact]
    public void RaisesAndRecordsHistory()
    {
        var svc = NewService(out _);
        TriffAlertEvent? seen = null;
        using var gate = new ManualResetEventSlim();
        svc.AlertTriggered += (_, e) => { seen = e; gate.Set(); };

        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "Wormhole activated");

        Assert.True(Wait(gate), "AlertTriggered did not fire within 5s");
        Assert.NotNull(seen);
        Assert.Equal("wormhole_splash", seen!.Type);
        Assert.Equal("Pilot", seen.CharacterName);
        Assert.Contains(svc.History, a => a.Id == seen.Id);
    }

    [Fact]
    public void AppliesTheSameCooldownAsLogAlerts()
    {
        var svc = NewService(out _);
        var count = 0;
        using var gate = new ManualResetEventSlim();
        svc.AlertTriggered += (_, _) => { Interlocked.Increment(ref count); gate.Set(); };

        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "one");
        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "two");

        Assert.True(Wait(gate), "AlertTriggered did not fire within 5s");
        Thread.Sleep(500);                   // allow a second (incorrect) raise to arrive
        Assert.Equal(1, Volatile.Read(ref count));   // second suppressed by the 15 s cooldown
    }

    [Fact]
    public void DoesNotRaiseWhenTheEventTypeIsDisabled()
    {
        var settings = new TriffAlertsSettings { Enabled = true };
        settings.Normalize();
        settings.Events["wormhole_splash"].Enabled = false;
        var svc = new TriffAlertsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        svc.UpdateSettings(settings);

        var count = 0;
        svc.AlertTriggered += (_, _) => Interlocked.Increment(ref count);
        svc.RaiseExternalAlert("wormhole_splash", "Pilot", "audio", "x");
        Thread.Sleep(500);                   // a disabled event must produce nothing at all
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void SplashThresholdIsClamped()
    {
        var s = new TriffAlertsSettings { SplashThreshold = 5.0 };
        s.Normalize();
        Assert.InRange(s.SplashThreshold, 0.10, 0.90);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `RaiseExternalAlert` does not exist.

- [ ] **Step 3: Implement.** `RaiseExternalAlert` takes `_gate`, calls the existing `EmitLocked` path (do **not** duplicate cooldown or history logic), and dispatches notifications off-lock exactly as the log path does. Add `wormhole_splash` to `CreateDefaultEvents()` with: label "Wormhole splash", enabled `true`, severity `warning`, cooldown 15, flash enabled, tray enabled.

- [ ] **Step 4: Run tests.** Expected: PASS. Also run the existing alerts tests to confirm nothing regressed.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAlerts/TriffAlertsService.cs tests/TriffView.Tests/ExternalAlertTests.cs
git commit -m "feat(alerts): external alert entry point and wormhole_splash event type"
```

---

### Task 8: TriffAudioService

**Files:**
- Create: `native/TriffAudio/TriffAudioService.cs`
- Test: `native/TriffView.Tests/TriffAudioServiceTests.cs`

**Interfaces:**
- Consumes: `WasapiProcessCapture`, `AudioRingBuffer`, `SplashDetector`, `SplashTemplateStore`
- Produces:
  ```csharp
  namespace TriffView.Audio;
  internal sealed record AudioClientStatus(uint ProcessId, string CharacterName, string Status);
  //   Status is one of: "monitoring" | "silent" | "unavailable" | "off"

  internal sealed class TriffAudioService : IDisposable
  {
      public event EventHandler<(string CharacterName, double Score)>? SplashDetected;
      public void UpdateSettings(bool enabled, double threshold);
      public void SetClients(IReadOnlyList<(uint ProcessId, string CharacterName)> clients);
      public IReadOnlyList<AudioClientStatus> Statuses { get; }
      public IReadOnlyList<(double Score, double OffsetSeconds, float[] Samples)> CaptureCandidates(
          uint processId, int maxResults);
      public bool TryGetContextStats(uint processId, out float[] median, out float[] mad);
      public void Dispose();
  }
  ```

- [ ] **Step 1: Write the failing test**

Test the parts that do not need real audio: lifecycle, status transitions, and the no-character-name rule.

```csharp
using TriffView.Audio;
using Xunit;

public class TriffAudioServiceTests
{
    [Fact]
    public void ReportsOffForEveryClientWhenDisabled()
    {
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: false, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.All(svc.Statuses, s => Assert.Equal("off", s.Status));
    }

    [Fact]
    public void DroppingAClientRemovesItsStatus()
    {
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        svc.SetClients(new[] { (1234u, "Pilot"), (5678u, "Other") });
        Assert.Equal(2, svc.Statuses.Count);
        svc.SetClients(new[] { (1234u, "Pilot") });
        Assert.Single(svc.Statuses);
    }

    [Fact]
    public void NeverAlertsForAClientWithNoCharacterName()
    {
        using var svc = new TriffAudioService();
        svc.UpdateSettings(enabled: true, threshold: 0.35);
        var raised = 0;
        svc.SplashDetected += (_, _) => raised++;
        // Deliberately NOT using TestTemplates: that helper lives in tests/TriffView.Tests,
        // which native/TriffView.Tests cannot see (it references only TriffView.csproj).
        // This test is about the no-character-name rule, not about detection quality, so
        // any audio that would score above threshold will do.
        svc.SetClients(new[] { (1234u, "") });          // character-select screen
        svc.ForceDetectionPassForTests(1234u, score: 0.99);
        Assert.Equal(0, raised);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `TriffAudioService` does not exist.

- [ ] **Step 3: Implement.**
  - `SetClients` diffs against current sessions: start capture for new PIDs, dispose sessions for departed ones. Never restart a session for a PID that is already capturing.
  - One 4 Hz detection timer for all clients, not one per client.
  - Status rules: `off` when disabled; `unavailable` when `Start` failed or no packet for 10 s (then tear down and retry on the next `SetClients`); `silent` when capture is alive but no non-zero sample in 60 s; otherwise `monitoring`.
  - Detections are queued and drained with the `Interlocked.CompareExchange` idiom used at `TriffAlertsService.cs:873-893`.
  - `SplashDetected` is raised off any lock, never on the UI thread.
  - Expose `internal void ForceDetectionPassForTests(uint processId, double score)` so the tests above can drive the post-detection decision path (character-name rule, threshold comparison, event raising) without real audio or templates. It must run the same code the real detection loop runs after scoring — not a parallel copy, or the test proves nothing.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAudio/TriffAudioService.cs native/TriffView.Tests/TriffAudioServiceTests.cs
git commit -m "feat(audio): capture lifecycle, detection loop and per-client health"
```

---

### Task 9: Controller wiring, native sound, and state projection

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs` — construct the service near `:85`, wire `SplashDetected`, feed `SetClients` from the client refresh at `:536`, add `audioStatus` to the client projection at `:2514`, handle the new `triffaudio:*` messages in `HandleWebMessage` at `:240`, add `case`s to `ApplyAlertsPatch` at `:1089`
- Create: `native/TriffAudio/AlertSoundPlayer.cs`
- Modify: `native/MainWindow.xaml.cs` — subscribe to the new sound event and play
- Modify: `native/TriffView.csproj` — add the converted WAV sound assets as `<Resource>`
- Create: `native/TriffView/Assets/sounds/{alarm,woop,siren,ding}.wav`
- Modify: `app/src/App.jsx` — remove web-side sound playback (block at `:280-306`, imports at `:3-6`)
- Test: `native/TriffView.Tests/AudioStateProjectionTests.cs`

**Interfaces:**
- Consumes: `TriffAudioService`, `TriffAlertsService.RaiseExternalAlert`
- Produces: `audioStatus` string on each entry of `clients[]` in `triffview:state`; the `triffaudio:*` message handlers listed in the spec.

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

public class AudioStateProjectionTests
{
    [Fact]
    public void ClientStateIncludesAudioStatus()
    {
        var json = TestHarness.BuildClientState(processId: 1234, characterName: "Pilot",
                                                audioStatus: "monitoring");
        Assert.Equal("monitoring", json["audioStatus"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownAudioStatusFallsBackToOff()
    {
        var json = TestHarness.BuildClientState(processId: 1234, characterName: "Pilot",
                                                audioStatus: null);
        Assert.Equal("off", json["audioStatus"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `audioStatus` is not in the projection.

- [ ] **Step 3: Implement the wiring.** On `SplashDetected`, resolve the character name and call `_alerts.RaiseExternalAlert("wormhole_splash", characterName, "Audio", "Wormhole activated nearby")`, marshalling to the dispatcher exactly as `OnAlertTriggered` does at `:1180`.

- [ ] **Step 4: Add a sound event with its own gate.**

**Do not reuse `AlertNotificationRequested`.** That event is raised only when `config.TrayNotification` is enabled (`TriffViewSubsystem.cs:1216-1218`), whereas web playback today is independent of tray configuration (`App.jsx:280-306`). Hanging sound off it would silently remove sound for any alert configured with sound on and tray off — a regression in the behaviour this change exists to improve.

Add a sibling branch in `ProcessPendingAlerts`, next to the flash and tray branches:

```csharp
if (!string.Equals(config.Sound, "none", StringComparison.OrdinalIgnoreCase))
{
    AlertSoundRequested?.Invoke(alert, config.Sound, Settings.Alerts.MasterVolume);
}
```

with `public event Action<TriffAlertEvent, string, double>? AlertSoundRequested;` on the controller, mirroring `AlertNotificationRequested`.

- [ ] **Step 5: Implement `AlertSoundPlayer` and convert the assets.**

The four existing assets are `.ogg`, which WPF's `MediaPlayer` does not reliably decode. Convert each to 16-bit PCM WAV and add them as `<Resource>` in `native/TriffView.csproj` — the same build action already used for `Assets\TriffView.ico` at `:38` — then reference them by `pack://application:,,,/Assets/sounds/{id}.wav`.

Use `System.Windows.Media.MediaPlayer`, not `System.Media.SoundPlayer`: only the former exposes `Volume`, and `MasterVolume` is a real user-facing setting that would otherwise be silently dropped.

Two properties to implement deliberately rather than discover:
- `ProcessPendingAlerts` runs on the WPF dispatcher, so playback must be fire-and-forget. Never block the dispatcher on audio.
- A single `MediaPlayer` cuts off the previous sound when two alerts land close together. That is acceptable given per-event cooldowns; keep one instance and accept the cutoff rather than building a pool.

Subscribe in `MainWindow` alongside the existing `AlertNotificationRequested` subscription at `:241`.

- [ ] **Step 6: Remove web-side playback.** Delete the block at `App.jsx:280-306` and the now-unused `.ogg` imports at `App.jsx:3-6`, plus `playedAlertIdsRef` / `alertAudioReadyRef` if nothing else uses them. Grep for any other reference to the `.ogg` assets before deleting the files.

- [ ] **Step 7: Run tests and build.** Expected: PASS, and the native build succeeds.

- [ ] **Step 8: Commit**

```bash
git add native/TriffView/TriffViewSubsystem.cs native/TriffAudio/AlertSoundPlayer.cs native/MainWindow.xaml.cs native/TriffView.csproj native/TriffView/Assets/sounds app/src/App.jsx native/TriffView.Tests/AudioStateProjectionTests.cs
git commit -m "feat: wire splash detection into alerts and move alert sound native"
```

---

### Task 10: Settings UI and template capture UI

**Files:**
- Modify: `app/src/tools/TriffViewSettings.jsx` — add `wormhole_splash` to `ALERT_EVENT_DEFS` (`:25`), `ALERT_EVENT_DEFAULTS` (`:34`) and `DEFAULT_ALERT_EVENTS` (`:85`); add the splash toggle and threshold to the alerts panel (`:1636-1810`); add the Splash templates section; add `audioStatus` to the Clients panel (`:2003-2013`)

Line anchors in this task are approximate and will drift once earlier edits land — locate by symbol name, not by line.

**Interfaces:**
- Consumes: `triffview:state` (`clients[].audioStatus`, `alerts.splashDetectionEnabled`, `alerts.splashThreshold`), the `triffaudio:*` messages
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Add the event type to all three JS constants.** All four copies (three JS, one C#) must agree; nothing enforces this.

- [ ] **Step 2: Add the splash controls** to the alerts panel: an enable toggle and a threshold slider (0.10–0.90, default 0.35), committing via the existing `patchAlerts` helper at `:133-135` (note `:137` is `patchAlertEvent`, which is a different function).

- [ ] **Step 3: Add the Splash templates section.** Client picker plus **Capture recent splash**, which sends `triffaudio:capture`. On `triffaudio:capture-result`, render three candidates each with a play button (`new Audio("data:audio/wav;base64," + wavBase64)`), a **Save as template** button sending `triffaudio:save-template`, and a discard. Below it, the template list from `triffaudio:templates` with play and delete; delete is hidden for `builtIn` templates.

- [ ] **Step 4: Add audio status to the Clients panel** — a per-client label reading Monitoring / Silent / Unavailable / Off, with "Silent" visually distinct, since it is the failure mode the user must notice.

- [ ] **Step 5: Build the web UI**

```bash
cd app && npm install && npm run build
```

Expected: build succeeds, `app/dist` produced.

- [ ] **Step 6: Commit**

```bash
git add app/src/tools/TriffViewSettings.jsx
git commit -m "feat(ui): splash alert settings, template capture, and audio status"
```

---

### Task 11: Documentation and CLAUDE.md correction

**Files:**
- Modify: `CLAUDE.md` — correct the testing section
- Modify: `docs/DIAGNOSTICS.md` — note the new audio diagnostics lines

- [ ] **Step 1: Correct the testing section of CLAUDE.md.** It currently describes only `tests/TriffView.Tests` and its `<Compile Include>` constraint. There are in fact **three** test targets:

| Project | Framework | Style | CI |
|---|---|---|---|
| `tests/TriffView.Tests` | net8.0 | `<Compile Include>` links, cross-platform | `.github/workflows/build.yml:29-30` |
| `native/TriffView.Tests` | net8.0-windows | `ProjectReference` + `InternalsVisibleTo`, 27 test files | `.github/workflows/ci.yml:49-55` |
| `tests/TriffAlerts.Tests` | regression harness, run via `dotnet run` | — | `.github/workflows/ci.yml:58` |

Record that the Windows-free `<Compile Include>` constraint applies only to the first.

- [ ] **Step 2: Add a short audio section to CLAUDE.md** covering the fixed detector constants, the fact that changing them invalidates the shipped templates, and the muted-client silent-failure mode.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/DIAGNOSTICS.md
git commit -m "docs: correct test project description, document audio subsystem"
```

---

### Task 12: Verification on Windows

**Files:** none — this task produces evidence, not code.

Nothing in this plan proves runtime behaviour. This project cannot be run from WSL, so everything above is a claim until exercised on Windows with real EVE clients.

- [ ] **Step 1: Build and run the full test suite.** All three targets — `tests/TriffView.Tests`, `native/TriffView.Tests`, and the `tests/TriffAlerts.Tests` regression harness (`dotnet run --project tests/TriffAlerts.Tests/TriffAlerts.Tests.csproj -c Release`). Paste the output; do not summarise it.

- [ ] **Step 2: Copy `native/Assets/overlay-dist.zip` from the main checkout** and rebuild, or the app serves the "missing overlay" page and no UI check is valid. Confirm with `[Reflection.Assembly]::LoadFrom(...).GetManifestResourceNames()`.

- [ ] **Step 3: Launch with EVE clients running.** Confirm: every client shows an audio status; a muted client shows `silent`; disabling the feature shows `off` everywhere.

- [ ] **Step 4: Trigger a real splash** and confirm the alert fires with the right character name, the preview flashes, the tray balloon appears, and the sound plays **with the settings window closed**.

- [ ] **Step 5: Capture a template** through the UI end to end: capture, audition three candidates, save, confirm it appears in the list and survives a restart, then delete it.

- [ ] **Step 6: Measure CPU** with all clients monitored, and compare against the spike's 2.73% of one core for six captures. Report the actual number.

- [ ] **Step 7: Report results honestly.** State which steps were performed and which were not. Do not claim a step passed without output.

---

## Self-review notes

**Spec coverage.** Detection algorithm → Tasks 1, 3. Template format → Tasks 2, 6. Capture → Task 5. Alert integration → Task 7. Character attribution rule → Task 8. Audio health → Tasks 8, 9, 10. Native sound → Task 9. Template capture UI → Tasks 8 (`CaptureCandidates`), 9 (messages), 10 (UI). Settings → Tasks 7, 10. Documentation correction → Task 11. Runtime verification → Task 12.

**Known gap.** Task 4 depends on fixture audio that must be produced from recordings outside the repo. If the operator cannot supply it, Task 4 is blocked and must be reported as such rather than skipped silently — it is the only guard against a C# port that differs from the validated Python implementation.
