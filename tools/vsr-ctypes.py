"""用 ctypes 直连 NVVideoEffects.dll 验证 VSR 通路（不经过 nvvfx 的 Python 封装）。

要验证的事：
  1. CreateEffect 的 selector 是不是 "VideoSuperRes"
  2. 8 位 BGRA + CPU_PINNED 能不能直接喂进去（省掉 float32/CUDA 张量转换）
  3. HIGHBITRATE_ULTRA（19）能不能用
  4. 换一组尺寸时重新 Load 要多久 —— 决定要不要按尺寸缓存 effect
"""
import ctypes as C
import sys
import time
from pathlib import Path

LIBS = Path(r'D:\Recreation\ComfyUI-aki-v3\python\Lib\site-packages\nvvfx\libs')
OUT = Path(__file__).parent.parent / 'vsr_out'
OUT.mkdir(exist_ok=True)

# 先把依赖目录加进 DLL 搜索路径，并按依赖顺序预加载（和 nvvfx 的 _lib_loader 一致）
import os
os.add_dll_directory(str(LIBS))
os.environ['PATH'] = str(LIBS) + ';' + os.environ.get('PATH', '')

_PRELOAD = [
    'cudart64_12.dll',
    'nppc64_12.dll', 'nppial64_12.dll', 'nppicc64_12.dll', 'nppidei64_12.dll',
    'nppif64_12.dll', 'nppig64_12.dll', 'nppim64_12.dll', 'nppist64_12.dll', 'nppitc64_12.dll',
    'NVCVImage.dll', 'nvngxruntime.dll', 'NVVideoEffects.dll',
    'nvVFXVideoSuperRes.dll', 'nvngx_vsr.dll',
]
for _n in _PRELOAD:
    _p = LIBS / _n
    if not _p.exists():
        continue
    try:
        C.WinDLL(str(_p))
        print('  preload ok  ', _n)
    except OSError as e:
        print('  preload FAIL', _n, e)

NVCV_BGRA, NVCV_RGBA = 7, 6
NVCV_U8 = 1
NVCV_CHUNKY = 0
NVCV_CPU, NVCV_CUDA, NVCV_CPU_PINNED = 0, 1, 2


class NvCVImage(C.Structure):
    _fields_ = [
        ('width', C.c_uint), ('height', C.c_uint), ('pitch', C.c_int),
        ('pixelFormat', C.c_int), ('componentType', C.c_int),
        ('pixelBytes', C.c_ubyte), ('componentBytes', C.c_ubyte), ('numComponents', C.c_ubyte),
        ('planar', C.c_ubyte), ('gpuMem', C.c_ubyte), ('colorspace', C.c_ubyte),
        ('reserved', C.c_ubyte * 2),
        ('pixels', C.c_void_p), ('deletePtr', C.c_void_p), ('deleteProc', C.c_void_p),
        ('bufferBytes', C.c_ulonglong),
    ]


print('sizeof(NvCVImage) =', C.sizeof(NvCVImage))

vfx = C.WinDLL(str(LIBS / 'NVVideoEffects.dll'))
cv = C.WinDLL(str(LIBS / 'NVCVImage.dll'))

vfx.NvVFX_CreateEffect.argtypes = [C.c_char_p, C.POINTER(C.c_void_p)]
vfx.NvVFX_CreateEffect.restype = C.c_int
vfx.NvVFX_SetU32.argtypes = [C.c_void_p, C.c_char_p, C.c_uint]
vfx.NvVFX_SetU32.restype = C.c_int
vfx.NvVFX_SetF32.argtypes = [C.c_void_p, C.c_char_p, C.c_float]
vfx.NvVFX_SetF32.restype = C.c_int
vfx.NvVFX_SetImage.argtypes = [C.c_void_p, C.c_char_p, C.POINTER(NvCVImage)]
vfx.NvVFX_SetImage.restype = C.c_int
vfx.NvVFX_Load.argtypes = [C.c_void_p]
vfx.NvVFX_Load.restype = C.c_int
vfx.NvVFX_Run.argtypes = [C.c_void_p, C.c_int]
vfx.NvVFX_Run.restype = C.c_int
vfx.NvVFX_DestroyEffect.argtypes = [C.c_void_p]
vfx.NvVFX_DestroyEffect.restype = C.c_int

cv.NvCVImage_Alloc.argtypes = [C.POINTER(NvCVImage), C.c_uint, C.c_uint, C.c_int, C.c_int, C.c_uint, C.c_uint, C.c_uint]
cv.NvCVImage_Alloc.restype = C.c_int
cv.NvCVImage_Dealloc.argtypes = [C.POINTER(NvCVImage)]
cv.NvCVImage_Dealloc.restype = C.c_int


