using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LatentFolio.Vsr;

/// <summary>
/// RTX Video Super Resolution，直接 P/Invoke NVIDIA Video Effects SDK。
///
/// 关键发现（实测，见 tools/vsr-ctypes.py / vsr-probe2.py）：
///   · selector 就是 "VideoSuperRes"
///   · 8 位 BGRA + CPU_PINNED 内存可以直接喂，不需要 CUDA 张量
///   · QualityLevel=19 是 HIGHBITRATE_ULTRA（对干净的 AI 图不涂抹）
///   · Load() 一次要 ~2.9 s，但**之后换任意尺寸直接 Run 就行**（16~38 ms），
///     所以整个进程只 Load 一次。
/// </summary>
public sealed unsafe class VsrEngine : IDisposable
{
    public const int QualityHighBitrateUltra = 19;

    /// <summary>档位分组（值来自 NVIDIA Video Effects SDK 的 QualityLevel）。</summary>
    public static readonly (string Name, int[] Values)[] QualityGroups =
    [
        ("放大", [1, 2, 3, 4]),           // LOW / MEDIUM / HIGH / ULTRA
        ("降噪", [8, 9, 10, 11]),         // DENOISE_*
        ("去模糊", [12, 13, 14, 15]),     // DEBLUR_*
        ("高清放大", [16, 17, 18, 19]),   // HIGHBITRATE_*：干净/无损源，不压细节
        ("直播", [-1, -1, 21, 23]),       // 只有 MEDIUM / ULTRA
    ];

    public static string QualityName(int level) => level switch
    {
        0 => "BICUBIC",
        1 => "LOW",
        2 => "MEDIUM",
        3 => "HIGH",
        4 => "ULTRA",
        8 => "DENOISE_LOW",
        9 => "DENOISE_MEDIUM",
        10 => "DENOISE_HIGH",
        11 => "DENOISE_ULTRA",
        12 => "DEBLUR_LOW",
        13 => "DEBLUR_MEDIUM",
        14 => "DEBLUR_HIGH",
        15 => "DEBLUR_ULTRA",
        16 => "HIGHBITRATE_LOW",
        17 => "HIGHBITRATE_MEDIUM",
        18 => "HIGHBITRATE_HIGH",
        19 => "HIGHBITRATE_ULTRA",
        21 => "STREAMING_MEDIUM",
        23 => "STREAMING_ULTRA",
        _ => $"LEVEL_{level}",
    };

    /// <summary>
    /// 降噪 / 去模糊是「同分辨率」处理（输出尺寸 = 输入尺寸），
    /// 放大 / 高清放大 / 直播是 2× 放大。
    /// </summary>
    public static bool IsSameRes(int level) => level is >= 8 and <= 15;

    /// <summary>与输入相同的倍数（同分辨率模式返回 1，其余 2）。</summary>
    public static int BaseOutScale(int level) => IsSameRes(level) ? 1 : 2;


    // NvCVImage_PixelFormat / ComponentType / memory space
    private const int NVCV_RGBA = 6;
    private const int NVCV_BGRA = 7;
    private const int NVCV_U8 = 1;
    private const int NVCV_CHUNKY = 0;
    private const int NVCV_CPU_PINNED = 2;

    private const int NVCV_SUCCESS = 0;

