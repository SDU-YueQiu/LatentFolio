import sqlite3, os, collections

p = os.path.expandvars(r'%LOCALAPPDATA%\LatentFolio\library.db')
c = sqlite3.connect(p)

cnt = collections.Counter()
for (s,) in c.execute("select chars from image where length(chars)>0"):
    for t in s.split('\u0001'):
        t = t.strip()
        if t:
            cnt[t] += 1

print('distinct', len(cnt))
for t, n in cnt.most_common():
    print(f'{n:5d}  {t}')

print()
print('=== candidates ===')
for k, s, h, st in c.execute('select key, suggest, hits, state from cand order by hits desc'):
    print(f'{h:5d}  {k}  ->  {s}  [{st}]')
