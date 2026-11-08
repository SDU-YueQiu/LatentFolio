"""正负面提示词识别 v2 —— 参考实现 + 全量实测

v1 漏掉的形态：Efficient Loader / StringConcatenate 这类
  - 文本是节点上的字符串 widget（inputs.positive = "..."），不是 CLIPTextEncode
  - 文本由 StringConcatenate(string_a, delimiter, string_b) 拼出来
修复：种子里接受"字符串 widget"，上游遍历时对拼接/替换节点做求值。

输出：
  data/parsed_prompts.json   每张图 pos/neg/技术元数据/角色/画师
  data/parse_stats.txt       实测统计
"""
import json
import re
import sys
from collections import Counter
from pathlib import Path

LIB = Path(sys.argv[1] if len(sys.argv) > 1 else r'D:\iop18\Pictures\AI绘图库\ComfyUI-output.whalelib')
OUT = Path(__file__).parent / 'data'

NO_FOLLOW = {
    'model', 'clip', 'vae', 'clip_vision', 'image', 'images', 'pixels', 'latent', 'latent_image',
    'samples', 'control_net', 'noise', 'sigmas', 'guider', 'sampler', 'scheduler', 'mask',
    'filename_prefix', 'upload', 'video', 'audio', 'style_model', 'pipe', 'basic_pipe',
    'lora_name', 'ckpt_name', 'vae_name', 'positive_ref', 'negative_ref',
}
TEXT_KEYS = ('text', 'text_g', 'text_l', 'value', 'string', 'prompt', 'wildcard_text',
             'populated_text', 'text_0', 'text_1', 'text_positive', 'text_negative',
             'positive_prompt', 'negative_prompt')
POS_KEYS = {'positive', 'text_positive', 'positive_prompt'}
NEG_KEYS = {'negative', 'text_negative', 'negative_prompt'}

CONCAT_RE = re.compile(r'concat|join|append|stringfunction', re.I)
NEG_MARKERS = [r'negative\s*prompt\s*[:：]', r'【\s*负面\s*】', r'#{2,}\s*negative',
               r'^\s*neg(?:ative)?\s*[:：]']


def is_link(v):
    return isinstance(v, list) and len(v) == 2 and isinstance(v[0], str)


def resolve_value(graph, v, depth=0, seen=None):
    """把 字符串 / 链接 求值成文本"""
    if isinstance(v, str):
        return v
    if not is_link(v) or depth > 8:
        return ''
    seen = seen or set()
    nid = v[0]
    if nid in seen:
        return ''
    seen.add(nid)
    node = graph.get(nid)
    if not node:
        return ''
    return resolve_node_text(graph, node, depth + 1, seen)


def resolve_node_text(graph, node, depth=0, seen=None):
    """节点自身能产出的文本（含拼接语义）"""
    ins = node.get('inputs') or {}
    ct = str(node.get('class_type') or '')
    if CONCAT_RE.search(ct) or ('string_a' in ins or 'string_b' in ins):
        parts = []
        for k in ('string_a', 'string_b', 'text_a', 'text_b', 'a', 'b'):
            if k in ins:
                parts.append(resolve_value(graph, ins[k], depth, (seen or set()).copy()))
        d = ins.get('delimiter') or ins.get('delim') or ''
        d = d if isinstance(d, str) else ''
        parts = [p.strip() for p in parts if p and p.strip()]
        return d.join(parts)
    for k in TEXT_KEYS:
        v = ins.get(k)
        if isinstance(v, str) and v.strip():
            return v
    return ''


