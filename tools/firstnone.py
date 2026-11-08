import os
import sqlite3

c = sqlite3.connect(os.path.join(os.environ['LOCALAPPDATA'], 'LatentFolio', 'library.db'))
r = c.execute("select path from image where prompt_source='none' limit 1").fetchone()
print(r[0] if r else '')
