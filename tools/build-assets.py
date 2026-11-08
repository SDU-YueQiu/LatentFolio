"""生成 LatentFolio 需要的紧凑词表 + 旧库评分迁移表。

输入（仓库内 _data/，从旧鲸鱼图库的逆向工作区拷出来的原始资料）:
  _data/characters.csv               tag,name,post_count,copyright,...
  _data/artists.csv                     tag,post_count,...
  _data/zh_names.json                       tag -> 中文名
  _data/whale_library_export.json

输出:
  src/LatentFolio.App/Assets/characters.tsv   tag<TAB>name
  src/LatentFolio.App/Assets/artists.txt      每行一个 tag
  src/LatentFolio.App/Assets/zh_names.tsv     tag<TAB>中文名
  src/LatentFolio.App/Assets/legacy_ratings.tsv  folder/file<TAB>rating<TAB>note
"""
import csv
import json
import sys
from pathlib import Path

REPO = Path(r'D:\Projects\LatentFolio')
DATA = REPO / '_data'
ASSETS = REPO / 'src' / 'LatentFolio.App' / 'Assets'
ASSETS.mkdir(parents=True, exist_ok=True)

# ---- characters
rows = []
with (DATA / 'characters.csv').open(encoding='utf-8-sig', newline='') as f:
    for r in csv.DictReader(f):
        pc = r.get('post_count') or '0'
        try:
            if int(float(pc)) <= 0:
                continue
        except ValueError:
            continue
        tag = (r.get('tag') or '').strip()
        name = (r.get('name') or '').strip()
        if tag:
            rows.append((tag, name))
rows.sort()
with (ASSETS / 'characters.tsv').open('w', encoding='utf-8', newline='\n') as f:
    for tag, name in rows:
        f.write(f'{tag}\t{name}\n')
print(f'characters.tsv  {len(rows)} 行  {(ASSETS / "characters.tsv").stat().st_size / 1e6:.2f} MB')

# ---- artists
arts = []
with (DATA / 'artists.csv').open(encoding='utf-8-sig', newline='') as f:
    for r in csv.DictReader(f):
        pc = r.get('post_count') or '0'
        try:
            if int(float(pc)) <= 0:
                continue
        except ValueError:
            continue
        tag = (r.get('tag') or '').strip()
        if tag:
            arts.append(tag)
arts = sorted(set(arts))
with (ASSETS / 'artists.txt').open('w', encoding='utf-8', newline='\n') as f:
    for t in arts:
        f.write(t + '\n')
print(f'artists.txt     {len(arts)} 行  {(ASSETS / "artists.txt").stat().st_size / 1e6:.2f} MB')

# ---- zh names
zh = json.loads((DATA / 'zh_names.json').read_text(encoding='utf-8'))
items = []
for k, v in zh.items():
    if isinstance(v, dict):
        v = v.get('zh') or v.get('name') or ''
    if not v:
        continue
    items.append((k, str(v).replace('\t', ' ').replace('\n', ' ')))
items.sort()
with (ASSETS / 'zh_names.tsv').open('w', encoding='utf-8', newline='\n') as f:
    for k, v in items:
        f.write(f'{k}\t{v}\n')
print(f'zh_names.tsv    {len(items)} 行')

# ---- legacy ratings
exp = DATA / 'whale_library_export.json'
if exp.exists():
    recs = json.loads(exp.read_text(encoding='utf-8'))
    out = []
    for r in recs:
        folder = (r.get('folder') or '').replace('\\', '/')
        fname = r.get('file_name') or ''
        if not fname:
            continue
        rating = r.get('rating')
        if rating is None or rating == '':
            rating = 0
        rating = int(rating)
        note = (r.get('annotation') or '').replace('\t', ' ').replace('\r', ' ').replace('\n', ' ')
        deleted = 1 if r.get('deleted') else 0
        # 还原脚本把已删除的图挪到了 _whale_deleted/ 下面，路径要跟着加前缀，
        # 否则这些行的评分和删除状态对不上任何一张图（131 张）。
        rel_folder = f'_whale_deleted/{folder}' if deleted and folder else folder
        rel = f'{rel_folder}/{fname}' if rel_folder else fname
        if rating == 0 and not note and not deleted:
            continue
        out.append((rel, int(rating), note, deleted))
    out.sort()
    with (ASSETS / 'legacy_ratings.tsv').open('w', encoding='utf-8', newline='\n') as f:
        for rel, rating, note, deleted in out:
            f.write(f'{rel}\t{rating}\t{note}\t{deleted}\n')
    print(f'legacy_ratings.tsv  {len(out)} 行（{sum(1 for x in out if x[1] > 0)} 条评分）')
else:
    print('!! 没有 whale_library_export.json，跳过评分迁移')



