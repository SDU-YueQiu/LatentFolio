import json
import os
import sqlite3

from PIL import Image

c = sqlite3.connect(os.path.join(os.environ['LOCALAPPDATA'], 'LatentFolio', 'library.db'))
rows = c.execute("select path,file_name from image where prompt_source='none' limit 6").fetchall()
for p, n in rows[:4]:
    im = Image.open(p)
    keys = list(im.info.keys())
    print(n, 'keys=', keys)
    pr = im.info.get('prompt')
    if pr:
        print('   head:', pr[:160].replace('\n', ' '))
        try:
            d = json.loads(pr)
            print('   nodes:', len(d), 'types:', sorted({v.get('class_type') for v in d.values() if isinstance(v, dict)})[:8])
        except Exception as e:
            print('   parse fail', e)
    wf = im.info.get('workflow')
    if wf:
        try:
            d = json.loads(wf)
            print('   workflow nodes:', len(d.get('nodes', [])),
                  'types:', sorted({v.get('type') for v in d.get('nodes', [])})[:8])
        except Exception as e:
            print('   workflow parse fail', e)
    print()

# 统计：prompt 段存在但解析不出东西的，看它们的 class_type 集合
import collections
cnt = collections.Counter()
for (p,) in c.execute("select path from image where prompt_source in ('none','') limit 400"):
    try:
        im = Image.open(p)
    except Exception:
        continue
    if 'prompt' not in im.info:
        cnt['<no prompt chunk>'] += 1
        continue
    try:
        d = json.loads(im.info['prompt'])
    except Exception:
        cnt['<bad json>'] += 1
        continue
    types = {v.get('class_type') for v in d.values() if isinstance(v, dict)}
    if not types:
        cnt['<no class_type>'] += 1
    else:
        cnt['has types'] += 1
print(cnt.most_common())
