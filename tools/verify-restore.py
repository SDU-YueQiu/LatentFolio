"""删 15 GB 旧库之前先核对：.whalelib 里每张图都在 output 里有一份。

按文件名比（还原时就是按 data.json 的 name + 原扩展名落盘的，
同名冲突的少数几个会被改名成 "name (2).png"）。
"""
import json
import os
import re
from pathlib import Path

LIB = Path(r'D:\iop18\Pictures\AI绘图库\ComfyUI-output.whalelib')
OUT = Path(r'D:\Recreation\ComfyUI-aki-v3\ComfyUI\output')
EXPORT = Path(r'D:\Recreation\ComfyUI-aki-v3\ComfyUI\whale_library_export.json')

# output 里现有的所有文件名
index = {}
dup = 0
for root, _dirs, files in os.walk(OUT):
    for f in files:
        key = f.lower()
        if key in index:
            dup += 1
        index[key] = os.path.join(root, f)
print(f'output 文件 {len(index):,} 个（重名 {dup}）')

stem_index = {}
for key in index:
    m = re.match(r'^(.*?) \((\d+)\)(\.[^.]+)$', key)
    stem = m.group(1) + m.group(3) if m else key
    stem_index.setdefault(stem, key)

records = json.loads(EXPORT.read_text(encoding='utf-8')) if EXPORT.exists() else []
print(f'导出记录 {len(records):,} 条')

missing = []
diff_size = []
for r in records:
    fname = r.get('file_name') or ''
    if not fname:
        missing.append(('<no name>', ''))
        continue
    key = fname.lower()
    hit = index.get(key) or stem_index.get(key)
    if hit is None:
        missing.append((r.get('file_name'), r.get('folder')))
    else:
        # 大小对不上的记一笔（还原时用原文件大小做校验）
        want = r.get('size_byte')
        if want:
            try:
                if os.path.getsize(hit) != want:
                    diff_size.append((fname, os.path.getsize(hit), want))
            except OSError:
                pass

print(f'output 里找不到的: {len(missing)}')
for m in missing[:10]:
    print('   ', m)
print(f'大小不一致: {len(diff_size)}')
for m in diff_size[:5]:
    print('   ', m)

# imageData 目录数
dirs = [d for d in (LIB / 'imageData').iterdir() if d.is_dir()]
print(f'imageData 目录 {len(dirs):,}')
print('结论:', '可以删' if not missing else '有缺失，先别删')
