using System.Diagnostics;
using System.Runtime.InteropServices;
using Altrove.Capture.Native;
using Altrove.Core.Streaming;
using Altrove.Core.Video;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Altrove.Capture;

public sealed record EncoderCandidate(string Name, bool IsHardware, string? VendorId);

/// <summary>
/// H.264 through a Media Foundation encoder: the GPU's hardware encoder (AMD AMF, NVIDIA NVENC,
/// Intel Quick Sync) fed with GPU textures, or Microsoft's software encoder fed from memory.
/// Tuned for screen streaming: low-latency mode, CBR, no B-frames, frames encoded one at a time.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    private static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MtNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    private static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid HardwareVendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");

    private static readonly Guid CodecRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CodecLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CodecGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid CodecBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CodecQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid CodecForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private const uint EnumSync = 0x01, EnumHardware = 0x04, EnumSortAndFilter = 0x40;
    private const int ProfileMain = 77;
    private const int InterlaceProgressive = 2;
    private const int RateControlCbr = 0;
    private const int RateControlPeakConstrainedVbr = 1;
    private static readonly Guid CodecMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    private const int OutputProvidesSamples = 0x100, OutputCanProvideSamples = 0x200;

    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);

    private static readonly object StartupLock = new();
    private static bool _started;

    private readonly GraphicsDevice _device;
    private readonly EncoderSettings _settings;
    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly IntPtr _codecApi;
    private readonly ParameterSetCache _parameterSets = new();
    private readonly object _slotLock = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _keyframeRequested = true;
    private (Nv12Frame Frame, long Time)? _pending;
    private (Nv12Frame Frame, long Time)? _last;
    private long _lastSubmittedTime = -1;
    private ID3D11Texture2D? _readback;

    private H264Encoder(GraphicsDevice device, EncoderSettings settings, IMFActivate activate, bool hardware, string name)
    {
        _device = device;
        _settings = settings;
        _activate = activate;
        IsHardware = hardware;
        Name = name;
        _transform = activate.ActivateObject<IMFTransform>();
        try
        {
            var attributes = _transform.Attributes;
            if (hardware || GetUInt32(attributes, TransformAsync) != 0)
            {
                attributes.Set(TransformAsyncUnlock, 1u);
                _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            }
            attributes.Set(LowLatency, 1u);

            if (hardware)
            {
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(device.Device).CheckError();
                _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);
            }

            if (Marshal.QueryInterface(_transform.NativePointer, Interop.IidCodecApi, out _codecApi) != 0)
                _codecApi = IntPtr.Zero;
            ConfigureCodec();
            SetMediaTypes();
            if (_codecApi != IntPtr.Zero)
            {
                // Some encoders reset the rates when the media types are set: say them again.
                Interop.CodecSetUInt32(_codecApi, CodecMeanBitRate, (uint)settings.BitrateKbps * 1000);
                if (RateControl == "VBR")
                    Interop.CodecSetUInt32(_codecApi, CodecMaxBitRate, PeakBitrate);
            }

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            Release();
            throw;
        }
    }

    public string Name { get; }
    public bool IsHardware { get; }

    /// <summary>"VBR" (peak-constrained), "CBR", or "" when the encoder exposes no rate settings.</summary>
    public string RateControl { get; private set; } = "";

    // Up to 1.5× the mean for a moment, e.g. an IDR after a still screen.
    private uint PeakBitrate => (uint)(_settings.BitrateKbps * 1500L);

    /// <summary>True when the encoder takes GPU textures; otherwise frames go through system memory.</summary>
    public bool TakesTextures => IsHardware;

    public event Action<EncodedFrame>? FrameEncoded;
    public event Action<Exception>? Failed;

    /// <summary>The H.264 encoders on this PC, hardware first.</summary>
    public static IReadOnlyList<EncoderCandidate> List()
    {
        EnsureStarted();
        var result = new List<EncoderCandidate>();
        foreach (var (activate, hardware) in Enumerate(null))
        {
            using (activate)
                result.Add(new EncoderCandidate(NameOf(activate, hardware), hardware, GetString(activate, HardwareVendorId)));
        }
        // The AMD driver registers its encoder twice; one line is enough.
        return result.Distinct().ToList();
    }

    /// <summary>
    /// The best encoder that accepts these settings: the hardware one of the capturing GPU if it can take
    /// GPU frames, else any other that works, else software. Throws when none can be set up.
    /// </summary>
    public static H264Encoder Create(GraphicsDevice device, EncoderSettings settings, bool texturesAvailable, bool preferSoftware = false)
    {
        EnsureStarted();
        var vendor = $"VEN_{device.VendorId:X4}";
        var errors = new List<string>();
        var candidates = Enumerate(vendor)
            .Where(c => !c.Hardware || (texturesAvailable && !preferSoftware && !device.IsSoftware))
            .ToList();
        try
        {
            foreach (var (activate, hardware) in candidates)
            {
                var name = NameOf(activate, hardware);
                try
                {
                    var encoder = new H264Encoder(device, settings, activate, hardware, name);
                    candidates.RemoveAll(c => ReferenceEquals(c.Activate, activate));
                    return encoder;
                }
                catch (Exception ex) when (ex is SharpGenException or COMException or InvalidCastException)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var (activate, _) in candidates)
                activate.Dispose();
        }
        throw new InvalidOperationException("Nessun encoder H.264 utilizzabile. " + string.Join(" | ", errors));
    }

    private static List<(IMFActivate Activate, bool Hardware)> Enumerate(string? preferredVendor)
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatNv12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatH264 };
        var hardware = Find(VideoEncoderCategory, EnumHardware | EnumSortAndFilter, input, output);
        // The capturing GPU's own encoder first, so frames never leave the card.
        hardware = hardware
            .OrderByDescending(a => preferredVendor is not null && string.Equals(GetString(a, HardwareVendorId), preferredVendor, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var software = Find(VideoEncoderCategory, EnumSync | EnumSortAndFilter, input, output);
        return hardware.Select(a => (a, true)).Concat(software.Select(a => (a, false))).ToList();
    }

    /// <summary>MFTEnumEx, empty instead of null when nothing matches (a PC without a hardware encoder).</summary>
    internal static List<IMFActivate> Find(Guid category, uint flags, RegisterTypeInfo input, RegisterTypeInfo output)
    {
        IMFActivateCollection? found;
        try
        {
            found = MediaFactory.MFTEnumEx(category, flags, input, output);
        }
        catch (SharpGenException)
        {
            return [];
        }
        if (found is null)
            return [];
        // The collection releases its items when disposed: keep our own reference to each.
        using (found)
        {
            return found
                .Where(a => a is not null && a.NativePointer != IntPtr.Zero)
                .Select(a =>
                {
                    Marshal.AddRef(a.NativePointer);
                    return new IMFActivate(a.NativePointer);
                })
                .ToList();
        }
    }

    private static string NameOf(IMFActivate activate, bool hardware) =>
        (GetString(activate, FriendlyName) ?? "H.264") + (hardware ? " (hardware)" : " (software)");

    private static string? GetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static uint GetUInt32(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (SharpGenException)
        {
            return 0;
        }
    }

    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started)
                return;
            MediaFactory.MFStartup(false).CheckError();
            _started = true;
        }
    }

    private void ConfigureCodec()
    {
        if (_codecApi == IntPtr.Zero)
            return;
        // Each is best effort: encoders accept different subsets, and the defaults still work.
        // Peak-constrained VBR, not CBR: in CBR the AMD encoder pads every frame with filler data to hold
        // the rate (29% of the stream in the first test on the RX 6750 XT), bandwidth that carries nothing.
        // The peak keeps keyframes from flooding the network; encoders that refuse it fall back to CBR.
        if (Interop.CodecSetUInt32(_codecApi, CodecRateControlMode, RateControlPeakConstrainedVbr) >= 0)
        {
            RateControl = "VBR";
            Interop.CodecSetUInt32(_codecApi, CodecMaxBitRate, PeakBitrate);
        }
        else
        {
            Interop.CodecSetUInt32(_codecApi, CodecRateControlMode, RateControlCbr);
            RateControl = "CBR";
        }
        Interop.CodecSetUInt32(_codecApi, CodecMeanBitRate, (uint)_settings.BitrateKbps * 1000);
        Interop.CodecSetBool(_codecApi, CodecLowLatencyMode, true);
        Interop.CodecSetUInt32(_codecApi, CodecBPictureCount, 0);
        // Receivers ask for an IDR when they need one; a long GOP keeps the bitrate for the picture.
        Interop.CodecSetUInt32(_codecApi, CodecGopSize, (uint)Math.Max(1, _settings.Fps * 5));
        Interop.CodecSetUInt32(_codecApi, CodecQualityVsSpeed, IsHardware ? 50u : 15u);
    }

    private void SetMediaTypes()
    {
        var width = (uint)_settings.Width;
        var height = (uint)_settings.Height;

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MtMajorType, MediaTypeVideo);
            output.Set(MtSubtype, FormatH264);
            output.Set(MtAvgBitrate, (uint)_settings.BitrateKbps * 1000);
            output.Set(MtFrameSize, MediaFactory.PackSize(width, height));
            output.Set(MtFrameRate, MediaFactory.PackRatio(_settings.Fps, 1));
            output.Set(MtPixelAspectRatio, MediaFactory.PackRatio(1, 1));
            output.Set(MtInterlaceMode, (uint)InterlaceProgressive);
            output.Set(MtMpeg2Profile, (uint)ProfileMain);
            _transform.SetOutputType(0, output, 0);
        }

        using var input = FindInputType() ?? MediaFactory.MFCreateMediaType();
        input.Set(MtMajorType, MediaTypeVideo);
        input.Set(MtSubtype, FormatNv12);
        input.Set(MtFrameSize, MediaFactory.PackSize(width, height));
        input.Set(MtFrameRate, MediaFactory.PackRatio(_settings.Fps, 1));
        input.Set(MtPixelAspectRatio, MediaFactory.PackRatio(1, 1));
        input.Set(MtInterlaceMode, (uint)InterlaceProgressive);
        input.Set(MtDefaultStride, width);
        input.Set(MtNominalRange, 2u); // 16-235, what both converters produce
        _transform.SetInputType(0, input, 0);
    }

    private IMFMediaType? FindInputType()
    {
        for (var i = 0; i < 32; i++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetInputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null;
            }
            if (GetGuid(type, MtSubtype) == FormatNv12)
                return type;
            type.Dispose();
        }
        return null;
    }

    private static Guid GetGuid(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetGUID(key);
        }
        catch (SharpGenException)
        {
            return Guid.Empty;
        }
    }

    public void Start()
    {
        _thread = new Thread(IsHardware || _events is not null ? AsyncLoop : SyncLoop)
        {
            IsBackground = true,
            Name = "Altrove encoder",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Hands the latest converted frame to the encoder; a frame not yet taken is replaced.</summary>
    public void Submit(Nv12Frame frame, long time100ns)
    {
        lock (_slotLock)
            _pending = (frame, time100ns);
        if (_frameReady.CurrentCount == 0)
        {
            try
            {
                _frameReady.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public void RequestKeyframe()
    {
        _keyframeRequested = true;
        // A still screen sends no new frames: wake the encoder so it re-encodes the last one.
        lock (_slotLock)
        {
            if (_pending is null && _last is { } last)
                _pending = (last.Frame, Math.Max(last.Time, _lastSubmittedTime) + 1);
        }
        if (_frameReady.CurrentCount == 0)
        {
            try
            {
                _frameReady.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private (Nv12Frame Frame, long Time)? WaitForFrame()
    {
        while (!_stopping)
        {
            _frameReady.Wait(200);
            lock (_slotLock)
            {
                if (_pending is { } next)
                {
                    _pending = null;
                    _last = next;
                    // Media Foundation wants timestamps that only go forward.
                    var time = Math.Max(next.Time, _lastSubmittedTime + 1);
                    _lastSubmittedTime = time;
                    return (next.Frame, time);
                }
            }
        }
        return null;
    }

    private void AsyncLoop()
    {
        try
        {
            while (!_stopping)
            {
                using var mediaEvent = _events!.GetEvent(0);
                switch (mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        if (WaitForFrame() is not { } frame)
                            return;
                        Encode(frame.Frame, frame.Time);
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        DrainOutput(single: true);
                        break;
                    case MediaEventTypes.TransformDrainComplete:
                        return;
                }
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Failed?.Invoke(ex);
        }
        catch (Exception)
        {
            // Shutting down: the encoder was torn down under the blocking GetEvent.
        }
    }

    private void SyncLoop()
    {
        try
        {
            while (!_stopping)
            {
                if (WaitForFrame() is not { } frame)
                    return;
                Encode(frame.Frame, frame.Time);
                DrainOutput(single: false);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Failed?.Invoke(ex);
        }
    }

    private void Encode(Nv12Frame frame, long time)
    {
        if (_keyframeRequested && _codecApi != IntPtr.Zero)
        {
            _keyframeRequested = false;
            Interop.CodecSetUInt32(_codecApi, CodecForceKeyFrame, 1);
        }

        using var sample = MediaFactory.MFCreateSample();
        using var buffer = CreateInputBuffer(frame);
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = 10_000_000L / _settings.Fps;
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.HResult == NotAccepting && _events is null)
        {
            DrainOutput(single: false);
            _transform.ProcessInput(0, sample, 0);
        }
    }

    private IMFMediaBuffer CreateInputBuffer(Nv12Frame frame)
    {
        if (TakesTextures && frame.Texture is not null)
        {
            var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, frame.Texture, 0, false);
            using var buffer2D = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = buffer2D.ContiguousLength;
            return buffer;
        }

        var length = frame.Width * frame.Height * 3 / 2;
        var memory = MediaFactory.MFCreateMemoryBuffer(length);
        memory.Lock(out var pointer, out _, out _);
        try
        {
            if (frame.Bytes is not null)
                Marshal.Copy(frame.Bytes, 0, pointer, length);
            else
                ReadBack(frame, pointer);
        }
        finally
        {
            memory.Unlock();
        }
        memory.CurrentLength = length;
        return memory;
    }

    /// <summary>Copies a GPU NV12 frame into memory for the software encoder.</summary>
    private unsafe void ReadBack(Nv12Frame frame, IntPtr destination)
    {
        if (_readback is null)
        {
            _readback = _device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        _device.Context.CopyResource(_readback, frame.Texture!);
        var mapped = _device.Context.Map(_readback, 0, MapMode.Read);
        try
        {
            var source = (byte*)mapped.DataPointer;
            var target = (byte*)destination;
            var pitch = (int)mapped.RowPitch;
            // Y plane, then the interleaved chroma plane, which starts right after Height rows.
            for (var row = 0; row < frame.Height * 3 / 2; row++)
                Buffer.MemoryCopy(source + row * pitch, target + row * frame.Width, frame.Width, frame.Width);
        }
        finally
        {
            _device.Context.Unmap(_readback, 0);
        }
    }

    private void DrainOutput(bool single)
    {
        while (true)
        {
            var info = _transform.GetOutputStreamInfo(0);
            var providesSamples = (info.Flags & (OutputProvidesSamples | OutputCanProvideSamples)) != 0;
            IMFSample? allocated = null;
            if (!providesSamples)
            {
                allocated = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _settings.Width * _settings.Height * 3 / 2));
                allocated.AddBuffer(buffer);
            }

            var output = new OutputDataBuffer { StreamID = 0, Sample = allocated! };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            try
            {
                if (result.Code == NeedMoreInput)
                    return;
                if (result.Code == StreamChange)
                {
                    // The encoder changed its output format (some hardware does this after the first frame).
                    using var type = _transform.GetOutputAvailableType(0, 0);
                    _transform.SetOutputType(0, type, 0);
                    continue;
                }
                result.CheckError();
                if (output.Sample is { } sample)
                    Emit(sample);
            }
            finally
            {
                if (output.Sample is not null && !ReferenceEquals(output.Sample, allocated))
                    output.Sample.Dispose();
                allocated?.Dispose();
            }
            if (single)
                return;
        }
    }

    private void Emit(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        byte[] data;
        try
        {
            data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
        }
        finally
        {
            buffer.Unlock();
        }
        if (data.Length == 0)
            return;
        var unit = _parameterSets.Complete(data);
        var keyframe = H264.Describe(unit).HasIdr;
        FrameEncoded?.Invoke(new EncodedFrame(unit, sample.SampleTime / 10, keyframe));
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _frameReady.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        if (_events is not null)
        {
            // Unblocks the GetEvent the encoder thread is waiting in.
            try
            {
                using var shutdown = _transform.QueryInterface<IMFShutdown>();
                shutdown.Shutdown();
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidCastException)
            {
            }
        }
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(2)))
            Debug.WriteLine("Encoder thread did not stop in time.");
        Release();
    }

    private void Release()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        if (_codecApi != IntPtr.Zero)
            Marshal.Release(_codecApi);
        _readback?.Dispose();
        _events?.Dispose();
        _transform.Dispose();
        try
        {
            _activate.ShutdownObject();
        }
        catch (SharpGenException)
        {
        }
        _activate.Dispose();
        _manager?.Dispose();
    }
}
