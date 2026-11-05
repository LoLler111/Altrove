using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Altrove.Capture;

public sealed record VerifyResult(int FramesIn, int FramesDecoded, int Width, int Height, string Decoder);

/// <summary>
/// Decodes a stream with Windows' own H.264 decoder: a check that what the encoder produced is valid
/// H.264 that a receiver can play, without needing one.
/// </summary>
public static class H264Verifier
{
    private static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);

    public static VerifyResult Decode(IEnumerable<byte[]> accessUnits, string? snapshotBmp = null)
    {
        _snapshotPath = snapshotBmp;
        MediaFactory.MFStartup(false).CheckError();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatH264 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatNv12 };
        var found = H264Encoder.Find(VideoDecoderCategory, 0x01 | 0x40, input, output);
        var activate = found.FirstOrDefault() ?? throw new InvalidOperationException("No software H.264 decoder found.");
        foreach (var other in found.Skip(1))
            other.Dispose();
        var name = activate.GetString(FriendlyName);
        using var decoder = activate.ActivateObject<IMFTransform>();

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MtMajorType, MediaTypeVideo);
            type.Set(MtSubtype, FormatH264);
            decoder.SetInputType(0, type, 0);
        }
        SetNv12Output(decoder);
        decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);

        int framesIn = 0, decoded = 0;
        long time = 0;
        foreach (var unit in accessUnits)
        {
            framesIn++;
            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(unit.Length);
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(unit, 0, pointer, unit.Length);
            buffer.Unlock();
            buffer.CurrentLength = unit.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = 166_667;
            time += 166_667;
            while (true)
            {
                try
                {
                    decoder.ProcessInput(0, sample, 0);
                    break;
                }
                catch (SharpGenException ex) when (ex.HResult == NotAccepting)
                {
                    decoded += Drain(decoder);
                }
            }
            decoded += Drain(decoder);
        }
        decoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        decoded += Drain(decoder);

        using var current = decoder.GetOutputCurrentType(0);
        MediaFactory.UnpackSize(current.GetUInt64(MtFrameSize), out var width, out var height);
        activate.ShutdownObject();
        return new VerifyResult(framesIn, decoded, (int)width, (int)height, name);
    }

    private static void SetNv12Output(IMFTransform decoder)
    {
        for (var i = 0; ; i++)
        {
            using var type = decoder.GetOutputAvailableType(0, i);
            if (type.GetGUID(MtSubtype) == FormatNv12)
            {
                decoder.SetOutputType(0, type, 0);
                return;
            }
        }
    }

    private static int Drain(IMFTransform decoder)
    {
        var count = 0;
        while (true)
        {
            var info = decoder.GetOutputStreamInfo(0);
            using var sample = MediaFactory.MFCreateSample();
            using (var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 4096 * 2304 * 3 / 2)))
                sample.AddBuffer(buffer);
            var output = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var result = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result.Code == NeedMoreInput)
                return count;
            if (result.Code == StreamChange)
            {
                SetNv12Output(decoder);
                continue;
            }
            result.CheckError();
            if (count == 0 && _snapshotPath is not null)
            {
                SaveSnapshot(decoder, sample, _snapshotPath);
                _snapshotPath = null;
            }
            count++;
        }
    }

    [ThreadStatic]
    private static string? _snapshotPath;

    /// <summary>Writes a decoded NV12 frame as a 24-bit BMP, BT.709 limited range.</summary>
    private static void SaveSnapshot(IMFTransform decoder, IMFSample sample, string path)
    {
        using var type = decoder.GetOutputCurrentType(0);
        MediaFactory.UnpackSize(type.GetUInt64(MtFrameSize), out var w, out var h);
        int width = (int)w, height = (int)h;
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        var nv12 = new byte[length];
        Marshal.Copy(pointer, nv12, 0, length);
        buffer.Unlock();
        var stride = width;
        var rowBytes = (width * 3 + 3) & ~3;
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + rowBytes * height);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(new byte[24]);
        var row = new byte[rowBytes];
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var luma = nv12[y * stride + x] - 16;
                var uv = stride * height + (y / 2) * stride + (x & ~1);
                var cb = nv12[uv] - 128;
                var cr = nv12[uv + 1] - 128;
                row[x * 3 + 2] = Clamp(1.164 * luma + 1.793 * cr);
                row[x * 3 + 1] = Clamp(1.164 * luma - 0.213 * cb - 0.533 * cr);
                row[x * 3] = Clamp(1.164 * luma + 2.112 * cb);
            }
            writer.Write(row);
        }
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
