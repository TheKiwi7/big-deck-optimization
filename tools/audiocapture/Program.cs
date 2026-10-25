// Records the audio of one process (and its children) to a 48 kHz stereo 16-bit WAV file, with Windows' process loopback
// capture (Windows 10 2004+): only that process's sound, nothing else playing on the PC.
// Usage: dotnet run --project tools/audiocapture -- <pid> <out.wav>   (stops on a line on stdin, e.g. "q")
// Silence the process doesn't render is written as silence, so the file stays as long as the wall-clock recording.
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length < 2 || !int.TryParse(args[0], out int pid))
{
    Console.Error.WriteLine("usage: audiocapture <pid> <out.wav>");
    return 2;
}
const int Rate = 48000, Channels = 2, Bits = 16, BlockAlign = Channels * Bits / 8;

var audioClient = Loopback.Activate((uint)pid);
var format = new WaveFormatEx { wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, wBitsPerSample = Bits,
    nBlockAlign = BlockAlign, nAvgBytesPerSec = Rate * BlockAlign, cbSize = 0 };
const uint Loopback_ = 0x00020000, AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000;
Marshal.ThrowExceptionForHR(audioClient.Initialize(0, Loopback_ | AutoConvertPcm | SrcDefaultQuality, 2_000_000, 0, ref format, IntPtr.Zero));
Guid captureId = typeof(IAudioCaptureClient).GUID;
Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureId, out object captureObject));
var capture = (IAudioCaptureClient)captureObject;

using var file = new FileStream(args[1], FileMode.Create, FileAccess.Write);
using var writer = new BinaryWriter(file);
writer.Write(new byte[44]); // header written at the end
long frames = 0;
var clock = Stopwatch.StartNew();
Marshal.ThrowExceptionForHR(audioClient.Start());
Console.WriteLine($"recording process {pid} at {DateTime.Now:HH:mm:ss.fff}");
var stop = Task.Run(() => Console.ReadLine());
byte[] silence = new byte[Rate / 10 * BlockAlign];
while (!stop.IsCompleted)
{
    Thread.Sleep(10);
    while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
    {
        Marshal.ThrowExceptionForHR(capture.GetBuffer(out IntPtr data, out uint count, out uint flags, out _, out _));
        int bytes = (int)count * BlockAlign;
        if ((flags & 2) != 0 || data == IntPtr.Zero)
        {
            writer.Write(new byte[bytes]); // AUDCLNT_BUFFERFLAGS_SILENT
        }
        else
        {
            byte[] chunk = new byte[bytes];
            Marshal.Copy(data, chunk, 0, bytes);
            writer.Write(chunk);
        }
        frames += count;
        capture.ReleaseBuffer(count);
    }
    // No packets while the process renders nothing: pad with silence to stay in step with the clock (100 ms behind,
    // the capture's own latency).
    long expected = (long)((clock.Elapsed.TotalSeconds - 0.1) * Rate);
    while (frames < expected)
    {
        int pad = (int)Math.Min(expected - frames, silence.Length / BlockAlign);
        writer.Write(silence, 0, pad * BlockAlign);
        frames += pad;
    }
}
audioClient.Stop();
long dataBytes = frames * BlockAlign;
writer.Seek(0, SeekOrigin.Begin);
writer.Write("RIFF"u8.ToArray()); writer.Write((int)(36 + dataBytes)); writer.Write("WAVE"u8.ToArray());
writer.Write("fmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)Channels); writer.Write(Rate);
writer.Write(Rate * BlockAlign); writer.Write((short)BlockAlign); writer.Write((short)Bits);
writer.Write("data"u8.ToArray()); writer.Write((int)dataBytes);
Console.WriteLine($"saved {args[1]}: {frames / (double)Rate:F1} s");
return 0;

[StructLayout(LayoutKind.Sequential)]
struct WaveFormatEx
{
    public ushort wFormatTag, nChannels;
    public int nSamplesPerSec, nAvgBytesPerSec;
    public ushort nBlockAlign, wBitsPerSample, cbSize;
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

// Marker: the completion handler may be called from any thread.
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject
{
}

static class Loopback
{
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Done.Set();
            return 0;
        }
    }

    /// <summary>An IAudioClient capturing the process tree of <paramref name="pid"/> (VAD\Process_Loopback).</summary>
    public static IAudioClient Activate(uint pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK (1), { TargetProcessId, INCLUDE_TARGET_PROCESS_TREE (0) } }
        IntPtr parameters = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(parameters, 0, 1);
        Marshal.WriteInt32(parameters, 4, (int)pid);
        Marshal.WriteInt32(parameters, 8, 0);
        // PROPVARIANT { VT_BLOB (65), BLOB { cbSize, pBlobData } } on x64: vt at 0, cbSize at 8, pointer at 16.
        IntPtr variant = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i += 4)
        {
            Marshal.WriteInt32(variant, i, 0);
        }
        Marshal.WriteInt16(variant, 0, 65);
        Marshal.WriteInt32(variant, 8, 12);
        Marshal.WriteIntPtr(variant, 16, parameters);
        Guid audioClient = typeof(IAudioClient).GUID;
        var handler = new Handler();
        Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref audioClient, variant, handler, out var operation));
        if (!handler.Done.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("audio activation timed out");
        }
        Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int result, out object activated));
        Marshal.ThrowExceptionForHR(result);
        return (IAudioClient)activated;
    }
}