def walk_up(graph, nid, role, out, seen=None, depth=0):
    """从条件入口反向走到文本源。

    关键：穿过中间节点时，positive 口子只跟正向那条、negative 只跟负向那条。
    不然从 KSampler 的输出再往上游走时，会把 positive 和 negative 两个文本
    都收进同一个角色里（实测 2,495 张被这样搞错）。
    """
    seen = seen or set()
    if depth > 25 or nid in seen:
        return
    seen = seen | {nid}
    node = graph.get(nid)
    if not node:
        return
    txt = resolve_node_text(graph, node, 0, set())
    if txt:
        out.append((nid, role, txt, str(node.get('class_type'))))
        return
    for k, v in (node.get('inputs') or {}).items():
        if not is_link(v):
            continue
        kl = k.lower()
        if kl in NO_FOLLOW:
            continue
        if kl in POS_KEYS and role != 'positive':
            continue
        if kl in NEG_KEYS and role != 'negative':
            continue
        walk_up(graph, v[0], role, out, seen, depth + 1)


def split_negative(text):
    for pat in NEG_MARKERS:
        m = re.search(pat, text, re.I | re.M)
        if m and m.start() > 0:
            return text[:m.start()], text[m.end():]
    return text, ''


def extract(graph):
    direct_pos, direct_neg = [], []
    seeds_pos, seeds_neg = [], []
    for nid, node in graph.items():
        if not isinstance(node, dict):
            continue
        ct = str(node.get('class_type') or '')
        for k, v in (node.get('inputs') or {}).items():
            kl = k.lower()
            if kl in POS_KEYS:
                if isinstance(v, str) and v.strip():
                    direct_pos.append((nid, 'positive', v, ct))
                elif is_link(v):
                    seeds_pos.append(v[0])
            elif kl in NEG_KEYS:
                if isinstance(v, str) and v.strip():
                    direct_neg.append((nid, 'negative', v, ct))
                elif is_link(v):
                    seeds_neg.append(v[0])
            elif kl in ('conditioning', 'cond') and 'uider' in ct:
                if is_link(v):
                    seeds_pos.append(v[0])

    found = list(direct_pos) + list(direct_neg)
    for nid in seeds_pos:
        walk_up(graph, nid, 'positive', found)
    for nid in seeds_neg:
        walk_up(graph, nid, 'negative', found)

    method = 'conditioning-trace'
    if not any(r == 'positive' for _, r, _, _ in found):
        method = 'fallback'
        cands = []
        for nid, node in graph.items():
            if not isinstance(node, dict):
                continue
            t = resolve_node_text(graph, node, 0, set())
            if t:
                cands.append((nid, str(node.get('class_type')), t))
        if len(cands) == 1:
            found.append((cands[0][0], 'positive', cands[0][2], cands[0][1]))
        else:
            for nid, ct, t in cands:
                found.append((nid, 'negative' if re.search(r'neg', ct, re.I) else 'positive', t, ct))

    pos = '\n'.join(dict.fromkeys(t for _, r, t, _ in found if r == 'positive')).strip()
    neg = '\n'.join(dict.fromkeys(t for _, r, t, _ in found if r == 'negative')).strip()
    split = False
    if not neg:
        p2, n2 = split_negative(pos)
        if n2.strip():
            pos, neg, split = p2.strip(), n2.strip(), True

    return {
        'pos': pos, 'neg': neg, 'method': method, 'split': split,
        'pos_classes': sorted({c for _, r, _, c in found if r == 'positive'}),
        'neg_classes': sorted({c for _, r, _, c in found if r == 'negative'}),
    }


def tech(graph):
    t = {}
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        ct = str(node.get('class_type') or '')
        ins = node.get('inputs') or {}
        if 'model' not in t:
            for k in ('ckpt_name', 'unet_name', 'model_name', 'checkpoint'):
                v = ins.get(k)
                if isinstance(v, str) and v.strip() and v.lower() != 'none':
                    t['model'] = v
                    t['model_node'] = ct
                    break
        for k in ('sampler_name', 'scheduler'):
            if k not in t and isinstance(ins.get(k), str) and ins[k].strip():
                t[k] = ins[k]
        for k in ('steps', 'cfg', 'seed', 'noise_seed', 'width', 'height', 'denoise'):
            v = ins.get(k)
            if k not in t and isinstance(v, (int, float)) and not isinstance(v, bool):
                t[k] = v
    if 'seed' not in t and 'noise_seed' in t:
        t['seed'] = t['noise_seed']
    return t


