import os
import sqlite3

p = os.path.join(os.environ['LOCALAPPDATA'], 'LatentFolio', 'library.db')
c = sqlite3.connect(p)
q = lambda s: c.execute(s).fetchone()[0]
print('total        ', q('select count(*) from image'))
print('rating hist  ', c.execute('select rating,count(*) from image group by rating order by rating').fetchall())
print('state hist   ', c.execute('select state,count(*) from image group by state').fetchall())
print('with prompt  ', q("select count(*) from image where prompt<>''"))
print('with neg     ', q("select count(*) from image where negative<>''"))
print('neg dup      ', q('select count(*) from image where neg_is_dup=1'))
print('chars        ', q("select count(*) from image where chars<>''"))
print('artists      ', q("select count(*) from image where artists<>''"))
print('notes        ', q("select count(*) from image where note<>''"))
print('zero size    ', q('select count(*) from image where width=0 or height=0'))
print('cols         ', c.execute('select id,name,count from col').fetchall())
print()
for r in c.execute('select file_name,folder,rating,note,substr(prompt,1,40),model,sampler,steps,cfg,seed,width,height from image order by id limit 4'):
    print(r)
