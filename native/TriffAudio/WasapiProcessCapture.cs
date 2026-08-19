using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TriffView.Audio;

/// <summary>
/// Captures the audio rendered by a single process (and its child processes) via WASAPI
/// process-loopback capture, resampling to 16 kHz mono before invoking <see cref="SampleCallback"/>.
/// Windows-only COM/WASAPI interop, so unlike the rest of TriffAudio this is not linked into the
/// cross-platform test project; it is exercised live against real EVE clients (see Task 10).
///
/// This reproduces a spike proven working against six live EVE clients. Several points here look
/// like they could be simplified but were each confirmed by measurement during that spike; see the
/// comments at each one before changing it.
/// </summary>
internal sealed class WasapiProcessCapture : IDisposable
{
    public delegate void SampleCallback(ReadOnlySpan<float> samples);

    private readonly uint _processId;
    private readonly SampleCallback _onSamples;
    private readonly AutoResetEvent _dataEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly DownmixResampler _resampler = new();

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private bool _disposed;

    public WasapiProcessCapture(uint processId, SampleCallback onSamples)
    {
        _processId = processId;
        _onSamples = onSamples;
    }

    /// <summary>UTC time of the most recently delivered packet, updated even when the packet is
    /// silence — this is "capture is alive", not "audio is playing".</summary>
    public DateTime LastPacketUtc { get; private set; }