def status(code, what):
    if code != 0:
        print(f'  !! {what} 失败 status={code}')
    return code


h = C.c_void_p()
print('CreateEffect...')
st = status(vfx.NvVFX_CreateEffect(b'VideoSuperRes', C.byref(h)), 'CreateEffect')
print('  handle =', h.value)
if st != 0 or not h.value:
    sys.exit(1)

print('set params')
status(vfx.NvVFX_SetU32(h, b'QualityLevel', 19), 'QualityLevel')      # HIGHBITRATE_ULTRA
status(vfx.NvVFX_SetU32(h, b'ImageEncodingMode', 0), 'ImageEncodingMode')  # 8-bit
status(vfx.NvVFX_SetF32(h, b'Strength', 1.0), 'Strength')

src_img = Path(r'D:\Recreation\ComfyUI-aki-v3\ComfyUI\output\2025-09-15\ComfyUI_00001_.png')
from PIL import Image
img = Image.open(src_img).convert('RGBA')
W, H = img.size
OW, OH = W * 2, H * 2
print(f'input {W}x{H} -> {OW}x{OH}')

raw = img.tobytes()     # RGBA 交错

src = NvCVImage()
dst = NvCVImage()
status(cv.NvCVImage_Alloc(C.byref(src), W, H, NVCV_RGBA, NVCV_U8, NVCV_CHUNKY, NVCV_CPU_PINNED, 0), 'alloc src')
status(cv.NvCVImage_Alloc(C.byref(dst), OW, OH, NVCV_RGBA, NVCV_U8, NVCV_CHUNKY, NVCV_CPU_PINNED, 0), 'alloc dst')
print('  src pitch', src.pitch, 'dst pitch', dst.pitch, 'gpuMem', src.gpuMem, dst.gpuMem)
C.memmove(src.pixels, raw, len(raw))

status(vfx.NvVFX_SetImage(h, b'SrcImage0', C.byref(src)), 'SetImage src')
status(vfx.NvVFX_SetImage(h, b'DstImage0', C.byref(dst)), 'SetImage dst')

t0 = time.perf_counter()
status(vfx.NvVFX_Load(h), 'Load')
t1 = time.perf_counter()
print(f'  Load 用时 {(t1 - t0) * 1000:.0f} ms')

t0 = time.perf_counter()
status(vfx.NvVFX_Run(h, 0), 'Run')
t1 = time.perf_counter()
print(f'  Run 用时 {(t1 - t0) * 1000:.1f} ms')

buf = C.string_at(dst.pixels, dst.pitch * OH)
out = Image.frombytes('RGBA', (OW, OH), buf[:OW * OH * 4])
p = OUT / 'ctypes-hbultra.png'
out.save(p)
print('  写出', p)

# 换尺寸测第二次 Load
img2 = Image.open(src_img).convert('RGBA').resize((1024, 1024))
W2, H2 = img2.size
raw2 = img2.tobytes()
cv.NvCVImage_Dealloc(C.byref(src))
cv.NvCVImage_Dealloc(C.byref(dst))
status(cv.NvCVImage_Alloc(C.byref(src), W2, H2, NVCV_RGBA, NVCV_U8, NVCV_CHUNKY, NVCV_CPU_PINNED, 0), 'alloc src2')
status(cv.NvCVImage_Alloc(C.byref(dst), W2 * 2, H2 * 2, NVCV_RGBA, NVCV_U8, NVCV_CHUNKY, NVCV_CPU_PINNED, 0), 'alloc dst2')
C.memmove(src.pixels, raw2, len(raw2))
status(vfx.NvVFX_SetImage(h, b'SrcImage0', C.byref(src)), 'SetImage src2')
status(vfx.NvVFX_SetImage(h, b'DstImage0', C.byref(dst)), 'SetImage dst2')
t0 = time.perf_counter()
status(vfx.NvVFX_Load(h), 'Load2')
t1 = time.perf_counter()
print(f'  第二次 Load（换尺寸）{(t1 - t0) * 1000:.0f} ms')
t0 = time.perf_counter()
status(vfx.NvVFX_Run(h, 0), 'Run2')
t1 = time.perf_counter()
print(f'  第二次 Run {(t1 - t0) * 1000:.1f} ms')

# 同尺寸再跑
t0 = time.perf_counter()
status(vfx.NvVFX_Run(h, 0), 'Run3')
t1 = time.perf_counter()
print(f'  同尺寸第三次 Run {(t1 - t0) * 1000:.1f} ms')

cv.NvCVImage_Dealloc(C.byref(src))
cv.NvCVImage_Dealloc(C.byref(dst))
vfx.NvVFX_DestroyEffect(h)
print('done')