# ---------------- 文本归一化 / 打标 ----------------

WEIGHT_RE = re.compile(r'\(([^()]*(?:\([^()]*\)[^()]*)*)\s*:\s*[-+]?[\d.]+\s*\)')
BRACKET_RE = re.compile(r'[\[\]]')
LORA_RE = re.compile(r'<lora:[^>]*>', re.I)
ARTIST_PREFIX_RE = re.compile(r'^(?:@+|artist[:_\s]+)+', re.I)


def strip_emphasis(t: str) -> str:
    """去纯强调括号：(sunbeam) / ((x)) / (chiaroscuro  ← 未闭合
    只处理"整个 token 被括号包住"的情况，name_(series) 不受影响。"""
    while True:
        s = t.strip()
        if s.startswith('(') and s.count('(') > s.count(')'):   # 未闭合
            t = s[1:]
            continue
        if s.startswith('(') and s.endswith(')'):
            t = s[1:-1]
            continue
        return s


def norm_tag(t: str):
    """归一成 danbooru 形式；返回 (tag, artist_or_None)"""
    t = LORA_RE.sub('', t)
    t = WEIGHT_RE.sub(r'\1', t)
    t = t.replace('\\', '')
    # danbooru 的 [x] 是降权、{a|b} 是通配，方括号一律剥掉
    t = t.strip().strip('[]').strip()
    t = t.strip('[]').strip()
    t = strip_emphasis(t)
    artist = None
    m = ARTIST_PREFIX_RE.match(t)
    if m and len(t) > m.end():
        artist = t[m.end():].strip().strip('[]').strip()
        artist = strip_emphasis(artist)
        t = artist
    t = strip_emphasis(t)
    # name(series) / name (series) -> name_(series)
    t = re.sub(r'(?<=[a-z0-9)\]}])\s*\(\s*', '_(', t, flags=re.I)
    t = re.sub(r'\s*\)', ')', t)
    # 只剩不成对的括号（多作品别名 a_(b)_(c) 之类保留）
    while t.count('(') > t.count(')'):
        t = t.replace('(', '', 1)
    while t.count(')') > t.count('('):
        t = t[::-1].replace(')', '', 1)[::-1]
    t = re.sub(r'\s*/\s*', '/', t)
    t = re.sub(r'\s+', ' ', t).strip().strip(',').strip('/')
    t = t.replace(' ', '_').lower()
    if artist is not None:
        artist = re.sub(r'\s+', '_', artist).strip('_/').lower()
        artist = re.sub(r'[()]', '', artist)
    return t, artist


def tokens_of(text: str):
    """返回 (tags, artists)"""
    tags, artists = [], []
    if not text:
        return tags, artists
    text = re.sub(r'\bBREAK\b', ',', text)
    for raw in re.split(r'[,\n\r]+', text):
        t, a = norm_tag(raw)
        if t and len(t) <= 90:
            tags.append(t)
        if a and 1 < len(a) <= 90:
            artists.append(a)
    return tags, artists


def load_set(path, extra_col=False):
    s = set()
    with Path(path).open(encoding='utf-8-sig') as f:
        next(f, None)
        for line in f:
            tag = line.split(',', 1)[0].strip()
            if tag:
                s.add(tag)
    return s


def load_chars(path):
    import csv
    tags, byname = set(), {}
    with Path(path).open(encoding='utf-8-sig', newline='') as f:
        for r in csv.DictReader(f):
            tags.add(r['tag'])
            byname.setdefault(r['name'].replace(' ', '_'), []).append(r['tag'])
    return tags, byname


STRIP_VARIANT = re.compile(r'_\([^()]*\)$')


