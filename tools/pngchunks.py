import struct
import sys
from pathlib import Path

p = Path(sys.argv[1])
data = p.read_bytes()
assert data[:8] == b'\x89PNG\r\n\x1a\n'
off = 8
print('chunk table:')
while off < len(data):
    (ln,) = struct.unpack('>I', data[off:off + 4])
    typ = data[off + 4:off + 8].decode('ascii', 'replace')
    body = data[off + 8:off + 8 + ln]
    print(f'  {typ:6s} len={ln:<9d} off={off}')
    if typ in ('tEXt', 'iTXt', 'zTXt'):
        z = body.find(b'\x00')
        key = body[:z].decode('latin-1')
        print(f'         key={key!r} valueLen={ln - z - 1}')
        if key == 'prompt':
            print('         tail:', body[-70:])
    if typ == 'IEND':
        break
    off += 12 + ln
print('total bytes', len(data), 'left over', len(data) - off)

from PIL import Image
im = Image.open(p)
pr = im.info.get('prompt', '')
print('PIL prompt len', len(pr))
print('PIL prompt tail', repr(pr[-70:]))
