"""人物词条体检 / 清理。

用法：
    python tools/audit-people.py            # 只打印报告
    python tools/audit-people.py --apply    # 落库：删错误别名、写合并别名、忽略无义词

背景：角色词表来自 danbooru 的完整角色表，裸名（solo / smile / sky / flower…）
会撞上同名角色，候选池于是被这类通用提示词灌满。这个脚本做三件事：

1. 删掉历史上被误确认的别名（默认 solo → solo_(breath_of_fire)）；
2. 写入「同一个人不同写法」的合并别名；
3. 把通用词写进 cand.state=2（已忽略），并同步到 Assets/general_tags.txt，
   让 Vocabulary 以后连候选都不再提。
"""

import os
import sqlite3
import sys

APP = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(APP, 'src', 'LatentFolio.App', 'Assets')
DB = os.path.expandvars(r'%LOCALAPPDATA%\LatentFolio\library.db')

# 误确认的别名：key -> 错的 tag
BAD_ALIASES = {
    'solo': 'solo_(breath_of_fire)',
}

# 同一个人不同写法：key -> 正规 tag
MERGES = {
    'arcane_jinx': 'jinx_(league_of_legends)',
    'magical_mirai_miku': 'hatsune_miku',
    'souryuu_asuka_langley_(plugsuit)': 'souryuu_asuka_langley',
}

# 裸名里确实像人名的，保留候选（其余一律视为通用词）
KEEP = {'jinx', 'tinker_bell'}

# 通用词补充表（提示词里高频出现、但绝不会是角色名的词）
EXTRA = """
1girl 1boy 2girls 3girls multiple_girls solo_focus looking_at_viewer looking_back
upper_body lower_body full_body cowboy_shot portrait close-up wide_shot from_side from_behind
long_hair short_hair medium_hair blonde_hair brown_hair black_hair white_hair red_hair blue_hair
green_hair purple_hair pink_hair silver_hair grey_hair gray_hair orange_hair multicolored_hair
hair_ornament hair_between_eyes hair_ribbon twintails side_ponytail braid braids bangs
open_mouth closed_mouth closed_eyes half-closed_eyes one_eye_closed tears sweatdrop
freckles makeup eyelashes eyebrows earrings necklace bracelet belt gloves scarf hat cap
helmet hoodie sweater cardigan blouse shirt skirt dress gown kimono yukata uniform
thighhighs stockings pantyhose kneehighs boots shoes sandals heels barefoot
cat_ears fox_ears wolf_ears rabbit_ears animal_ears horns wings tail feather feathers
sword katana dagger spear lance bow arrow shield staff wand book scroll
masterpiece best_quality highres absurdres ultra_detailed highly_detailed intricate_detail
photorealistic realistic anime_style watercolor illustration digital_painting lineart
monochrome greyscale sketch drawing painting
depth_of_field blurry_background bokeh lens_flare cinematic_lighting dramatic_lighting
backlighting rim_light soft_lighting studio_lighting volumetric_lighting god_rays
scenery indoors outdoors city cityscape building house room bed chair table window
tree trees grass field flower flowers petal petals leaf leaves garden forest mountain
river lake sea ocean beach wave waves sky clouds sunset sunrise night day dusk dawn
rain snow fog mist smoke flame flames spark sparks ember light shadow reflection glow
sunlight moonlight starlight star stars moon sun planet galaxy nebula
smile grin laugh crying blush angry sad happy surprised
dress_shirt long_sleeves short_sleeves bare_shoulders cleavage navel collarbone
red blue green pink purple black white gray grey orange yellow gold silver bronze brown
cyan teal indigo violet magenta beige ivory cream
""".split()

def main() -> int:
    apply = '--apply' in sys.argv
    if not os.path.exists(DB):
        print('找不到数据库', DB)
        return 1
    c = sqlite3.connect(DB)

    # ---- 现有候选 ----
    cands = list(c.execute('select key, suggest, hits, state from cand'))
    junk = [k for k, s, h, st in cands if k not in KEEP and k not in MERGES]
    print(f'候选 {len(cands)} 条，其中通用词 {len(junk)} 条')
    print('--- 保留为候选 ---')
    for k, s, h, st in sorted(cands, key=lambda r: -r[2]):
        if k in KEEP:
            print(f'  {h:5d}  {k}  ->  {s}')

    # ---- 别名现状 ----
    print('--- 现有别名 ---')
    for row in c.execute('select key, tag, source from alias'):
        flag = ''
        if row[0] in BAD_ALIASES:
            flag = '  <== 误确认，将删除'
        print(f'  {row[0]}  ->  {row[1]}  [{row[2]}]{flag}')

    # ---- 人物分布 ----
    from collections import Counter
    cnt = Counter()
    for (s,) in c.execute("select chars from image where length(chars)>0"):
        for t in s.split('\u0001'):
            if t.strip():
                cnt[t.strip()] += 1
    print(f'--- 人物 {len(cnt)} 个 ---')
    for t, n in cnt.most_common(40):
        print(f'  {n:5d}  {t}')

    # ---- 通用词表 ----
    words = sorted({w for w in (junk + EXTRA) if w})
    out = os.path.join(ASSETS, 'general_tags.txt')
    print(f'通用词表 {len(words)} 个 -> {out}')

    if not apply:
        print('\n（只读模式，加 --apply 落库）')
        return 0

    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(words) + '\n')

    c.execute('begin')
    for k, v in BAD_ALIASES.items():
        c.execute('delete from alias where key=? and tag=?', (k, v))
    for k, v in MERGES.items():
        c.execute('insert into alias(key, tag, source) values(?,?,?) '
                  'on conflict(key) do update set tag=excluded.tag', (k, v, 'manual'))
    for k in junk:
        c.execute('insert into cand(key, suggest, hits, state, at) values(?,?,1,2,0) '
                  'on conflict(key) do update set state=2', (k, ''))
    c.commit()
    print('已落库：别名清理 / 合并 / 忽略')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
