import os
import sqlite3

c = sqlite3.connect(os.path.join(os.environ['LOCALAPPDATA'], 'LatentFolio', 'library.db'))
q = lambda s: c.execute(s).fetchone()[0]
print('steps not null ', q('select count(*) from image where steps is not null'))
print('seed not null  ', q('select count(*) from image where seed is not null'))
print('cfg not null   ', q('select count(*) from image where cfg is not null'))
print('denoise n/n    ', q('select count(*) from image where denoise is not null'))
print('with prompt    ', q("select count(*) from image where prompt<>''"))
print('with neg       ', q("select count(*) from image where negative<>''"))
print('neg dup        ', q('select count(*) from image where neg_is_dup=1'))
print('chars          ', q("select count(*) from image where chars<>''"))
print('artists        ', q("select count(*) from image where artists<>''"))
print('prompt sources ', c.execute('select prompt_source,count(*) from image group by prompt_source').fetchall())
print('samplers       ', c.execute('select sampler,count(*) from image group by sampler order by 2 desc limit 6').fetchall())
print('sample         ', c.execute('select file_name,steps,cfg,seed,denoise,width,height,substr(model,1,30) from image where steps is not null limit 3').fetchall())