    private static readonly string[] Dependencies =
    [
        "cudart64_12.dll",
        "NVCVImage.dll",
        "nvngxruntime.dll",
        "NVVideoEffects.dll",
        "nvVFXVideoSuperRes.dll",
        "nvngx_vsr.dll",
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct NvCVImage
    {
        public uint width;
        public uint height;
        public int pitch;
        public int pixelFormat;
        public int componentType;
        public byte pixelBytes;
        public byte componentBytes;
        public byte numComponents;
        public byte planar;
        public byte gpuMem;
        public byte colorspace;
        public byte r0;
        public byte r1;
        public IntPtr pixels;
        public IntPtr deletePtr;
        public IntPtr deleteProc;
        public ulong bufferBytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnCreateEffect([MarshalAs(UnmanagedType.LPUTF8Str)] string selector, out IntPtr effect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnSetU32(IntPtr effect, [MarshalAs(UnmanagedType.LPUTF8Str)] string param, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnSetF32(IntPtr effect, [MarshalAs(UnmanagedType.LPUTF8Str)] string param, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnSetImage(IntPtr effect, [MarshalAs(UnmanagedType.LPUTF8Str)] string param, NvCVImage* image);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnLoad(IntPtr effect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnRun(IntPtr effect, int async);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnDestroy(IntPtr effect);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnAlloc(NvCVImage* im, uint w, uint h, int format, int type, uint layout, uint memSpace, uint align);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnDealloc(NvCVImage* im);

    private FnCreateEffect _createEffect = null!;
    private FnSetU32 _setU32 = null!;
    private FnSetF32 _setF32 = null!;
    private FnSetImage _setImage = null!;
    private FnLoad _load = null!;
    private FnRun _run = null!;
    private FnDestroy _destroy = null!;
    private FnAlloc _alloc = null!;
    private FnDealloc _dealloc = null!;

    private readonly List<IntPtr> _handles = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _effect;
    private NvCVImage* _src;
    private NvCVImage* _dst;
    private uint _srcW, _srcH, _dstW, _dstH;
    private bool _loaded;
    private bool _reloadPending;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "未初始化";
    public string LibDir { get; set; } = "";
    public double LastRunMs { get; private set; }
    public int ProcessedCount { get; private set; }

    /// <summary>当前档位（QualityLevel）。默认 19 = HIGHBITRATE_ULTRA，对干净的 AI 图不涂抹。</summary>
    public int Quality { get; private set; } = QualityHighBitrateUltra;

    /// <summary>
    /// 换档位。模型是按档位挑的，所以要让 SDK 再 Load 一次（实测 3.6 s）；
    /// 这一步放到下一次 Process 的后台线程里做，设置界面的点击不能卡住 3.6 秒。
    /// </summary>
    public bool SetQuality(int level)
    {
        if (Quality == level) return true;
        Quality = level;
        if (!_loaded || _effect == IntPtr.Zero) return true;
        if (_setU32(_effect, "QualityLevel", (uint)level) != NVCV_SUCCESS) return false;
        _reloadPending = true;
        Status = $"就绪 · 档位 {QualityName(level)}";
        return true;
    }

    /// <summary>输出的最大像素数，防止 8K 图把内存撑爆。</summary>
    public const long MaxOutputPixels = 36_000_000;

    public static IEnumerable<string> CandidateLibDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "vsr");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatentFolio", "vsr");

        // ComfyUI + nvvfx（NVIDIA 官方 python 包自带全部依赖）
        foreach (var drive in new[] { "D:", "C:", "E:" })
        {
            foreach (var root in new[] { @"\Recreation\ComfyUI-aki-v3", @"\ComfyUI", @"\AI" })
            {
                var p = drive + root;
                if (!Directory.Exists(p)) continue;
                yield return Path.Combine(p, @"python\Lib\site-packages\nvvfx\libs");
            }
        }
    }

    /// <summary>找一个可用的库目录，必要时把 6 个 DLL 拷到本地缓存。</summary>
    public static string ResolveLibDir(out string note)
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatentFolio", "vsr");

        if (AllPresent(local)) { note = "使用本地缓存"; return local; }

        foreach (var dir in CandidateLibDirs())
        {
            if (!AllPresent(dir)) continue;
            try
            {
                Directory.CreateDirectory(local);
                foreach (var f in Dependencies)
                    File.Copy(Path.Combine(dir, f), Path.Combine(local, f), overwrite: true);
                note = $"已从 {dir} 复制依赖";
                return local;
            }
            catch (Exception ex)
            {
                note = "复制失败：" + ex.Message;
                return dir;
            }
        }

        note = "找不到 NVVideoEffects.dll 及其依赖";
        return "";
    }

    private static bool AllPresent(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        foreach (var f in Dependencies)
            if (!File.Exists(Path.Combine(dir, f))) return false;
        return true;
    }

    /// <summary>
    /// 只看依赖文件在不在，不加载 DLL、不建 CUDA 上下文。
    /// 用来决定右键菜单里 VSR 那一项亮不亮 —— 真正 Load 一次要 3 秒、几百 MB 显存，
    /// 没点它之前不该付这个代价。
    /// </summary>
    public static bool Probe(out string dir)
    {
        foreach (var d in CandidateLibDirs())
        {
            if (!AllPresent(d)) continue;
            dir = d;
            return true;
        }
        dir = "";
        return false;
    }

    /// <summary>加载 DLL、建 effect、一次性 Load。调用要放到后台线程。</summary>
    public bool Start()
    {
        if (_loaded) return true;
        var sw = Stopwatch.StartNew();
        try
        {
            var dir = ResolveLibDir(out var note);
            LibDir = dir;
            if (dir.Length == 0) { Status = note; Available = false; return false; }

            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            SetDllDirectory(dir);

            IntPtr vfx = IntPtr.Zero, cv = IntPtr.Zero;
            foreach (var name in Dependencies)
            {
                var full = Path.Combine(dir, name);
                var h = NativeLibrary.Load(full);
                _handles.Add(h);
                if (name == "NVVideoEffects.dll") vfx = h;
                else if (name == "NVCVImage.dll") cv = h;
            }

            _createEffect = Bind<FnCreateEffect>(vfx, "NvVFX_CreateEffect");
            _setU32 = Bind<FnSetU32>(vfx, "NvVFX_SetU32");
            _setF32 = Bind<FnSetF32>(vfx, "NvVFX_SetF32");
            _setImage = Bind<FnSetImage>(vfx, "NvVFX_SetImage");
            _load = Bind<FnLoad>(vfx, "NvVFX_Load");
            _run = Bind<FnRun>(vfx, "NvVFX_Run");
            _destroy = Bind<FnDestroy>(vfx, "NvVFX_DestroyEffect");
            _alloc = Bind<FnAlloc>(cv, "NvCVImage_Alloc");
            _dealloc = Bind<FnDealloc>(cv, "NvCVImage_Dealloc");

            int st = _createEffect("VideoSuperRes", out _effect);
            if (st != NVCV_SUCCESS || _effect == IntPtr.Zero)
            {
                Status = $"CreateEffect 失败 ({st})";
                Available = false;
                return false;
            }

            _setU32(_effect, "QualityLevel", (uint)Quality);
            _setU32(_effect, "ImageEncodingMode", 0);   // 8-bit RGBA/BGRA
            _setF32(_effect, "Strength", 1.0f);

            // 预热：拿一张 512x512 占位图 Load 一次，之后换尺寸不用再 Load
            _src = (NvCVImage*)NativeMemory.AllocZeroed((nuint)sizeof(NvCVImage));
            _dst = (NvCVImage*)NativeMemory.AllocZeroed((nuint)sizeof(NvCVImage));
            if (!Realloc(ref _src, 512, 512, ref _srcW, ref _srcH)) { Status = "分配输入缓冲失败"; return false; }
            if (!Realloc(ref _dst, 1024, 1024, ref _dstW, ref _dstH)) { Status = "分配输出缓冲失败"; return false; }
            new Span<byte>((void*)_src->pixels, (int)(_src->pitch * 512)).Clear();

            _setImage(_effect, "SrcImage0", _src);
            _setImage(_effect, "DstImage0", _dst);

            var t0 = Stopwatch.StartNew();
            st = _load(_effect);
            t0.Stop();
            if (st != NVCV_SUCCESS)
            {
                Status = $"Load 失败 ({st})";
                Available = false;
                return false;
            }

            _loaded = true;
            Available = true;
            Status = $"就绪 · 预热 {t0.ElapsedMilliseconds} ms · 档位 {QualityName(Quality)}";
            sw.Stop();
            return true;
        }
        catch (Exception ex)
        {
            Status = "初始化异常：" + ex.Message;
            Available = false;
            return false;
        }
    }

    private static T Bind<T>(IntPtr lib, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private bool Realloc(ref NvCVImage* img, uint w, uint h, ref uint curW, ref uint curH)
    {
        if (img != null && curW == w && curH == h) return true;
        if (img != null) { _dealloc(img); img = null; }
        var p = (NvCVImage*)NativeMemory.AllocZeroed((nuint)sizeof(NvCVImage));
        int st = _alloc(p, w, h, NVCV_BGRA, NVCV_U8, NVCV_CHUNKY, NVCV_CPU_PINNED, 0);
        if (st != NVCV_SUCCESS)
        {
            NativeMemory.Free(p);
            return false;
        }
        img = p;
        curW = w;
        curH = h;
        return true;
    }

    public sealed record Result(byte[] Bgra, int Width, int Height, double Ms);

    /// <summary>
    /// 处理一张图。8 位 BGRA8888 进、出新缓冲。
    /// 降噪 / 去模糊是同分辨率（输出 = 输入尺寸），其余是 2×。
    /// 线程安全（内部串行）。
    /// </summary>
    public Result? Process(byte[] bgra, int width, int height)
    {
        if (!_loaded || !Available) return null;
        if (width <= 0 || height <= 0) return null;

        int k = BaseOutScale(Quality);
        int outW = width * k, outH = height * k;
        if ((long)outW * outH > MaxOutputPixels) return null;

        _gate.Wait();
        try
        {
            // 换过档位：模型得重新 Load，放在这里做（调用方是后台线程）
            if (_reloadPending)
            {
                _reloadPending = false;
                int rst = _load(_effect);
                if (rst != NVCV_SUCCESS)
                {
                    Available = false;
                    Status = $"换档位后 Load 失败 ({rst})";
                    return null;
                }
            }

            if (!Realloc(ref _src, (uint)width, (uint)height, ref _srcW, ref _srcH)) return null;
            if (!Realloc(ref _dst, (uint)outW, (uint)outH, ref _dstW, ref _dstH)) return null;

            int srcBytes = width * 4 * height;
            if (bgra.Length < srcBytes) return null;

            // 逐行拷（pitch 可能大于 width*4）
            long need = (long)_src->pitch * height;
            if (need <= 0) return null;
            fixed (byte* p = bgra)
            {
                int row = width * 4;
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(p + (long)y * row, (byte*)_src->pixels + (long)y * _src->pitch, row, row);
                }
            }

            if (_setImage(_effect, "SrcImage0", _src) != NVCV_SUCCESS) return null;
            if (_setImage(_effect, "DstImage0", _dst) != NVCV_SUCCESS) return null;

            var sw = Stopwatch.StartNew();
            int st = _run(_effect, 0);
            sw.Stop();
            if (st != NVCV_SUCCESS) return null;
            LastRunMs = sw.Elapsed.TotalMilliseconds;

            var outBuf = new byte[outW * 4 * outH];
            fixed (byte* p = outBuf)
            {
                int row = outW * 4;
                for (int y = 0; y < outH; y++)
                    Buffer.MemoryCopy((byte*)_dst->pixels + (long)y * _dst->pitch, p + (long)y * row, row, row);
            }

            ProcessedCount++;
            return new Result(outBuf, outW, outH, LastRunMs);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_effect != IntPtr.Zero) _destroy(_effect);
            _effect = IntPtr.Zero;
            if (_src != null) { _dealloc(_src); NativeMemory.Free(_src); _src = null; }
            if (_dst != null) { _dealloc(_dst); NativeMemory.Free(_dst); _dst = null; }
        }
        catch { }
        foreach (var h in _handles) { try { NativeLibrary.Free(h); } catch { } }
        _handles.Clear();
        _gate.Dispose();
    }
}