def match_char(tok, tags, byname, known):
    """返回 (canonical_tag, how)。

    只有 exact / variant-strip 算"自动确认"；name-only 一律进候选池，
    因为 smile / star / leaf / hood / bow 这类普通词在 danbooru 角色表里
    都有同名角色（smile_(.flow)、star_(signalis)…），裸名匹配会大面积误判。
    """
    if tok in tags:
        return tok, 'exact'
    t = tok
    for _ in range(3):
        m = STRIP_VARIANT.search(t)
        if not m:
            break
        t2 = t[:m.start()] + t[m.end():]
        if t2 in tags:
            return t2, 'variant-strip'
        t = t2
    base = STRIP_VARIANT.sub('', tok)
    if '(' not in tok and base in byname:
        return None, f'cand:{byname[base][0]}'
    return None, ''


def main():
    char_tags, char_byname = load_chars(OUT / 'characters.csv')
    artist_tags = load_set(OUT / 'artists.csv')
    general_tags = load_set(OUT / 'danbooru_full.csv')  # 全部 tag，用于"已知词"判定
    print(f'词表: 角色 {len(char_tags)} / 画师 {len(artist_tags)} / 全部 {len(general_tags)}')

    files = sorted((LIB / 'imageData').iterdir())
    recs = []
    stats, methods = Counter(), Counter()
    pos_class, neg_class = Counter(), Counter()
    feat = Counter()
    no_pos_types = Counter()
    no_pos_samples = []

    for d in files:
        if not d.is_dir():
            continue
        aj, dj = d / 'data-ai.json', d / 'data.json'
        if not (aj.exists() and dj.exists()):
            stats['缺文件'] += 1
            continue
        try:
            meta = json.loads(dj.read_text(encoding='utf-8'))
            doc = json.loads(aj.read_text(encoding='utf-8'))
        except Exception:
            stats['JSON坏'] += 1
            continue
        if meta.get('isDeleted'):
            stats['已删除'] += 1
            continue
        stats['有效'] += 1
        graph = doc.get('data') or {}
        if not isinstance(graph, dict) or not graph:
            stats['无API图'] += 1
            graph = {}
        r = extract(graph)
        r.update(tech(graph))
        r['id'] = d.name
        r['name'] = meta.get('name')
        methods[r['method']] += 1
        if r['pos']:
            stats['有正向'] += 1
            for c in r['pos_classes']:
                pos_class[c] += 1
        else:
            stats['无正向'] += 1
            for node in graph.values():
                if not isinstance(node, dict):
                    continue
                if any(isinstance(v, str) and v.strip() for v in (node.get('inputs') or {}).values()):
                    no_pos_types[str(node.get('class_type'))] += 1
            if len(no_pos_samples) < 5:
                no_pos_samples.append((meta.get('name'), sorted({
                    str(n.get('class_type')) for n in graph.values()
                    if isinstance(n, dict) and any(isinstance(v, str) and v.strip()
                                                   for v in (n.get('inputs') or {}).values())})))
        if r['neg']:
            stats['有负向'] += 1
            for c in r['neg_classes']:
                neg_class[c] += 1
        else:
            stats['无负向'] += 1
        if r['pos'] and r['neg']:
            stats['正负都有'] += 1
        if r['split']:
            stats['内嵌Negative分隔'] += 1

        toks, art_from_syntax = tokens_of(r['pos'])
        r['pos_tokens'] = toks
        r['neg_tokens'] = tokens_of(r['neg'])[0]
        chars, cands, artists = [], [], list(art_from_syntax)
        for t in toks:
            c, how = match_char(t, char_tags, char_byname, general_tags)
            if c:
                chars.append({'tag': c, 'raw': t, 'how': how})
            elif how.startswith('cand:'):
                cands.append({'raw': t, 'suggest': how[5:]})
            if t in artist_tags:
                artists.append(t)
        artists = list(dict.fromkeys(artists))
        r['chars'] = chars
        r['char_cands'] = cands
        r['artists'] = artists
        if chars:
            stats['命中角色'] += 1
            for c in chars:
                stats['  via_' + c['how']] += 1
        elif cands:
            stats['仅候选角色'] += 1
        if artists:
            stats['命中画师'] += 1
        r['pos_tags_known'] = sum(1 for t in toks if t in general_tags)
        recs.append(r)

        p = r['pos']
        if p:
            if re.search(r'[\u4e00-\u9fff]', p):
                feat['含中文'] += 1
            if WEIGHT_RE.search(p):
                feat['带权重'] += 1
            if 'BREAK' in p:
                feat['含BREAK'] += 1
            if '\\(' in p:
                feat['带转义括号'] += 1
            if re.search(r'\{[^{}]*\|[^{}]*\}', p):
                feat['通配{a|b}'] += 1
            if '<lora:' in p.lower():
                feat['含lora'] += 1
            n = len(toks)
            for lo, hi, name in ((0, 3, 'token<=3'), (4, 10, 'token4-10'), (11, 30, 'token11-30'),
                                 (31, 80, 'token31-80'), (81, 10**9, 'token>80')):
                if lo <= n <= hi:
                    feat[name] += 1
                    break

    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / 'parsed_prompts.json').write_text(json.dumps(recs, ensure_ascii=False), encoding='utf-8')

    lines = []

    def w(s=''):
        lines.append(s)
        print(s)

    w(f'图库 imageData 目录 {len(files)}')
    for k, v in stats.most_common():
        w(f'  {k:<14} {v}')
    w('\n识别方式:')
    for k, v in methods.most_common():
        w(f'  {k:<20} {v}')
    w('\n正向文本源节点类型 Top15:')
    for k, v in pos_class.most_common(15):
        w(f'  {k:<34} {v}')
    w('\n负向文本源节点类型 Top10:')
    for k, v in neg_class.most_common(10):
        w(f'  {k:<34} {v}')
    w('\n提示词形态:')
    for k, v in feat.most_common():
        w(f'  {k:<18} {v}')

    # 角色统计
    cc = Counter()
    for r in recs:
        for c in r['chars']:
            cc[c['tag']] += 1
    w(f'\n角色: 命中图片 {sum(1 for r in recs if r["chars"])}  角色种类 {len(cc)}')
    for t, n in cc.most_common(25):
        w(f'  {n:>4}  {t}')

    ac = Counter()
    for r in recs:
        for a in r['artists']:
            ac[a] += 1
    w(f'\n画师: 命中图片 {sum(1 for r in recs if r["artists"])}  画师种类 {len(ac)}')
    for t, n in ac.most_common(20):
        w(f'  {n:>4}  {t}')

    # 候选池
    candc = Counter()
    for r in recs:
        for c in r['char_cands']:
            candc[c['raw']] += 1
    w(f'\n候选池（裸名命中角色表，但不自动确认）: 图片 {sum(1 for r in recs if r["char_cands"])}  '
      f'条目 {len(candc)}')
    for t, n in candc.most_common(15):
        w(f'  {n:>4}  {t}')

    if no_pos_samples:
        w('\n无正向的 290 张里，节点类型分布（含字符串的节点）:')
        for k, v in no_pos_types.most_common(12):
            w(f'  {k:<36} {v}')
        w('  样本:')
        for s in no_pos_samples[:5]:
            w(f'   {s}')

    # 词表覆盖：正向 token 中本地词表认识的占比
    tot = sum(len(r['pos_tokens']) for r in recs)
    known = sum(r['pos_tags_known'] for r in recs)
    w(f'\n正向 token 共 {tot}，本地词表认识 {known} ({known / tot * 100:.1f}%)')

    (OUT / 'parse_stats.txt').write_text('\n'.join(lines), encoding='utf-8')
    print(f'\n写入 {OUT / "parsed_prompts.json"}')


if __name__ == '__main__':
    main()
