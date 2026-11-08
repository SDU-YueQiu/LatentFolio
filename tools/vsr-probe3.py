"""只 Load 一次，换尺寸直接 Run —— 输出到底对不对？落盘肉眼验。"""
import ctypes as C
import os
from pathlib import Path

LIBS = Path(r'D:\Recreation\ComfyUI-aki-v3\python\Lib\site-packages\nvvfx\libs')
OUT = Path(__file__).parent.parent / 'vsr_out'
OUT.mkdir(exist_ok=True)
os.add_dll_directory(str(LIBS))
os.environ['PATH'] = str(LIBS) + ';' + os.environ.get('PATH', '')

for _n in ['cudart64_12.dll', 'nppc64_12.dll', 'nppial64_12.dll', 'nppicc64_12.dll',
           'nppidei64_12.dll', 'nppif64_12.dll', 'nppig64_12.dll', 'nppim64_12.dll',
           'nppist64_12.dll', 'nppitc64_12.dll', 'NVCVImage.dll', 'nvngxruntime.dll',
           'NVVideoEffects.dll', 'nvVFXVideoSuperRes.dll', 'nvngx_vsr.dll']:
    _p = LIBS / _n
    if _p.exists():
        C.WinDLL(str(_p))


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


vfx = C.WinDLL(str(LIBS / 'NVVideoEffects.dll'))
cv = C.WinDLL(str(LIBS / 'NVCVImage.dll'))
for fn, args in [('NvVFX_CreateEffect', [C.c_char_p, C.POINTER(C.c_void_p)]),
                 ('NvVFX_SetU32', [C.c_void_p, C.c_char_p, C.c_uint]),
                 ('NvVFX_SetF32', [C.c_void_p, C.c_char_p, C.c_float]),
                 ('NvVFX_SetImage', [C.c_void_p, C.c_char_p, C.POINTER(NvCVImage)]),
                 ('NvVFX_Load', [C.c_void_p]), ('NvVFX_Run', [C.c_void_p, C.c_int]),
                 ('NvVFX_DestroyEffect', [C.c_void_p])]:
    f = getattr(vfx, fn); f.argtypes = args; f.restype = C.c_int
cv.NvCVImage_Alloc.argtypes = [C.POINTER(NvCVImage), C.c_uint, C.c_uint, C.c_int, C.c_int, C.c_uint, C.c_uint, C.c_uint]
cv.NvCVImage_Alloc.restype = C.c_int
cv.NvCVImage_Dealloc.argtypes = [C.POINTER(NvCVImage)]
cv.NvCVImage_Dealloc.restype = C.c_int

from PIL import Image

h = C.c_void_p()
assert vfx.NvVFX_CreateEffect(b'VideoSuperRes', C.byref(h)) == 0
vfx.NvVFX_SetU32(h, b'QualityLevel', 19)
vfx.NvVFX_SetU32(h, b'ImageEncodingMode', 0)
vfx.NvVFX_SetF32(h, b'Strength', 1.0)

src = NvCVImage()
dst = NvCVImage()
SIZES = []


def setup(w, hh):
    global src, dst
    if SIZES:
        cv.NvCVImage_Dealloc(C.byref(src)); cv.NvCVImage_Dealloc(C.byref(dst))
    src = NvCVImage(); dst = NvCVImage()
    assert cv.NvCVImage_Alloc(C.byref(src), w, hh, 6, 1, 0, 2, 0) == 0
    assert cv.NvCVImage_Alloc(C.byref(dst), w * 2, hh * 2, 6, 1, 0, 2, 0) == 0
    SIZES.append((w, hh))


def run(path, name, load):
    img = Image.open(path).convert('RGBA')
    w, hh = img.size
    setup(w, hh)
    raw = img.tobytes()
    C.memmove(src.pixels, raw, len(raw))
    vfx.NvVFX_SetImage(h, b'SrcImage0', C.byref(src))
    vfx.NvVFX_SetImage(h, b'DstImage0', C.byref(dst))
    if load:
        print('  Load ->', vfx.NvVFX_Load(h))
    r = vfx.NvVFX_Run(h, 0)
    buf = C.string_at(dst.pixels, dst.pitch * hh * 2)
    out = Image.frombytes('RGBA', (w * 2, hh * 2), buf[:w * 2 * hh * 2 * 4])
    p = OUT / f'{name}.png'
    out.save(p)
    print(f'  {name}: {w}x{hh} -> {w*2}x{hh*2} run={r} -> {p.name}')


base = Path(r'D:\Recreation\ComfyUI-aki-v3\ComfyUI\output')
a = next(base.rglob('*.png'))
print('A =', a.name)
run(a, 'noload-A', load=True)
b = sorted(base.rglob('*.png'), key=lambda p: p.stat().st_size)[len(list(base.rglob('*.png'))) // 2]
print('B =', b.name)
run(b, 'noload-B', load=False)
c = sorted(base.rglob('*.png'), key=lambda p: p.stat().st_size)[-1]
print('C =', c.name)
run(c, 'noload-C', load=False)

cv.NvCVImage_Dealloc(C.byref(src)); cv.NvCVImage_Dealloc(C.byref(dst))
vfx.NvVFX_DestroyEffect(h)
print('done')