    /// <summary>
    /// Activates process-loopback capture for the target process and starts the capture thread.
    /// Never throws: one client's activation or format failure must not take down the others, so
    /// every failure path is reported through <paramref name="error"/> instead.
    /// </summary>
    public bool Start(out string? error)
    {
        error = null;
        IntPtr paramsPtr = IntPtr.Zero;
        try
        {
            var activationParams = new AudioClientActivationParams
            {
                // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.
                ActivationType = 1,
                TargetProcessId = _processId,
                // 0 = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE: capture the EVE process
                // and anything it spawns, not just the top-level process.
                ProcessLoopbackMode = 0,
            };

            var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
            paramsPtr = Marshal.AllocHGlobal(paramsSize);
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            // The activation parameters travel inside a PROPVARIANT of type VT_BLOB, holding a
            // pointer to the AUDIOCLIENT_ACTIVATION_PARAMS struct above. This is the documented
            // (if obscure) way to pass process-loopback parameters to ActivateAudioInterfaceAsync.
            var propVariant = new PropVariant
            {
                Vt = VT_BLOB,
                Blob = new Blob { Size = (uint)paramsSize, Data = paramsPtr },
            };

            var handler = new ActivationCompletionHandler();
            try
            {
                ActivateAudioInterfaceAsync(
                    VirtualDeviceProcessLoopback, IID_IAudioClient, ref propVariant, handler, out _);
            }
            catch (Exception ex)
            {
                error = $"ActivateAudioInterfaceAsync failed: {ex.Message}";
                return false;
            }

            // Activation is asynchronous even though nothing here needs to run concurrently with
            // it; block until the completion handler's callback (delivered on an arbitrary MTA
            // thread, hence IAgileObject on the handler) signals it is done.
            if (!handler.Wait(TimeSpan.FromSeconds(5)))
            {
                error = "Timed out waiting for audio interface activation.";
                return false;
            }

            if (handler.ActivateResult != 0)
            {
                error = $"Audio interface activation failed with HRESULT 0x{handler.ActivateResult:X8}.";
                return false;
            }

            // The virtual process-loopback device hands back a bare IAudioClient; querying it for
            // IAudioClient2 returns E_NOINTERFACE (measured), so this deliberately does not attempt
            // the IAudioClient2-specific initialization path other WASAPI clients use.
            if (handler.ActivatedInterface is not IAudioClient audioClient)
            {
                error = "Activated interface was not an IAudioClient.";
                return false;
            }

            _audioClient = audioClient;
        }
        finally
        {
            if (paramsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(paramsPtr);
        }

        try
        {
            // The virtual device does not support GetMixFormat or AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            // (measured); an explicit format must be requested instead. 48 kHz stereo float32 is the
            // native EVE/Windows mix format in practice, so this avoids driver-side conversion.
            var format = new WaveFormatEx
            {
                FormatTag = WaveFormatIeeeFloat,
                Channels = 2,
                SamplesPerSec = 48000,
                BitsPerSample = 32,
                BlockAlign = 2 * 32 / 8,
                AvgBytesPerSec = 48000 * (2 * 32 / 8),
                ExtraSize = 0,
            };

            var sessionGuid = Guid.Empty;
            // hnsPeriodicity must be 0 in shared mode; 200,000 * 100 ns = 20 ms buffer.
            var hr = _audioClient.Initialize(
                AudclntSharemodeShared,
                AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback,
                200_000, 0, ref format, ref sessionGuid);
            if (hr != 0)
            {
                error = $"IAudioClient.Initialize failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            hr = _audioClient.SetEventHandle(_dataEvent.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0)
            {
                error = $"IAudioClient.SetEventHandle failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            var captureClientIid = IID_IAudioCaptureClient;
            hr = _audioClient.GetService(ref captureClientIid, out var captureClientObj);
            if (hr != 0 || captureClientObj is not IAudioCaptureClient captureClient)
            {
                error = $"IAudioClient.GetService(IAudioCaptureClient) failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            _captureClient = captureClient;

            hr = _audioClient.Start();
            if (hr != 0)
            {
                error = $"IAudioClient.Start failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = $"WasapiCapture-{_processId}",
            };
            _captureThread.Start();

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void CaptureLoop()
    {
        var waitHandles = new WaitHandle[] { _stopEvent, _dataEvent };
        while (true)
        {
            var signaled = WaitHandle.WaitAny(waitHandles, 2000);
            if (signaled == 0)
                return; // stop requested

            if (signaled == WaitHandle.WaitTimeout)
                continue; // no packet in this interval; keep waiting rather than treat it as an error

            DrainPackets();
        }
    }

    private void DrainPackets()
    {
        var captureClient = _captureClient!;
        while (true)
        {
            var hr = captureClient.GetNextPacketSize(out var packetFrames);
            if (hr != 0 || packetFrames == 0)
                return;

            hr = captureClient.GetBuffer(out var dataPtr, out var framesAvailable, out var flags, out _, out _);
            if (hr != 0)
                return;

            try
            {
                LastPacketUtc = DateTime.UtcNow;

                var stereoSamples = new float[framesAvailable * 2];
                // AUDCLNT_BUFFERFLAGS_SILENT is not trustworthy here: a muted process delivers
                // zero-filled buffers without ever setting it (measured). Always copy the real
                // buffer rather than branching on the flag, so muted silence and "no flag but
                // actually silent" both fall out of the same path as plain all-zero data.
                if (dataPtr != IntPtr.Zero)
                    Marshal.Copy(dataPtr, stereoSamples, 0, stereoSamples.Length);

                var mono16k = _resampler.Process(stereoSamples);
                if (mono16k.Length > 0)
                    _onSamples(mono16k);
            }
            finally
            {
                captureClient.ReleaseBuffer(framesAvailable);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stopEvent.Set();
        _captureThread?.Join(2000);

        try { _audioClient?.Stop(); } catch { /* best-effort teardown */ }

        if (_captureClient is not null)
            Marshal.ReleaseComObject(_captureClient);
        if (_audioClient is not null)
            Marshal.ReleaseComObject(_audioClient);

        _dataEvent.Dispose();
        _stopEvent.Dispose();
    }

    // --- Downmix + resample: 48 kHz stereo -> 16 kHz mono -------------------------------------

    /// <summary>
    /// Downmixes stereo to mono and decimates 48 kHz to 16 kHz (an exact 3:1 ratio) with a small
    /// windowed-sinc FIR low-pass ahead of the decimation. Decimating without it would alias
    /// everything above 8 kHz into the 0-8 kHz band the splash detector actually uses; the
    /// detector's discriminative content is below ~6 kHz, so filter quality beyond "some
    /// low-pass" does not matter, but no filter at all measurably corrupts that band.
    /// Holds FIR history and decimation phase across calls, since packets do not align with the
    /// 3-sample decimation period.
    /// </summary>
    private sealed class DownmixResampler
    {
        private const int TapCount = 31;
        private static readonly float[] Taps = BuildLowPassTaps(TapCount, cutoffHz: 7000.0, sampleRateHz: 48000.0);

        private readonly float[] _history = new float[TapCount - 1];
        private int _decimatePhase;

        public float[] Process(ReadOnlySpan<float> stereoSamples)
        {
            var frameCount = stereoSamples.Length / 2;
            var mono = new float[frameCount];
            for (var i = 0; i < frameCount; i++)
                mono[i] = (stereoSamples[2 * i] + stereoSamples[2 * i + 1]) * 0.5f;

            var combined = new float[_history.Length + mono.Length];
            _history.CopyTo(combined, 0);
            mono.CopyTo(combined, _history.Length);

            var output = new List<float>(mono.Length / 3 + 1);
            for (var n = 0; n < mono.Length; n++)
            {
                if (_decimatePhase == 0)
                {
                    var centerIndex = _history.Length + n;
                    double acc = 0;
                    for (var t = 0; t < TapCount; t++)
                        acc += Taps[t] * combined[centerIndex - t];
                    output.Add((float)acc);
                }

                _decimatePhase = (_decimatePhase + 1) % 3;
            }

            var historyStart = combined.Length - _history.Length;
            Array.Copy(combined, historyStart, _history, 0, _history.Length);

            return output.ToArray();
        }

        private static float[] BuildLowPassTaps(int tapCount, double cutoffHz, double sampleRateHz)
        {
            var cutoffNormalized = cutoffHz / (sampleRateHz / 2.0);
            var mid = (tapCount - 1) / 2.0;
            var taps = new double[tapCount];
            var sum = 0.0;

            for (var i = 0; i < tapCount; i++)
            {
                var x = i - mid;
                var sinc = x == 0.0
                    ? cutoffNormalized
                    : Math.Sin(Math.PI * cutoffNormalized * x) / (Math.PI * x);
                var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (tapCount - 1)); // Hamming
                var value = sinc * window;
                taps[i] = value;
                sum += value;
            }

            var result = new float[tapCount];
            for (var i = 0; i < tapCount; i++)
                result[i] = (float)(taps[i] / sum); // normalize so DC gain is exactly 1

            return result;
        }
    }

    // --- COM/WASAPI interop ---------------------------------------------------------------------

    private const string VirtualDeviceProcessLoopback = @"VAD\Process_Loopback";

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const ushort VT_BLOB = 0x41;
    private const int AudclntSharemodeShared = 0;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventCallback = 0x00040000;
    private const ushort WaveFormatIeeeFloat = 3;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    /// <summary>
    /// Only the VT_BLOB arm of PROPVARIANT's union is needed here, so this models just that arm
    /// rather than the full union: an 8-byte header (vt + reserved words) followed by the BLOB.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public Blob Blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WaveFormatEx format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint currentPadding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormatPtr);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>
    /// Marker interface with no members: implementing it tells COM the object can be called from
    /// any apartment without a proxy, which is required here because the activation callback
    /// arrives on an arbitrary MTA thread.
    /// </summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public int ActivateResult { get; private set; }
        public object? ActivatedInterface { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var hr, out var iface);
                ActivateResult = hr;
                ActivatedInterface = iface;
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult;
            }
            finally
            {
                _completed.Set();
            }

            return 0; // S_OK
        }

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }
}
