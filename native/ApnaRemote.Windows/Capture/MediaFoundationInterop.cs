using System.Runtime.InteropServices;
using System.Text;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Minimal Media Foundation interop for local H.264 sink-writer encode.
/// Uses raw COM pointers/vtables for IMFSinkWriter to avoid brittle RCW casts.
/// </summary>
internal static class MediaFoundationInterop
{
    public const int MF_SDK_VERSION = 0x0002;
    public const int MF_API_VERSION = 0x0070;
    public const int MF_VERSION = (MF_SDK_VERSION << 16) | MF_API_VERSION;
    public const int MFSTARTUP_FULL = 0;
    public const int MFVideoInterlace_Progressive = 2;
    private const uint CLSCTX_INPROC_SERVER = 1;

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    public static readonly Guid MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bc9c-0aa8d0ee8199");
    public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee515002292");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1eeb-4faa-9aaa-1aa8bfa4e6b0");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-e1df-42d3-94a2-a25a1f0d2678");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b2-a494-4cd3a7d4edbe");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    public static readonly Guid CLSID_MFReadWriteClassFactory = new(0x48e2ed0f, 0x98c2, 0x4a37, 0xbe, 0xd5, 0x16, 0x63, 0x12, 0xdd, 0xd8, 0x3f);
    public static readonly Guid CLSID_MFSinkWriter = new(0xa3bbfb17, 0x8273, 0x4e52, 0x9e, 0x0e, 0x97, 0x39, 0xdc, 0x88, 0x79, 0x90);
    public static readonly Guid IID_IMFSinkWriter = new(0x31336f1c, 0x5f7b, 0x4805, 0xa4, 0xd7, 0x30, 0x5c, 0xb4, 0x30, 0x82, 0x89);
    public static readonly Guid IID_IMFReadWriteClassFactory = new("E7FE2E12-661C-40DA-92F9-4F002AB67627");
    public static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    public static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae86633ee0");
    public static readonly Guid IID_IMFSample = new("c40a00f2-4214-4a9b-aadb-e84fd14bbb0a");
    public static readonly Guid IID_IMFMediaBuffer = new("045FA593-8799-42f0-99cb-36d3d3f0f799");

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateAttributes(out IntPtr ppMFAttributes, int cInitialSize);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(operation + " failed: 0x" + hr.ToString("X8"));
        }
    }

    public static ulong PackSize(int width, int height)
        => ((ulong)(uint)width << 32) | (uint)height;

    public static ulong PackRatio(int numerator, int denominator)
        => ((ulong)(uint)numerator << 32) | (uint)denominator;

    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string? pwszOutputURL,
        IntPtr pByteStream,
        IntPtr pAttributes,
        out IntPtr ppSinkWriter);

    public static IntPtr CreateSinkWriter(string outputPath, IntPtr attributes)
    {
        // Prefer the direct export. Use the returned pointer via raw vtable (no RCW/QI cast).
        ThrowIfFailed(
            MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, attributes, out IntPtr writer),
            "MFCreateSinkWriterFromURL");
        if (writer == IntPtr.Zero)
        {
            throw new InvalidOperationException("MFCreateSinkWriterFromURL returned null.");
        }

        return writer;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateInstanceFromUrlDelegate(
        IntPtr thisPtr,
        ref Guid clsid,
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        IntPtr attributes,
        ref Guid riid,
        out IntPtr ppv);

    public static void AttributesSetUint32(IntPtr attributes, Guid key, int value)
        => ThrowIfFailed(VTable.CallSetUint32(attributes, key, value), "IMFAttributes.SetUINT32");

    public static void AttributesSetGuid(IntPtr attributes, Guid key, Guid value)
        => ThrowIfFailed(VTable.CallSetGuid(attributes, key, value), "IMFAttributes.SetGUID");

    public static void AttributesSetUint64(IntPtr attributes, Guid key, ulong value)
        => ThrowIfFailed(VTable.CallSetUint64(attributes, key, value), "IMFAttributes.SetUINT64");

    public static void SinkAddStream(IntPtr sinkWriter, IntPtr mediaType, out int streamIndex)
        => ThrowIfFailed(VTable.CallAddStream(sinkWriter, mediaType, out streamIndex), "IMFSinkWriter.AddStream");

    public static void SinkSetInputMediaType(IntPtr sinkWriter, int streamIndex, IntPtr mediaType)
        => ThrowIfFailed(VTable.CallSetInputMediaType(sinkWriter, streamIndex, mediaType), "IMFSinkWriter.SetInputMediaType");

    public static void SinkBeginWriting(IntPtr sinkWriter)
        => ThrowIfFailed(VTable.CallBeginWriting(sinkWriter), "IMFSinkWriter.BeginWriting");

    public static void SinkWriteSample(IntPtr sinkWriter, int streamIndex, IntPtr sample)
        => ThrowIfFailed(VTable.CallWriteSample(sinkWriter, streamIndex, sample), "IMFSinkWriter.WriteSample");

    public static void SinkFinalize(IntPtr sinkWriter)
        => ThrowIfFailed(VTable.CallFinalize(sinkWriter), "IMFSinkWriter.Finalize");

    public static void SinkGetStatistics(IntPtr sinkWriter, int streamIndex, ref MfSinkWriterStatistics stats)
        => ThrowIfFailed(VTable.CallGetStatistics(sinkWriter, streamIndex, ref stats), "IMFSinkWriter.GetStatistics");

    public static void BufferLock(IntPtr buffer, out IntPtr data, out int maxLen, out int currentLen)
        => ThrowIfFailed(VTable.CallBufferLock(buffer, out data, out maxLen, out currentLen), "IMFMediaBuffer.Lock");

    public static void BufferUnlock(IntPtr buffer)
        => ThrowIfFailed(VTable.CallBufferUnlock(buffer), "IMFMediaBuffer.Unlock");

    public static void BufferSetCurrentLength(IntPtr buffer, int length)
        => ThrowIfFailed(VTable.CallBufferSetCurrentLength(buffer, length), "IMFMediaBuffer.SetCurrentLength");

    public static void SampleAddBuffer(IntPtr sample, IntPtr buffer)
        => ThrowIfFailed(VTable.CallSampleAddBuffer(sample, buffer), "IMFSample.AddBuffer");

    public static void SampleSetSampleTime(IntPtr sample, long time)
        => ThrowIfFailed(VTable.CallSampleSetSampleTime(sample, time), "IMFSample.SetSampleTime");

    public static void SampleSetSampleDuration(IntPtr sample, long duration)
        => ThrowIfFailed(VTable.CallSampleSetSampleDuration(sample, duration), "IMFSample.SetSampleDuration");

    [StructLayout(LayoutKind.Sequential)]
    public struct MfSinkWriterStatistics
    {
        public int cb;
        public long llLastTimestampReceived;
        public long llLastTimestampEncoded;
        public long llLastTimestampProcessed;
        public long llLastStreamTickReceived;
        public long llLastSinkSampleRequest;
        public long qwNumSamplesReceived;
        public long qwNumSamplesEncoded;
        public long qwNumSamplesProcessed;
        public long qwNumStreamTicksReceived;
        public int dwByteCountQueued;
        public long qwByteCountProcessed;
        public int dwNumOutstandingSinkSampleRequests;
        public int dwAverageSampleRateReceived;
        public int dwAverageSampleRateEncoded;
        public int dwAverageSampleRateProcessed;
    }

    private static class VTable
    {
        // IMFAttributes slots after IUnknown: GetItem=3 ... SetUINT32=21, SetUINT64=22, SetGUID=24
        private const int AttrSetUint32 = 21;
        private const int AttrSetUint64 = 22;
        private const int AttrSetGuid = 24;

        // IMFSinkWriter slots after IUnknown
        private const int SinkAddStream = 3;
        private const int SinkSetInput = 4;
        private const int SinkBegin = 5;
        private const int SinkWrite = 6;
        private const int SinkFinalize = 11;
        private const int SinkStats = 13;

        // IMFMediaBuffer after IUnknown
        private const int BufLock = 3;
        private const int BufUnlock = 4;
        private const int BufSetLen = 6;

        // IMFSample inherits IMFAttributes (33 methods after IUnknown?);
        // IMFAttributes has 32 methods (indices 3..34), then IMFSample adds GetSampleFlags at 35?
        // Count IMFAttributes methods from IUnknown:
        // 3 GetItem ... 32 CopyAllItems = 30 methods? Let's count carefully from MSDN:
        // After IUnknown (0-2): 
        // 3 GetItem, 4 GetItemType, 5 CompareItem, 6 Compare, 7 GetUINT32, 8 GetUINT64, 9 GetDouble,
        // 10 GetGUID, 11 GetStringLength, 12 GetString, 13 GetAllocatedString, 14 GetBlobSize, 15 GetBlob,
        // 16 GetAllocatedBlob, 17 GetUnknown, 18 SetItem, 19 DeleteItem, 20 DeleteAllItems,
        // 21 SetUINT32, 22 SetUINT64, 23 SetDouble, 24 SetGUID, 25 SetString, 26 SetBlob, 27 SetUnknown,
        // 28 LockStore, 29 UnlockStore, 30 GetCount, 31 GetItemByIndex, 32 CopyAllItems
        // IMFSample continues:
        // 33 GetSampleFlags, 34 SetSampleFlags, 35 GetSampleTime, 36 SetSampleTime,
        // 37 GetSampleDuration, 38 SetSampleDuration, 39 GetBufferCount, 40 GetBufferByIndex,
        // 41 ConvertToContiguousBuffer, 42 AddBuffer, ...
        private const int SampleSetTime = 36;
        private const int SampleSetDuration = 38;
        private const int SampleAddBuffer = 42;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUint32Delegate(IntPtr thisPtr, ref Guid key, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUint64Delegate(IntPtr thisPtr, ref Guid key, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetGuidDelegate(IntPtr thisPtr, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AddStreamDelegate(IntPtr thisPtr, IntPtr mediaType, out int streamIndex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetInputDelegate(IntPtr thisPtr, int streamIndex, IntPtr mediaType, IntPtr encodingParams);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrOnlyDelegate(IntPtr thisPtr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WriteSampleDelegate(IntPtr thisPtr, int streamIndex, IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetStatsDelegate(IntPtr thisPtr, int streamIndex, ref MfSinkWriterStatistics stats);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferLockDelegate(IntPtr thisPtr, out IntPtr data, out int maxLen, out int currentLen);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferUnlockDelegate(IntPtr thisPtr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferSetLenDelegate(IntPtr thisPtr, int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SampleLongDelegate(IntPtr thisPtr, long value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SampleAddBufferDelegate(IntPtr thisPtr, IntPtr buffer);

        private static IntPtr Slot(IntPtr obj, int index)
            => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), IntPtr.Size * index);

        public static int CallSetUint32(IntPtr obj, Guid key, int value)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<SetUint32Delegate>(Slot(obj, AttrSetUint32));
            Guid k = key;
            return fn(obj, ref k, value);
        }

        public static int CallSetUint64(IntPtr obj, Guid key, ulong value)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<SetUint64Delegate>(Slot(obj, AttrSetUint64));
            Guid k = key;
            return fn(obj, ref k, value);
        }

        public static int CallSetGuid(IntPtr obj, Guid key, Guid value)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<SetGuidDelegate>(Slot(obj, AttrSetGuid));
            Guid k = key;
            Guid v = value;
            return fn(obj, ref k, ref v);
        }

        public static int CallAddStream(IntPtr obj, IntPtr mediaType, out int streamIndex)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<AddStreamDelegate>(Slot(obj, SinkAddStream));
            return fn(obj, mediaType, out streamIndex);
        }

        public static int CallSetInputMediaType(IntPtr obj, int streamIndex, IntPtr mediaType)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<SetInputDelegate>(Slot(obj, SinkSetInput));
            return fn(obj, streamIndex, mediaType, IntPtr.Zero);
        }

        public static int CallBeginWriting(IntPtr obj)
            => Marshal.GetDelegateForFunctionPointer<HrOnlyDelegate>(Slot(obj, SinkBegin))(obj);

        public static int CallWriteSample(IntPtr obj, int streamIndex, IntPtr sample)
            => Marshal.GetDelegateForFunctionPointer<WriteSampleDelegate>(Slot(obj, SinkWrite))(obj, streamIndex, sample);

        public static int CallFinalize(IntPtr obj)
            => Marshal.GetDelegateForFunctionPointer<HrOnlyDelegate>(Slot(obj, SinkFinalize))(obj);

        public static int CallGetStatistics(IntPtr obj, int streamIndex, ref MfSinkWriterStatistics stats)
            => Marshal.GetDelegateForFunctionPointer<GetStatsDelegate>(Slot(obj, SinkStats))(obj, streamIndex, ref stats);

        public static int CallBufferLock(IntPtr obj, out IntPtr data, out int maxLen, out int currentLen)
            => Marshal.GetDelegateForFunctionPointer<BufferLockDelegate>(Slot(obj, BufLock))(obj, out data, out maxLen, out currentLen);

        public static int CallBufferUnlock(IntPtr obj)
            => Marshal.GetDelegateForFunctionPointer<BufferUnlockDelegate>(Slot(obj, BufUnlock))(obj);

        public static int CallBufferSetCurrentLength(IntPtr obj, int length)
            => Marshal.GetDelegateForFunctionPointer<BufferSetLenDelegate>(Slot(obj, BufSetLen))(obj, length);

        public static int CallSampleSetSampleTime(IntPtr obj, long time)
            => Marshal.GetDelegateForFunctionPointer<SampleLongDelegate>(Slot(obj, SampleSetTime))(obj, time);

        public static int CallSampleSetSampleDuration(IntPtr obj, long duration)
            => Marshal.GetDelegateForFunctionPointer<SampleLongDelegate>(Slot(obj, SampleSetDuration))(obj, duration);

        public static int CallSampleAddBuffer(IntPtr obj, IntPtr buffer)
            => Marshal.GetDelegateForFunctionPointer<SampleAddBufferDelegate>(Slot(obj, SampleAddBuffer))(obj, buffer);
    }
}
