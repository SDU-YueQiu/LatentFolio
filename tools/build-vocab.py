"""重建词表资产（角色 / 画师 / 通用词 / 版权作品）。

输入（都在 _data/，不进版本库）：

  characters.csv    tag,name,series,post_count,deprecated,aliases,other_names,name_cjk,...
                    —— 旧图库逆向时导出的 Danbooru 全量角色表
  artists.csv       同上，画师
  danbooru_tags.csv  tag,category,count,"aliases"
                    —— 从 DominikDoom/a1111-sd-webui-tagcomplete 的 tags/danbooru.csv 拿的快照
                       0=general 1=artist 3=copyright 4=character 5=meta
                       这份是全量里的「有热度」子集，拿来补新角色 + 通用词 + 版权作品

输出（src/LatentFolio.App/Assets/）：

  characters.tsv     tag<TAB>name          角色（name 空 = 用 tag 本体）
  artists.txt        每行一个画师 tag
  general_tags.txt   每行一个通用 tag（smile / solo / white_dress …）
  copyright_tags.txt 每行一个作品 tag（honkai:_star_rail / fate …）

为什么要通用词表：角色表里有一堆「裸名也像普通词」的条目（emerald / coral / plum / fedora /
jungle / train）。没有一份像样的通用词表，命不中的裸名就全被当成「可能是角色」塞进候选池。
以前只有 530 条手写词，实测库里 40 条待处理候选有 27 条是这种噪声。

版权作品表用来认「角色_(形态)_(作品)」这种三段式：知道第二段是作品，才知道哪一段该留下来配角色名。
"""
import csv
import sys
from pathlib import Path

REPO = Path(r"D:\Projects\LatentFolio")
DATA = REPO / "_data"
ASSETS = REPO / "src" / "LatentFolio.App" / "Assets"

GENERAL_MIN_COUNT = 100   # 通用词表只收有热度的，太冷门的反而可能真是角色名


def read_csv(path):
    with path.open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def write_lines(path, lines):
    with path.open("w", encoding="utf-8", newline="\n") as f:
        for line in lines:
            f.write(line + "\n")
    print(f"{path.name:20s} {len(lines):>7,} 行  {path.stat().st_size / 1e6:.2f} MB")


def main():
    if not (DATA / "characters.csv").exists():
        print("!! _data/characters.csv 不在，无法重建词表", file=sys.stderr)
        return 1

    # ---------- 读入现有词表（保住线上已有的条目） ----------
    chars_path = ASSETS / "characters.tsv"
    chars = {}
    for line in chars_path.read_text(encoding="utf-8").splitlines():
        if not line:
            continue
        tag, _, name = line.partition("\t")
        chars[tag.strip()] = name.strip()

    artists = {x.strip() for x in (ASSETS / "artists.txt").read_text(encoding="utf-8").splitlines() if x.strip()}
    general = {x.strip() for x in (ASSETS / "general_tags.txt").read_text(encoding="utf-8").splitlines() if x.strip()}
    before = (len(chars), len(artists), len(general))

    # ---------- 新快照：分类补全 ----------
    dump_chars = set()
    dump_artists = set()
    dump_general = []
    dump_copy = set()
    if (DATA / "danbooru_tags.csv").exists():
        with (DATA / "danbooru_tags.csv").open(encoding="utf-8", newline="") as f:
            for row in csv.reader(f):
                if len(row) < 3:
                    continue
                tag, cat, count = row[0], row[1], row[2]
                try:
                    n = int(count)
                except ValueError:
                    continue
                if cat == "4":
                    dump_chars.add(tag)
                elif cat == "1":
                    dump_artists.add(tag)
                elif cat == "3":
                    dump_copy.add(tag)
                elif cat == "0" and n >= GENERAL_MIN_COUNT:
                    dump_general.append(tag)
    else:
        print("!! 没有 danbooru_tags.csv，只重建角色/画师/作品（通用词表保持原样）", file=sys.stderr)

    # ---------- 角色 / 画师：旧表 + 新快照 ----------
    new_chars = sorted(dump_chars - chars.keys())
    for t in new_chars:
        chars[t] = ""
    new_artists = sorted(dump_artists - artists)
    artists |= set(new_artists)

    # ---------- 通用词 ----------
    general |= set(dump_general)

    # ---------- 版权作品：新快照 + 角色表里的 series 列 ----------
    copy = set(dump_copy)
    for row in read_csv(DATA / "characters.csv"):
        if (row.get("post_count") or "0") == "0":
            continue
        series = (row.get("series") or "").strip().lower()
        if series:
            copy.add(series.replace(" ", "_"))
    # 写成 Danbooru tag 形式
    copy = {c.strip().replace(" ", "_").lower() for c in copy if c.strip()}

    # ---------- 落盘 ----------
    order = sorted(chars.items())
    with chars_path.open("w", encoding="utf-8", newline="\n") as f:
        for tag, name in order:
            f.write(f"{tag}\t{name}\n")
    print(f"{chars_path.name:20s} {len(order):>7,} 行  {chars_path.stat().st_size / 1e6:.2f} MB"
          f"  （新增 {len(new_chars):,}）")

    write_lines(ASSETS / "artists.txt", sorted(artists))
    write_lines(ASSETS / "general_tags.txt", sorted(general))
    write_lines(ASSETS / "copyright_tags.txt", sorted(copy))

    print()
    print(f"角色 {before[0]:,} -> {len(chars):,}（+{len(new_chars):,}）")
    print(f"画师 {before[1]:,} -> {len(artists):,}（+{len(new_artists):,}）")
    print(f"通用 {before[2]:,} -> {len(general):,}")
    print(f"作品 {len(copy):,}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
