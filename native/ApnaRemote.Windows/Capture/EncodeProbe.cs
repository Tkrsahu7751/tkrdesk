using System.IO;
using System.Runtime.InteropServices;
using static ApnaRemote.Windows.Capture.MediaFoundationInterop;

namespace ApnaRemote.Windows.Capture;

/// <summary>
/// Temporary diagnostic: try SinkWriter input types until one accepts.
/// Run: ApnaRemote.exe --encode-probe
/// </summary>
internal static class EncodeProbe
{
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-0e1dd010cfc0");
    private static readonly Guid MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_IYUV = new("56555949-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("44e0b5c1-c5db-41d5-8a5c-e5c0d6b6d782");

    public static int Run()
    {
        int code = 1;
        var thread = new Thread(() => code = RunMta())
        {
            IsBackground = false,
            Name = "ApnaRemote-EncodeProbe",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunMta()
    {
        Console.WriteLine("Apna Remote encode probe");
        ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
        try
        {
            RoundTripAttributes();
            TryCoCreateH264Encoder();
            Console.WriteLine("Probe finished.");
            return 0;
        }
        finally
        {
            MFShutdown();
        }
    }

    private static void RoundTripAttributes()
    {
        ThrowIfFailed(MFCreateMediaType(out IntPtr mt), "MFCreateMediaType(roundtrip)");
        try
        {
            AttributesSetGuid(mt, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            AttributesSetGuid(mt, MF_MT_SUBTYPE, MFVideoFormat_NV12);
            AttributesSetUint64(mt, MF_MT_FRAME_SIZE, PackSize(640, 360));
            AttributesSetUint32(mt, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

            Guid major = GetGuid(mt, MF_MT_MAJOR_TYPE);
            Guid sub = GetGuid(mt, MF_MT_SUBTYPE);
            ulong size = GetUint64(mt, MF_MT_FRAME_SIZE);
            int interlaced = GetUint32(mt, MF_MT_INTERLACE_MODE);
            Console.WriteLine($"RoundTrip major={major} sub={sub} size=0x{size:X16} interlace={interlaced}");
            Console.WriteLine($"Expected major={MFMediaType_Video} sub={MFVideoFormat_NV12}");
        }
        finally
        {
            Marshal.Release(mt);
        }
    }

    private static void TryCoCreateH264Encoder()
    {
        Guid clsid = new("6CA50344-051A-4DED-9779-A43305165E35"); // CLSID_CMSH264EncoderMFT
        Guid iidUnk = new("00000000-0000-0000-C000-000000000046");
        Guid iidFactory = new("00000001-0000-0000-C000-000000000046");
        Guid iidTransform = new("bf94c121-5b05-4e8f-9b63-657a6d1e2d4e"); // IID_IMFTransform

        int hrUnk = CoCreateInstance(in clsid, IntPtr.Zero, 1, in iidUnk, out IntPtr unk);
        Console.WriteLine($"CoCreate IUnknown hr=0x{hrUnk:X8} ptr={(unk != IntPtr.Zero)}");
        if (hrUnk >= 0 && unk != IntPtr.Zero)
        {
            Guid qi = iidTransform;
            int qiHr = Marshal.QueryInterface(unk, in qi, out IntPtr transform);
            Console.WriteLine($"QI IMFTransform hr=0x{qiHr:X8} ptr={(transform != IntPtr.Zero)}");
            if (transform != IntPtr.Zero)
            {
                Marshal.Release(transform);
            }

            Marshal.Release(unk);
        }

        int hrT = CoCreateInstance(in clsid, IntPtr.Zero, 1, in iidTransform, out IntPtr direct);
        Console.WriteLine($"CoCreate IMFTransform hr=0x{hrT:X8} ptr={(direct != IntPtr.Zero)}");
        if (direct != IntPtr.Zero)
        {
            Marshal.Release(direct);
        }

        IntPtr module = LoadLibrary("mfh264enc.dll");
        Console.WriteLine($"LoadLibrary mfh264enc={(module != IntPtr.Zero)} err={Marshal.GetLastWin32Error()}");
        if (module == IntPtr.Zero)
        {
            return;
        }

        IntPtr proc = GetProcAddress(module, "DllGetClassObject");
        if (proc == IntPtr.Zero)
        {
            Console.WriteLine("DllGetClassObject missing");
            return;
        }

        var dgco = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);
        Guid clsid2 = clsid;
        Guid factoryIid = iidFactory;
        int hrCf = dgco(ref clsid2, ref factoryIid, out IntPtr factory);
        Console.WriteLine($"DllGetClassObject hr=0x{hrCf:X8} factory={(factory != IntPtr.Zero)}");
        if (hrCf < 0 || factory == IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr createPtr = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), IntPtr.Size * 3);
            var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(createPtr);
            Guid tIid = iidTransform;
            int hrCreate = create(factory, IntPtr.Zero, ref tIid, out IntPtr transform2);
            Console.WriteLine($"ClassFactory.CreateInstance IMFTransform hr=0x{hrCreate:X8} ptr={(transform2 != IntPtr.Zero)}");
            if (transform2 != IntPtr.Zero)
            {
                try { DumpInputTypes(transform2); }
                finally { Marshal.Release(transform2); }
            }

            Guid unkIid = iidUnk;
            int hrUnkObj = create(factory, IntPtr.Zero, ref unkIid, out IntPtr unkObj);
            Console.WriteLine($"ClassFactory.CreateInstance IUnknown hr=0x{hrUnkObj:X8} ptr={(unkObj != IntPtr.Zero)}");
            if (unkObj != IntPtr.Zero)
            {
                try
                {
                    ProbeInterfaces(unkObj);
                }
                finally
                {
                    Marshal.Release(unkObj);
                }
            }

            // Intel Quick Sync H.264 Encoder MFT — skip deep QI (can AV on some drivers)
            Guid intel = new("4be8d3c0-0515-4a37-ad55-e4bae19af471");
            int hrIntel = CoCreateInstance(in intel, IntPtr.Zero, 1, in iidTransform, out IntPtr intelT);
            Console.WriteLine($"Intel QSV H264 CoCreate IMFTransform hr=0x{hrIntel:X8} ptr={(intelT != IntPtr.Zero)}");
            if (intelT != IntPtr.Zero)
            {
                Marshal.Release(intelT);
            }

            Guid decoder = new("62ce7e72-4c71-4d20-b15d-452831a87d9d"); // CMSH264DecoderMFT
            int hrDec = CoCreateInstance(in decoder, IntPtr.Zero, 1, in iidTransform, out IntPtr decT);
            Console.WriteLine($"H264 Decoder CoCreate IMFTransform hr=0x{hrDec:X8} ptr={(decT != IntPtr.Zero)}");
            if (decT != IntPtr.Zero) Marshal.Release(decT);

            Guid mpg2 = new("e6335f02-80b7-4dc4-adfa-dfe7210d20d5"); // Microsoft MPEG-2 Video Encoder
            int hrMpg = CoCreateInstance(in mpg2, IntPtr.Zero, 1, in iidTransform, out IntPtr mpgT);
            Console.WriteLine($"MPEG2 Encoder CoCreate IMFTransform hr=0x{hrMpg:X8} ptr={(mpgT != IntPtr.Zero)}");
            if (mpgT != IntPtr.Zero) Marshal.Release(mpgT);

            Guid wmv9 = new("d23b90d0-144f-46bd-841d-59e4eb19dc59"); // WMVideo9 Encoder
            int hrWmv = CoCreateInstance(in wmv9, IntPtr.Zero, 1, in iidTransform, out IntPtr wmvT);
            Console.WriteLine($"WMV9 Encoder CoCreate IMFTransform hr=0x{hrWmv:X8} ptr={(wmvT != IntPtr.Zero)}");
            if (wmvT != IntPtr.Zero) Marshal.Release(wmvT);

            // Local-register Microsoft H264 encoder into this process, then enum + activate + sink writer.
            Guid videoEncoderCat = MFT_CATEGORY_VIDEO_ENCODER;
            int regHr = MFTRegisterLocalByCLSID(in clsid, in videoEncoderCat, "ApnaRemoteLocalH264", 0, 0, IntPtr.Zero, 0, IntPtr.Zero);
            Console.WriteLine($"MFTRegisterLocalByCLSID hr=0x{regHr:X8}");
            EnumEncoders(0x00000017);

            if (!TryActivateEnumeratedEncoder(0x00000017))
            {
                Console.WriteLine("Activate enumerated encoder failed.");
            }

            try
            {
                TryOne(640, 360, MFVideoFormat_NV12, "NV12", hw: 0, profile: 77);
                Console.WriteLine("PASS SinkWriter after local register (NV12 640x360)");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL SinkWriter after local register: " + ex.Message);
            }

            try
            {
                TryOne(640, 360, MFVideoFormat_RGB32, "RGB32", hw: 0, profile: 77);
                Console.WriteLine("PASS SinkWriter after local register (RGB32 640x360)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL SinkWriter RGB32: " + ex.Message);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static bool TryActivateEnumeratedEncoder(uint flags)
    {
        int hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            flags,
            IntPtr.Zero,
            IntPtr.Zero,
            out IntPtr ppActivate,
            out int count);
        if (hr < 0 || count <= 0 || ppActivate == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr activate = Marshal.ReadIntPtr(ppActivate, 0);
            // IMFActivate::ActivateObject is slot 33? IMFAttributes has methods 3-32, then
            // IMFActivate: ActivateObject=33, ShutdownObject=34, DetachObject=35
            IntPtr fnPtr = Marshal.ReadIntPtr(Marshal.ReadIntPtr(activate), IntPtr.Size * 33);
            var activateObject = Marshal.GetDelegateForFunctionPointer<ActivateObjectDelegate>(fnPtr);
            Guid iidTransform = new("bf94c121-5b05-4e8f-9b63-657a6d1e2d4e");
            int ahr = activateObject(activate, ref iidTransform, out IntPtr transform);
            Console.WriteLine($"ActivateObject IMFTransform hr=0x{ahr:X8} ptr={(transform != IntPtr.Zero)}");
            if (transform != IntPtr.Zero)
            {
                try
                {
                    DumpInputTypes(transform);
                    return true;
                }
                finally
                {
                    Marshal.Release(transform);
                }
            }

            Guid iidUnk = new("00000000-0000-0000-C000-000000000046");
            ahr = activateObject(activate, ref iidUnk, out IntPtr unk);
            Console.WriteLine($"ActivateObject IUnknown hr=0x{ahr:X8} ptr={(unk != IntPtr.Zero)}");
            if (unk != IntPtr.Zero)
            {
                try { ProbeInterfaces(unk); }
                finally { Marshal.Release(unk); }
            }

            return false;
        }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(ppActivate, i * IntPtr.Size);
                if (activate != IntPtr.Zero)
                {
                    Marshal.Release(activate);
                }
            }

            Marshal.FreeCoTaskMem(ppActivate);
        }
    }

    private static void ProbeInterfaces(IntPtr unk)
    {
        (string name, Guid iid)[] list =
        [
            ("IMFActivate", new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")),
            ("IMFTransform", new("bf94c121-5b05-4e8f-9b63-657a6d1e2d4e")),
            ("IMFAttributes", new("2cd2d921-c447-44a7-a13c-4adabfc247e3")),
            ("IMFMediaEventGenerator", new("2a2c7c19-4eeb-4645-a1d0-67d976c1b632")),
            ("IMediaObject", new("d8ad0f58-5494-4102-97c5-ec798e59bcf4")),
            ("ICodecAPI", new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")),
            ("IMFRealTimeClientEx", new("03910848-bc48-4243-a13e-6f4046e1756e")),
            ("IMFFieldOfUseMFTUnlock", new("08cdc9d1-0c1c-4321-a206-38360f0a1a68")),
        ];

        foreach ((string name, Guid iid) in list)
        {
            Guid g = iid;
            int hr = Marshal.QueryInterface(unk, in g, out IntPtr p);
            Console.WriteLine($"  QI {name} hr=0x{hr:X8} ptr={(p != IntPtr.Zero)}");
            if (p != IntPtr.Zero)
            {
                Marshal.Release(p);
            }
        }

        // Read friendly attributes if IMFAttributes works.
        Guid attrIid = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
        if (Marshal.QueryInterface(unk, in attrIid, out IntPtr attrs) >= 0 && attrs != IntPtr.Zero)
        {
            try
            {
                Guid transformClsidKey = new("6821c54b-bd59-403b-978e-bdfef20e5057"); // MFT_TRANSFORM_CLSID_Attribute
                Guid friendlyKey = new("87e0f56f-6c54-4b86-b077-0cda0000745b"); // MFT_FRIENDLY_NAME_Attribute
                try
                {
                    Guid clsid = GetGuid(attrs, transformClsidKey);
                    Console.WriteLine("  attr MFT_TRANSFORM_CLSID=" + clsid);
                }
                catch (Exception ex) { Console.WriteLine("  attr CLSID read fail: " + ex.Message); }

                try
                {
                    // GetAllocatedString slot 13
                    IntPtr fnPtr = Marshal.ReadIntPtr(Marshal.ReadIntPtr(attrs), IntPtr.Size * 13);
                    var fn = Marshal.GetDelegateForFunctionPointer<GetAllocatedStringDelegate>(fnPtr);
                    Guid k = friendlyKey;
                    if (fn(attrs, ref k, out IntPtr str, out _) >= 0 && str != IntPtr.Zero)
                    {
                        Console.WriteLine("  attr FRIENDLY_NAME=" + Marshal.PtrToStringUni(str));
                        Marshal.FreeCoTaskMem(str);
                    }
                }
                catch (Exception ex) { Console.WriteLine("  attr name read fail: " + ex.Message); }
            }
            finally
            {
                Marshal.Release(attrs);
            }
        }
    }

    private static void DumpInputTypes(IntPtr transform)
    {
        int typeIndex = 0;
        while (typeIndex < 8)
        {
            int thr = TransformGetInputAvailableType(transform, 0, typeIndex, out IntPtr type);
            if (thr < 0 || type == IntPtr.Zero)
            {
                Console.WriteLine($"  inputAvailable[{typeIndex}] hr=0x{thr:X8}");
                break;
            }

            try
            {
                Guid sub = GetGuid(type, MF_MT_SUBTYPE);
                Console.WriteLine($"  inputAvailable[{typeIndex}] subtype={sub}");
            }
            finally
            {
                Marshal.Release(type);
            }

            typeIndex++;
        }
    }

    private static Guid GetGuid(IntPtr attrs, Guid key)
    {
        IntPtr vtable = Marshal.ReadIntPtr(attrs);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 10); // GetGUID
        var fn = Marshal.GetDelegateForFunctionPointer<GetGuidDelegate>(fnPtr);
        Guid k = key;
        ThrowIfFailed(fn(attrs, ref k, out Guid value), "GetGUID");
        return value;
    }

    private static ulong GetUint64(IntPtr attrs, Guid key)
    {
        IntPtr vtable = Marshal.ReadIntPtr(attrs);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 8); // GetUINT64
        var fn = Marshal.GetDelegateForFunctionPointer<GetUint64Delegate>(fnPtr);
        Guid k = key;
        ThrowIfFailed(fn(attrs, ref k, out ulong value), "GetUINT64");
        return value;
    }

    private static int GetUint32(IntPtr attrs, Guid key)
    {
        IntPtr vtable = Marshal.ReadIntPtr(attrs);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7); // GetUINT32
        var fn = Marshal.GetDelegateForFunctionPointer<GetUint32Delegate>(fnPtr);
        Guid k = key;
        ThrowIfFailed(fn(attrs, ref k, out int value), "GetUINT32");
        return value;
    }

    private static int TransformGetInputAvailableType(IntPtr transform, int streamId, int typeIndex, out IntPtr type)
    {
        // IMFTransform::GetInputAvailableType is vtable slot 8 (after IUnknown)
        IntPtr vtable = Marshal.ReadIntPtr(transform);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 8);
        var fn = Marshal.GetDelegateForFunctionPointer<GetInputAvailableTypeDelegate>(fnPtr);
        return fn(transform, streamId, typeIndex, out type);
    }

