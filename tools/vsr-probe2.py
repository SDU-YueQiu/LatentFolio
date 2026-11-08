"""VSR 二次探测：换尺寸到底要不要重新 Load？同尺寸再 Load 贵不贵？"""
import ctypes as C
import os
import time
from pathlib import Path

LIBS = Path(r'D:\Recreation\ComfyUI-aki-v3\python\Lib\site-packages\nvvfx\libs')
os.add_dll_directory(str(LIBS))
os.environ['PATH'] = str(LIBS) + ';' + os.environ.get('PATH', '')

_PRELOAD = ['cudart64_12.dll', 'nppc64_12.dll', 'nppial64_12.dll', 'nppicc64_12.dll',
            'nppidei64_12.dll', 'nppif64_12.dll', 'nppig64_12.dll', 'nppim64_12.dll',
            'nppist64_12.dll', 'nppitc64_12.dll', 'NVCVImage.dll', 'nvngxruntime.dll',
            'NVVideoEffects.dll', 'nvVFXVideoSuperRes.dll', 'nvngx_vsr.dll']
for _n in _PRELOAD:
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
for fn, args in [
    ('NvVFX_CreateEffect', [C.c_char_p, C.POINTER(C.c_void_p)]),
    ('NvVFX_SetU32', [C.c_void_p, C.c_char_p, C.c_uint]),
    ('NvVFX_SetF32', [C.c_void_p, C.c_char_p, C.c_float]),
    ('NvVFX_SetImage', [C.c_void_p, C.c_char_p, C.POINTER(NvCVImage)]),
    ('NvVFX_Load', [C.c_void_p]),
    ('NvVFX_Run', [C.c_void_p, C.c_int]),
    ('NvVFX_DestroyEffect', [C.c_void_p]),
]:
    f = getattr(vfx, fn)
    f.argtypes = args
    f.restype = C.c_int
cv.NvCVImage_Alloc.argtypes = [C.POINTER(NvCVImage), C.c_uint, C.c_uint, C.c_int, C.c_int, C.c_uint, C.c_uint, C.c_uint]
cv.NvCVImage_Alloc.restype = C.c_int
cv.NvCVImage_Dealloc.argtypes = [C.POINTER(NvCVImage)]
cv.NvCVImage_Dealloc.restype = C.c_int

RGBA, U8, CHUNKY, PINNED = 6, 1, 0, 2


def new_img(w, h):
    im = NvCVImage()
    r = cv.NvCVImage_Alloc(C.byref(im), w, h, RGBA, U8, CHUNKY, PINNED, 0)
    assert r == 0, r
    C.memset(im.pixels, 128, im.pitch * h)
    return im


h = C.c_void_p()
assert vfx.NvVFX_CreateEffect(b'VideoSuperRes', C.byref(h)) == 0
vfx.NvVFX_SetU32(h, b'QualityLevel', 19)
vfx.NvVFX_SetU32(h, b'ImageEncodingMode', 0)
vfx.NvVFX_SetF32(h, b'Strength', 1.0)

src, dst = new_img(1024, 1024), new_img(2048, 2048)
vfx.NvVFX_SetImage(h, b'SrcImage0', C.byref(src))
vfx.NvVFX_SetImage(h, b'DstImage0', C.byref(dst))
t = time.perf_counter(); print('Load#1', vfx.NvVFX_Load(h), f'{(time.perf_counter()-t)*1000:.0f} ms')
t = time.perf_counter(); print('Run#1 ', vfx.NvVFX_Run(h, 0), f'{(time.perf_counter()-t)*1000:.1f} ms')

print('\n--- 同尺寸再 Load ---')
t = time.perf_counter(); print('Load again', vfx.NvVFX_Load(h), f'{(time.perf_counter()-t)*1000:.0f} ms')

print('\n--- 换尺寸：不 Load 直接 Run ---')
cv.NvCVImage_Dealloc(C.byref(src)); cv.NvCVImage_Dealloc(C.byref(dst))
src, dst = new_img(1216, 832), new_img(2432, 1664)
print('SetImage', vfx.NvVFX_SetImage(h, b'SrcImage0', C.byref(src)), vfx.NvVFX_SetImage(h, b'DstImage0', C.byref(dst)))
t = time.perf_counter(); r = vfx.NvVFX_Run(h, 0); print('Run (no reload)', r, f'{(time.perf_counter()-t)*1000:.1f} ms')

print('\n--- 换尺寸后 Load 再 Run ---')
t = time.perf_counter(); print('Load(1216x832)', vfx.NvVFX_Load(h), f'{(time.perf_counter()-t)*1000:.0f} ms')
t = time.perf_counter(); print('Run', vfx.NvVFX_Run(h, 0), f'{(time.perf_counter()-t)*1000:.1f} ms')

print('\n--- 第二个 effect 实例（同尺寸）建+load ---')
h2 = C.c_void_p()
t = time.perf_counter()
vfx.NvVFX_CreateEffect(b'VideoSuperRes', C.byref(h2))
vfx.NvVFX_SetU32(h2, b'QualityLevel', 19)
vfx.NvVFX_SetU32(h2, b'ImageEncodingMode', 0)
s2, d2 = new_img(1024, 1024), new_img(2048, 2048)
vfx.NvVFX_SetImage(h2, b'SrcImage0', C.byref(s2))
vfx.NvVFX_SetImage(h2, b'DstImage0', C.byref(d2))
print('create+load#2', vfx.NvVFX_Load(h2), f'{(time.perf_counter()-t)*1000:.0f} ms')
t = time.perf_counter(); print('Run h2', vfx.NvVFX_Run(h2, 0), f'{(time.perf_counter()-t)*1000:.1f} ms')

cv.NvCVImage_Dealloc(C.byref(src)); cv.NvCVImage_Dealloc(C.byref(dst))
cv.NvCVImage_Dealloc(C.byref(s2)); cv.NvCVImage_Dealloc(C.byref(d2))
vfx.NvVFX_DestroyEffect(h); vfx.NvVFX_DestroyEffect(h2)
print('done')