    private static void EnumEncoders(uint flags = 0x00000017)
    {
        try
        {
            int hr = MFTEnumEx(
                MFT_CATEGORY_VIDEO_ENCODER,
                flags,
                IntPtr.Zero,
                IntPtr.Zero,
                out IntPtr ppActivate,
                out int count);
            Console.WriteLine($"MFTEnumEx video encoders flags=0x{flags:X8} hr=0x{hr:X8} count={count}");
            if (hr < 0 || ppActivate == IntPtr.Zero || count <= 0)
            {
                return;
            }

            try
            {
                for (int i = 0; i < Math.Min(count, 12); i++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(ppActivate, i * IntPtr.Size);
                    string name = TryGetActivateString(activate, new Guid("87e0f56f-6c54-4b86-b077-0cda0000745b")) // MFT_FRIENDLY_NAME_Attribute
                                  ?? "(unnamed)";
                    Console.WriteLine("  encoder[" + i + "]: " + name);
                }
            }
            finally
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(ppActivate, i * IntPtr.Size);
                    if (activate != IntPtr.Zero)
                    {
                        Marshal.Release(activate);
                    }
                }

                Marshal.FreeCoTaskMem(ppActivate);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("MFTEnumEx failed: " + ex.Message);
        }
    }

    private static string? TryGetActivateString(IntPtr activate, Guid key)
    {
        try
        {
            // IMFAttributes.GetAllocatedString = vtable index 13
            IntPtr vtable = Marshal.ReadIntPtr(activate);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 13);
            var fn = Marshal.GetDelegateForFunctionPointer<GetAllocatedStringDelegate>(fnPtr);
            Guid k = key;
            int hr = fn(activate, ref k, out IntPtr str, out _);
            if (hr < 0 || str == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(str);
            }
            finally
            {
                Marshal.FreeCoTaskMem(str);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void TryOne(int width, int height, Guid subtype, string subtypeName, int hw, int profile)
    {
        string path = Path.Combine(Path.GetTempPath(), "ApnaRemote", "capture-lab", "probe-" + Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ThrowIfFailed(MFCreateAttributes(out IntPtr attrs, 2), "MFCreateAttributes");
        IntPtr writer = IntPtr.Zero;
        try
        {
            AttributesSetUint32(attrs, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hw);
            AttributesSetUint32(attrs, MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            writer = CreateSinkWriter(path, attrs);

            ThrowIfFailed(MFCreateMediaType(out IntPtr outputType), "out type");
            try
            {
                AttributesSetGuid(outputType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                AttributesSetGuid(outputType, MF_MT_SUBTYPE, MFVideoFormat_H264);
                AttributesSetUint32(outputType, MF_MT_AVG_BITRATE, 2_000_000);
                AttributesSetUint32(outputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                AttributesSetUint64(outputType, MF_MT_FRAME_SIZE, PackSize(width, height));
                AttributesSetUint64(outputType, MF_MT_FRAME_RATE, PackRatio(30, 1));
                AttributesSetUint64(outputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
                if (profile != 0)
                {
                    AttributesSetUint32(outputType, MF_MT_MPEG2_PROFILE, profile);
                }

                SinkAddStream(writer, outputType, out int streamIndex);

                ThrowIfFailed(MFCreateMediaType(out IntPtr inputType), "in type");
                try
                {
                    AttributesSetGuid(inputType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                    AttributesSetGuid(inputType, MF_MT_SUBTYPE, subtype);
                    AttributesSetUint32(inputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                    AttributesSetUint64(inputType, MF_MT_FRAME_SIZE, PackSize(width, height));
                    AttributesSetUint64(inputType, MF_MT_FRAME_RATE, PackRatio(30, 1));
                    AttributesSetUint64(inputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
                    if (subtypeName is "NV12" or "IYUV")
                    {
                        AttributesSetUint32(inputType, MF_MT_DEFAULT_STRIDE, width);
                        AttributesSetUint32(inputType, MF_MT_SAMPLE_SIZE, (width * height * 3) / 2);
                        AttributesSetUint32(inputType, MF_MT_FIXED_SIZE_SAMPLES, 1);
                        AttributesSetUint32(inputType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                    }
                    else if (subtypeName == "YUY2")
                    {
                        AttributesSetUint32(inputType, MF_MT_DEFAULT_STRIDE, width * 2);
                        AttributesSetUint32(inputType, MF_MT_SAMPLE_SIZE, width * height * 2);
                        AttributesSetUint32(inputType, MF_MT_FIXED_SIZE_SAMPLES, 1);
                        AttributesSetUint32(inputType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                    }
                    else
                    {
                        AttributesSetUint32(inputType, MF_MT_DEFAULT_STRIDE, width * 4);
                        AttributesSetUint32(inputType, MF_MT_SAMPLE_SIZE, width * height * 4);
                        AttributesSetUint32(inputType, MF_MT_FIXED_SIZE_SAMPLES, 1);
                        AttributesSetUint32(inputType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                    }

                    SinkSetInputMediaType(writer, streamIndex, inputType);
                    SinkBeginWriting(writer);
                }
                finally
                {
                    Marshal.Release(inputType);
                }
            }
            finally
            {
                Marshal.Release(outputType);
            }
        }
        finally
        {
            if (writer != IntPtr.Zero)
            {
                Marshal.Release(writer);
            }

            Marshal.Release(attrs);
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int MFTEnumEx(
        in Guid guidCategory,
        uint flags,
        IntPtr pInputType,
        IntPtr pOutputType,
        out IntPtr pppMFTActivate,
        out int pcMFTActivate);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int MFTRegisterLocalByCLSID(
        in Guid clsidMFT,
        in Guid guidCategory,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        uint Flags,
        uint cInputTypes,
        IntPtr pInputTypes,
        uint cOutputTypes,
        IntPtr pOutputTypes);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectDelegate(ref Guid clsid, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInstanceDelegate(IntPtr self, IntPtr outer, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateObjectDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAllocatedStringDelegate(IntPtr thisPtr, ref Guid key, out IntPtr value, out int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidDelegate(IntPtr thisPtr, ref Guid key, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUint64Delegate(IntPtr thisPtr, ref Guid key, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUint32Delegate(IntPtr thisPtr, ref Guid key, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInputAvailableTypeDelegate(IntPtr thisPtr, int dwInputStreamID, int dwTypeIndex, out IntPtr ppType);
}
