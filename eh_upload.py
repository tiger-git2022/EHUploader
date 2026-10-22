#!/usr/bin/env python3
"""把回填后的 cbz 上传为 e-hentai 翻译画廊。

用法：
  python eh_upload.py login                      # 首次使用：在弹出的浏览器里手动登录
  python eh_upload.py scan   <批次或作品目录...>   # 只检查，不上传
  python eh_upload.py upload <批次或作品目录...> [--limit N] [--force]
  python eh_upload.py tag    <原画廊 gid...>      # 重新补标签
"""
from __future__ import annotations

import argparse
import html
import json
import random
import re
import shutil
import sys
import time
import tomllib
import urllib.request
import zipfile
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "config.toml"
STATE_PATH = ROOT / "upload_state.json"
PROFILE_DIR = ROOT / "browser_profile"
TMP_DIR = ROOT / "_tmp"
LOG_DIR = ROOT / "logs"

UPLD = "https://upld.e-hentai.org"
API = "https://api.e-hentai.org/api.php"
LOGIN_URL = "https://forums.e-hentai.org/index.php?act=Login&CODE=00"
CATEGORY = {
    "Doujinshi": "2", "Manga": "3", "Artist CG": "4", "Game CG": "5", "Western": "10",
    "Non-H": "9", "Image Set": "6", "Cosplay": "7", "Misc": "1",
}
LANG_CHINESE = "3437"
IMAGE_LIMIT_MB = {".jpg": 20, ".jpeg": 20, ".webp": 20, ".png": 50, ".gif": 10}
# 语言标记要插在这些后缀之前，例如 [Chinese] [Decensored] [Digital]
EN_QUALIFIERS = {"digital", "decensored", "uncensored", "colorized", "ongoing", "incomplete", "textless"}
JP_QUALIFIERS = {"dl版", "無修正", "カラー化", "デジタル版", "進行中", "修正なし"}
GALLERY_RE = re.compile(r"hentai\.org/g/(\d+)/([0-9a-f]{10})")


def _path_setting(key: str) -> Path | None:
    """config.toml 里的路径设置（模块加载时读一次）；没写或读不到时返回 None。"""
    try:
        with CONFIG_PATH.open("rb") as f:
            v = tomllib.load(f).get(key)
    except (OSError, ValueError):
        v = None
    return Path(v) if v else None


# 漫画根目录（config.toml 的 source_dir）；EXE 里可以改，扫描时作为参数传进来
DEFAULT_SOURCE = _path_setting("source_dir")
SKIP_DIRS = {"已上传", "老文件", "att-mz", "tests", "pipeline_backups"}

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
LOG_DIR.mkdir(exist_ok=True)
_log_file = LOG_DIR / f"upload_{datetime.now():%Y%m%d}.log"


import threading
_print_lock = threading.Lock()   # 并行读评论时多个线程会同时写日志，整行一起输出，免得两行搅在一起


def log(msg: str) -> None:
    line = f"[{datetime.now():%H:%M:%S}] {msg}"
    with _print_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
        with _log_file.open("a", encoding="utf-8") as f:
            f.write(line + "\n")


def emit(ev: str, **data) -> None:
    """给 EXE 界面的结构化事件，一行一个，以 @@ 开头。"""
    line = "@@" + json.dumps({"ev": ev, **data}, ensure_ascii=False, default=str)
    with _print_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def load_config() -> dict:
    example = ROOT / "config.example.toml"
    if not CONFIG_PATH.exists() and example.exists():
        shutil.copyfile(example, CONFIG_PATH)   # 第一次运行：用示例配置生成 config.toml
    with CONFIG_PATH.open("rb") as f:
        return tomllib.load(f)


CONFIG_DOCS = {
    "comment": "上传者简介（ulcomment），留空表示不填写。{raw} 会换成未翻译原画廊的链接；作品文件夹里有 upload_note.txt 时，其内容放在简介最前面",
    "share_comment": "发布后在画廊下单独发表的评论（经验分享），留空表示不发",
    "share_comment_mode": "评论发送方式：first = 每次上传只在第一部下面发，all = 每部都发，off = 不发",
    "notice_keywords": "原画廊评论里出现这些词就先拦下不上传（例如禁止机翻、已预定翻译），确认后可放行",
    "review_keywords": "原画廊评论里出现这些词也先拦下，需要看原评论判断（英文词按整词匹配、不分大小写）",
    "extra_tags": "发布后补充的标签（原画廊标签会全部复制，下面命名空间除外）",
    "drop_tag_namespaces": None,
    "batch_max_mb": "单次 Start Upload 的上限（网站限制单次不超过 500MB）",
    "batch_max_files": None,
    "upload_timeout_min": None,
    "delay_between_steps": "随机等待（秒），避免请求过快",
    "delay_between_galleries": "每部作品之间随机等待的秒数 [最少, 最多]",
    "headless": "是否隐藏浏览器窗口",
    "browser_path": "自动化用的浏览器 exe（Chromium 内核），留空使用 Edge",
    "source_dir": "漫画根目录（命令行不给目录时扫描这里；EXE 用设置页里的目录）",
    "mtu_dir": "本地 manga-translator-ui 目录，用来识别目次页的日文；留空则不识别",
    "att_config": "翻译 API 配置文件（含 [llm.clients.*]，用于罗马音标题等）；留空时用 source_dir 下的 att-mz/runtime/config.toml",
    "romaji_client": "上面配置文件里用哪个客户端，默认 extra",
}


def toml_value(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return str(v)
    if isinstance(v, list):
        return "[" + ", ".join(toml_value(x) for x in v) + "]"
    return json.dumps(str(v), ensure_ascii=False)


def write_config(cfg: dict) -> None:
    lines = ["# e-hentai 自动上传配置（EXE 界面保存设置时会重写本文件）"]
    for key, doc in CONFIG_DOCS.items():
        if key not in cfg:
            continue
        if doc:
            lines += ["", f"# {doc}"]
        lines.append(f"{key} = {toml_value(cfg[key])}")
    CONFIG_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8")


EXCLUDED_FILE = ROOT / "excluded.json"


def load_excluded() -> dict:
    """永久排除、不再上传的作品：{任务键: {"title", "time"}}，由 EXE 的右键菜单维护。"""
    try:
        return json.loads(EXCLUDED_FILE.read_text(encoding="utf-8")) if EXCLUDED_FILE.exists() else {}
    except ValueError:
        return {}


def load_state() -> dict:
    if STATE_PATH.exists():
        return json.loads(STATE_PATH.read_text(encoding="utf-8"))
    return {}


def save_state(state: dict) -> None:
    tmp = STATE_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(STATE_PATH)


def pause(rng) -> None:
    time.sleep(random.uniform(*rng))


def natural_key(s: str):
    return [int(t) if t.isdigit() else t for t in re.split(r"(\d+)", s.lower())]


# ---------------------------------------------------------------- 作品发现

@dataclass
class Item:
    gid: int
    token: str
    folder: Path
    cbz: Path
    result_dir: Path
    orig_pages: int = 0                          # 原画廊页数（ComicInfo 的 PageCount）
    meta: dict = field(default_factory=dict)
    images: list = field(default_factory=list)   # [(zip 内路径, 上传文件名, 字节数)]
    problems: list = field(default_factory=list)
    key: str = ""                                # 上传任务键：整本用 gid，拆出来的单篇用 gid-p起始页
    part: dict | None = None                     # 拆分出的单篇对应的目录条目
    pages: list = field(default_factory=list)    # 这个任务包含的原书页码
    nosrc: str = ""                              # 原画廊找不到（已删除）时：cbz 里记录的日文标题
    nosrc_from: str = ""                         # 这类作品在别的站（hitomi）找到的同一本，用作 raw 链接

    def __post_init__(self):
        self.key = self.key or str(self.gid)


NOSRC_BASE = 900_000_000   # 找不到原画廊的作品用 9 亿以上的编号（真实画廊编号远小于这个），凡是访问原画廊的地方都跳过


def is_nosrc(gid: int) -> bool:
    return int(gid) >= NOSRC_BASE


def is_item_dir(p: Path) -> bool:
    return bool(re.match(r"^\d+-", p.name)) and (p / f"{p.name.split('-', 1)[0]}.cbz").exists() or other_cbz(p) is not None


def other_cbz(p: Path) -> Path | None:
    """从别的站（比如紳士漫畫）下载的作品文件夹：里面只有一个 cbz，旁边有和 cbz 同名的翻译工作目录。"""
    if re.match(r"^\d+-", p.name) or not p.is_dir():
        return None
    cbzs = list(p.glob("*.cbz"))
    return cbzs[0] if len(cbzs) == 1 and (p.parent / cbzs[0].stem).is_dir() else None


SOURCE_LINKS = Path(__file__).with_name("cache") / "source_links.json"


def panda_find(title: str) -> tuple[int, str, int] | None:
    """e-hentai 搜索里搜不到的（多半被下架但画廊还在）：去 e-hentai 备份站 Panda Backup（panda.chaika.moe）按标题搜，
    拿到原画廊的 gid / token。日文标题相似度 0.8 以上才算。"""
    import difflib
    import urllib.parse

    def get(q: str):
        def call():
            req = urllib.request.Request("https://panda.chaika.moe/jsearch/?" + q, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        return net_retry(call, "Panda Backup")

    norm = lambda s: re.sub(r"[\s\-_・･!！?？⁉×x✕●○〇◯■□…、。,，~～「」『』]|\[[^\]]*\]|\([^)]*\)", "", html.unescape(s or "").lower())
    want = norm(title)
    words = re.sub(r"[\[(（【][^\])）】]*[\])）】]", " ", title).split()
    best, best_score = None, 0.0
    # 整句和前两个词各搜一遍；备份站 / 原画廊的标题常把「催眠」写成伏字（催● / ●● / 催〇），两种写法互换着都搜
    def variants(s: str) -> list[str]:
        out = [s]
        if "催眠" in s:
            out += [s.replace("催眠", x) for x in ("催●", "●●", "催〇")]
        if re.search(r"催[●〇○]|●●", s):
            out.append(re.sub(r"催[●〇○]|●●", "催眠", s))
        return out
    queries = [v for q in (" ".join(words), " ".join(words[:2])) if q for v in variants(q)]
    for q in dict.fromkeys(queries):
        try:
            results = get("q=" + urllib.parse.quote(q))
        except Exception as e:
            log(f"  Panda Backup 搜索失败：{e}")
            return None
        for r in (results if isinstance(results, list) else [])[:8]:
            try:
                a = get(f"archive={int(r['id'])}")
                score = difflib.SequenceMatcher(None, want, norm(a.get("title_jpn") or a.get("title"))).ratio()
                if score > best_score and a.get("gallery"):
                    g = get(f"gallery={int(a['gallery'])}")
                    m = GALLERY_RE.search(g.get("link") or "")
                    if m:
                        best, best_score = (int(m.group(1)), m.group(2), int(a.get("filecount") or 0),
                                            f"https://panda.chaika.moe/archive/{int(r['id'])}/"), score
            except Exception:
                continue
        if best_score >= 0.97:   # 几乎一字不差才提前停；同系列的本标题只差一两个字（美雪 / 深雪），要都比完再挑
            break
    if best and best_score >= 0.8:
        log(f"  在 Panda Backup 找到原画廊：{best[0]}（标题相似度 {best_score:.2f}）")
        return best
    return None


def find_source(folder: Path, title: str) -> tuple[int, str, int] | None:
    """没有原画廊链接的作品，按标题在 exhentai / e-hentai 上搜原画廊，标题（日文或英文）完全一致才算。

    结果记在 cache/source_links.json（键是作品文件夹名）；也可以手动在里面填 {"url": "原画廊链接"}。
    没搜到的 7 天后再搜。
    """
    import urllib.parse
    links = json.loads(SOURCE_LINKS.read_text(encoding="utf-8")) if SOURCE_LINKS.exists() else {}
    rec = links.get(folder.name, {})
    m = GALLERY_RE.search(rec.get("url") or "")
    if m:
        return int(m.group(1)), m.group(2), int(rec.get("pages", 0))
    if rec.get("checked") and datetime.now() - datetime.fromisoformat(rec["checked"]) < timedelta(days=7):
        return None
    norm = lambda s: re.sub(r"\s+", "", html.unescape(s or ""))
    words = re.sub(r"[\[(（【][^\])）】]*[\])）】]", " ", title).split()
    queries = [" ".join(words), " ".join(words[:2])] if words else []
    # 「催眠」常被写成伏字（催● / ●● / 催〇），两种写法互换着也搜一遍
    queries += [re.sub(r"催眠", x, q) for q in list(queries) if "催眠" in q for x in ("催●", "●●", "催〇")]
    queries += [re.sub(r"催[●〇○]|●●", "催眠", q) for q in list(queries) if re.search(r"催[●〇○]|●●", q)]
    found = None
    for q in dict.fromkeys(q for q in queries if q):
        pairs = []
        for host in ("exhentai.org", "e-hentai.org"):
            try:
                page = http_get(f"https://{host}/?f_search=" + urllib.parse.quote(q), True)
            except Exception:
                continue
            pairs += [(int(g), t) for g, t in GALLERY_RE.findall(page)]
        pairs = list(dict.fromkeys(pairs))[:25]
        meta = api_gdata(pairs) if pairs else {}
        for gid, token in pairs:
            g = meta.get(gid, {})
            if norm(title) in (norm(g.get("title_jpn")), norm(g.get("title"))):
                found = (gid, token, int(g.get("filecount", 0) or 0))
                break
        if found:
            break
    if not found:
        found = panda_find(title)
    if found:
        links[folder.name] = {"url": f"https://exhentai.org/g/{found[0]}/{found[1]}/", "pages": found[2], "title": title}
        if len(found) > 3:
            links[folder.name]["raw"] = found[3]   # 备份站上的页面：原画廊页面被删时，简介里的 raw 链接改指向这里
        found = found[:3]
        log(f"按标题找到原画廊：{folder.name} → {links[folder.name]['url']}")
    else:
        links[folder.name] = {"url": "", "checked": datetime.now().isoformat(timespec="seconds"), "title": title}
        log(f"{folder.name}：没有原画廊链接，按标题也没搜到，按「无原画廊」作品处理（也可以在 cache/source_links.json 里手动填 url）")
    SOURCE_LINKS.parent.mkdir(exist_ok=True)
    SOURCE_LINKS.write_text(json.dumps(links, ensure_ascii=False, indent=2), encoding="utf-8")
    return found


def find_items(paths: list[str]) -> list[Item]:
    folders: list[Path] = []

    def walk(p: Path, depth: int) -> None:
        if is_item_dir(p):
            folders.append(p)
        elif depth < 2:
            for d in sorted(x for x in p.iterdir() if x.is_dir()):
                if not (d.name.startswith((".", "_")) or d.name in SKIP_DIRS):
                    walk(d, depth + 1)

    for p in map(Path, paths):
        if p.is_dir():
            walk(p, 0)
        else:
            log(f"跳过无效路径：{p}")
    items = []
    for d in folders:
        cbz = other_cbz(d)
        if cbz:
            with zipfile.ZipFile(cbz) as z:
                info = next((n for n in z.namelist() if n.lower().endswith("comicinfo.xml")), None)
                text = z.read(info).decode("utf-8", "replace") if info else ""
            t = re.search(r"<Series>(.*?)</Series>", text, re.S)
            title = html.unescape(t.group(1)).strip() if t else d.name
            src = find_source(d, title)
            if src:
                items.append(Item(src[0], src[1], d, cbz, d.parent / cbz.stem / "manga_translator_work" / "result", src[2]))
            else:
                # 原画廊在 e-hentai 上找不到（多半已被删除）：按 cbz 里的标题生成信息，用户确认后可以上传
                import zlib
                fake = NOSRC_BASE + zlib.crc32(d.name.encode("utf-8")) % 99_999_999
                items.append(Item(fake, "", d, cbz, d.parent / cbz.stem / "manga_translator_work" / "result", 0, nosrc=title))
            continue
        gid = d.name.split("-", 1)[0]
        cbz, info = d / f"{gid}.cbz", d / "ComicInfo.xml"
        if not cbz.exists() or not info.exists():
            log(f"跳过 {d.name}：缺少 {gid}.cbz 或 ComicInfo.xml")
            continue
        text = info.read_text(encoding="utf-8", errors="replace")
        m = GALLERY_RE.search(text)
        if not m or m.group(1) != gid:
            log(f"跳过 {d.name}：ComicInfo.xml 里没有对应的原画廊链接")
            continue
        pc = re.search(r"<PageCount>(\d+)</PageCount>", text)
        items.append(Item(int(gid), m.group(2), d, cbz, d.parent / gid / "manga_translator_work" / "result",
                          int(pc.group(1)) if pc else 0))
    return items


TMP_CROP = re.compile(r"^tmp-\d+-r\d+\.\w+$", re.I)


def check_ready(item: Item, cfg: dict) -> None:
    """检查回填状态并列出待上传图片。"""
    if not item.result_dir.is_dir():
        item.problems.append("没有找到 manga_translator_work/result，可能还没翻译")
    else:
        newest = max((f.stat().st_mtime for f in item.result_dir.iterdir() if f.is_file()), default=0)
        if newest > item.cbz.stat().st_mtime + 60:
            item.problems.append("result 比 cbz 新，可能还没重新回填")
    with zipfile.ZipFile(item.cbz) as z:
        entries = [e for e in z.infolist() if not e.is_dir() and Path(e.filename).suffix.lower() in IMAGE_LIMIT_MB]
    # 翻译工具修图时留下的小裁切图（tmp-14-r003.jpg 这种）偶尔会被一起打进 cbz，不是正文页
    junk = [e for e in entries if TMP_CROP.match(Path(e.filename).name)]
    if junk:
        log(f"  {item.folder.name}：跳过 {len(junk)} 张翻译临时裁切图（{', '.join(Path(e.filename).name for e in junk[:3])}…）")
        entries = [e for e in entries if e not in junk]
    entries.sort(key=lambda e: natural_key(e.filename))
    seen = set()
    for i, e in enumerate(entries, 1):
        name = Path(e.filename).name
        if name.lower() in seen:
            name = f"{i:04d}_{name}"
        seen.add(name.lower())
        limit = IMAGE_LIMIT_MB[Path(name).suffix.lower()]
        if e.file_size > limit * 1024 * 1024 and Path(name).suffix.lower() == ".gif":
            item.problems.append(f"{name} 超过 {limit}MB，GIF 无法自动压缩")
        item.images.append((e.filename, name, e.file_size))
    if not item.images:
        item.problems.append("cbz 里没有图片")
    if len(item.images) > 2000:
        item.problems.append("超过 2000 张图片")


def make_batches(images: list, cfg: dict) -> list[list]:
    max_bytes, max_files = cfg["batch_max_mb"] * 1024 * 1024, cfg["batch_max_files"]
    batches, cur, size = [], [], 0
    for img in images:
        if cur and (size + img[2] > max_bytes or len(cur) >= max_files):
            batches.append(cur)
            cur, size = [], 0
        cur.append(img)
        size += img[2]
    if cur:
        batches.append(cur)
    return batches


# ---------------------------------------------------------------- 元数据

def net_retry(fn, what: str = "", tries: int = 3):
    """网络偶尔卡一下（读超时、连接被重置、网站 5xx / 429）就隔几秒重试，不要一次超时就让整个扫描失败。
    404 之类明确的回答不重试，原样抛出。"""
    import http.client
    import urllib.error
    for n in range(tries):
        try:
            return fn()
        except urllib.error.HTTPError as e:
            if e.code < 500 and e.code != 429 or n == tries - 1:
                raise
            err = e
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException, OSError) as e:
            if n == tries - 1:
                raise
            err = e
        log(f"  网络请求{('（' + what + '）') if what else ''}出错：{err}，{3 + 5 * n} 秒后重试")
        time.sleep(3 + 5 * n)


def api_gdata(pairs: list[tuple[int, str]]) -> dict[int, dict]:
    out = {}
    for i in range(0, len(pairs), 25):
        body = json.dumps({"method": "gdata", "gidlist": [list(p) for p in pairs[i:i + 25]], "namespace": 1}).encode()

        def call():
            req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)["gmetadata"]
        for g in net_retry(call, "e-hentai API"):
            if "error" not in g:
                out[int(g["gid"])] = g
        if i + 25 < len(pairs):
            time.sleep(1.5)
    return out


def insert_marker(title: str, marker: str, qualifiers: set[str], already: tuple[str, ...]) -> str:
    title = re.sub(r"\s+", " ", html.unescape(title or "")).strip()
    if not title or any(a in title for a in already):
        return title
    pos = len(title)
    while True:
        m = re.search(r"\s*\[([^\[\]]+)\]\s*$", title[:pos])
        if not m or m.group(1).strip().lower() not in qualifiers:
            break
        pos = m.start()
    head, tail = title[:pos].rstrip(), title[pos:].strip()
    return f"{head} {marker}" + (f" {tail}" if tail else "")


def ehtag_name(tag: str, namespace: str) -> str | None:
    """e-hentai 标签 → 标签库里的参考名（多为日文原名）。"""
    try:
        db = json.loads(EHTAG_DB.read_text(encoding="utf-8"))
        data = next(x for x in db["data"] if x["namespace"] == namespace)["data"]
        return (data.get(tag) or {}).get("name") or None
    except Exception:
        return None


def ehtag_lookup(name: str, namespace: str) -> str | None:
    """日文名 → e-hentai 标签（EhTagTranslation 标签库里 name 或 intro 写着这个名字的条目）。"""
    try:
        db = json.loads(EHTAG_DB.read_text(encoding="utf-8"))
        data = next(x for x in db["data"] if x["namespace"] == namespace)["data"]
    except Exception:
        return None
    norm = lambda s: re.sub(r"[\s・･]", "", s or "")
    want = norm(name)
    for tag, v in data.items():
        if norm(v.get("name")) == want:
            return tag
    for tag, v in data.items():   # 标签库的参考名有时是中文，原名写在简介里
        if want and want in norm(re.sub(r"<[^>]+>|!\[.*?\]\(.*?\)", "", v.get("intro") or "")):
            return tag
    return None


HITOMI = "https://ltn.gold-usergeneratedcontent.net"   # hitomi 的数据域名（原来的 ltn.hitomi.la 已经连不上）
HITOMI_DIR = Path(__file__).with_name("cache") / "hitomi"
HITOMI_TYPES = {"doujinshi": "Doujinshi", "manga": "Manga", "artistcg": "Artist CG", "gamecg": "Game CG", "imageset": "Image Set"}


def hitomi_get(path: str, cache_days: float) -> bytes | None:
    """读 hitomi 的数据文件（本地缓存）。"""
    import hashlib
    HITOMI_DIR.mkdir(parents=True, exist_ok=True)
    f = HITOMI_DIR / hashlib.md5(path.encode()).hexdigest()
    if f.exists() and time.time() - f.stat().st_mtime < cache_days * 86400:
        return f.read_bytes()
    try:
        req = urllib.request.Request(HITOMI + path, headers={"User-Agent": "Mozilla/5.0", "Referer": "https://hitomi.la/"})
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read()
    except Exception as e:
        if "404" not in str(e):   # 404 是试作者名另一种写法时的正常情况，不记
            log(f"  读取 hitomi {path} 失败：{e}")
        return None
    f.write_bytes(data)
    return data


def hitomi_find(jp_title: str, names: list[tuple[str, str]]) -> dict | None:
    """在 hitomi 上按作者 / 社团列出画廊，找日文标题一致的那一本（e-hentai 上已删除的本子常在这里还有）。"""
    import difflib
    import struct
    import urllib.parse
    # 去掉空格、标点、伏字（●〇）和 [] () 里的附注再比；不同站的写法常有出入（催眠 → ●●、セイ活 → 性活），
    # 所以按相似度找：同系列别的本一般只有 0.4~0.5，同一本在 0.85 以上，门槛取 0.8，日文原版优先
    norm = lambda s: re.sub(r"[\s\-_・･!！?？⁉×x✕●○〇◯■□…、。,，~～「」『』]|\[[^\]]*\]|\([^)]*\)", "", (s or "").lower())
    want = norm(jp_title)
    best, best_score = None, 0.0
    for ns, name in names:
        for slug in dict.fromkeys((name, name.replace(" ", "-"))):
            data = hitomi_get(f"/{ns}/{urllib.parse.quote(slug)}-all.nozomi", 7)
            if not data or len(data) < 4:
                continue
            ids = struct.unpack(f">{len(data) // 4}i", data[: len(data) // 4 * 4])

            def load(gid):
                js = hitomi_get(f"/galleries/{gid}.js", 365)   # 画廊信息基本不变，缓存一年
                try:
                    text = js.decode("utf-8") if js else ""
                    return json.loads(text[text.index("{"):]) if text else None
                except ValueError:
                    return None

            from concurrent.futures import ThreadPoolExecutor
            with ThreadPoolExecutor(8) as ex:   # 作者画廊多时一个个读太慢，8 路并行，每批读完先比对，找到就停
                for k in range(0, min(len(ids), 400), 32):
                    for info in ex.map(load, ids[k:k + 32]):
                        if not info or not want:
                            continue
                        got = norm(info.get("japanese_title") or info.get("title"))
                        if got == want:
                            return info
                        score = difflib.SequenceMatcher(None, want, got).ratio() + (0.02 if info.get("language") == "japanese" else 0)
                        if score > best_score:
                            best, best_score = info, score
        if best_score >= 0.8:
            break
    return best if best_score >= 0.8 else None


def nosrc_gallery(item: Item, cfg: dict) -> dict:
    """原画廊在 e-hentai 上找不到的作品：先去 hitomi 按作者 / 社团找同一本，抄分类、标签和发布时间；
    找不到就按 cbz 里记录的日文标题生成（标题罗马音、社团 / 作者标签）。都需要用户在编辑窗口确认。"""
    jp = re.sub(r"\s+", " ", item.nosrc).strip()
    tags, circle, author = [], "", ""
    lead = re.match(r"^\s*(?:\([^)]*\)\s*)?\[([^\]]+)\]", jp)
    if lead:
        who = lead.group(1).strip()
        ca = re.match(r"^(.*?)\s*\(([^()]+)\)\s*$", who)
        circle, author = (ca.group(1).strip(), ca.group(2).strip()) if ca else ("", who)
    names = {}
    for name, ns in ((author, "artist"), (circle, "group")):
        if name:
            tag = ehtag_lookup(name, ns) or romanize([name], cfg).get(name, "").lower()
            if tag and not JAPANESE.search(tag):
                names[ns] = tag
                tags.append(f"{ns}:{tag}")
    lead_en = " ".join(w.capitalize() for w in names.get("group", "").split())
    if names.get("artist"):
        a = " ".join(w.capitalize() for w in names["artist"].split())
        lead_en = f"{lead_en} ({a})" if lead_en else a
    info = hitomi_find(jp, [(ns, n) for ns, n in names.items()])
    if info:
        hid = info.get("id")
        item.nosrc_from = f"https://hitomi.la/galleries/{hid}.html"
        log(f"  {item.folder.name[:40]}：e-hentai 上找不到，在 hitomi 找到同一本（{hid}）")
        for x in info.get("tags") or []:
            ns = "female" if str(x.get("female")) == "1" else "male" if str(x.get("male")) == "1" else "other"
            tags.append(f"{ns}:{x['tag']}")
        tags += [f"character:{x['character']}" for x in info.get("characters") or []]
        tags += [f"parody:{x['parody']}" for x in info.get("parodys") or [] if x.get("parody") != "original"]
        # 英文标题按自己的日文标题转罗马音（hitomi 的英文标题质量参差：有的是日文，有的是机翻）
        en = re.sub(r"\[DL\s*(?:Version|Ban|版)\]", "[Digital]", romanize([jp], cfg).get(jp, jp), flags=re.I)
        if lead_en:
            en = re.sub(r"^\s*(\([^)]*\)\s*)?\[[^\]]+\]", lambda m: (m.group(1) or "") + f"[{lead_en}]", en, count=1)
        try:
            posted = int(datetime.fromisoformat(re.sub(r"([+-]\d\d)$", r"\1:00", info.get("date", ""))).timestamp())
        except ValueError:
            posted = 0
        return {"title": en, "title_jpn": jp, "category": HITOMI_TYPES.get(info.get("type"), "Doujinshi"),
                "tags": list(dict.fromkeys(tags)), "posted": posted}
    en = re.sub(r"\[DL\s*(?:Version|Ban|版)\]", "[Digital]", romanize([jp], cfg).get(jp, jp), flags=re.I)
    if lead_en:   # 英文标题里的社团 / 作者用标签里的标准写法（罗马音转换常把 Mille Crepe 写成 Milk Crepe）
        en = re.sub(r"^\s*(\([^)]*\)\s*)?\[[^\]]+\]", lambda m: (m.group(1) or "") + f"[{lead_en}]", en, count=1)
    return {"title": en, "title_jpn": jp, "category": "Doujinshi", "tags": tags, "posted": 0}


def build_meta(item: Item, g: dict, cfg: dict) -> None:
    note = item.folder / "upload_note.txt"
    comment = cfg.get("comment", "").strip()
    if note.exists():
        comment = (note.read_text(encoding="utf-8").strip() + "\n" + comment).strip()
    drop = tuple(f"{ns}:" for ns in cfg["drop_tag_namespaces"])
    tags = [t for t in g["tags"] if not t.startswith(drop)]
    tags += [t for t in cfg["extra_tags"] if t not in tags]
    item.meta = {
        "en": insert_marker(g["title"], "[Chinese]", EN_QUALIFIERS, ("[Chinese]",)),
        "jp": insert_marker(g.get("title_jpn", ""), "[中国翻訳]", JP_QUALIFIERS, ("中国翻訳", "中国語")),
        "category": CATEGORY.get(g["category"], "1"),
        "category_name": g["category"],
        "tags": tags,
        "comment": comment,
        "posted": datetime.fromtimestamp(int(g["posted"])),
        "nosrc": bool(item.nosrc), "nosrc_from": item.nosrc_from,
    }


PLACEHOLDER = "◆篇名◆"
AUTHOR_PLACEHOLDER = "[◆作者◆]"
ANTHOLOGY_LEADS = {"アンソロジー", "anthology", "よろず", "various"}
OVERRIDE_FILE = "upload_meta.json"
# 只适用于整本书、不适用于其中一篇的标签
WHOLE_BOOK_TAGS = {"other:anthology", "other:tankoubon"}


def partial_title(title: str, qualifiers: set[str], markers: tuple[str, ...]) -> str:
    """整本标题 → 节选标题：[作者] ◆篇名◆ (原书名) [中国翻訳] [DL版]"""
    if not title:
        return ""
    lead = re.match(r"^\s*((?:\([^)]*\)\s*)?\[([^\]]+)\])\s*", title)
    # 合集和杂志没有单一作者，换成占位符让人填这一篇的作者
    head = lead.group(1) + " " if lead and lead.group(2).strip().lower() not in ANTHOLOGY_LEADS else AUTHOR_PLACEHOLDER + " "
    rest = title[lead.end():] if lead else title
    tail = []
    while True:
        m = re.search(r"\s*\[([^\[\]]+)\]\s*$", rest)
        if not m or not (m.group(1).strip().lower() in qualifiers or any(k in m.group(0) for k in markers)):
            break
        tail.insert(0, m.group(0).strip())
        rest = rest[:m.start()]
    return f"{head}{PLACEHOLDER} ({rest.strip()}) {' '.join(tail)}".strip()


CACHE_DIR = ROOT / "cache"
# 页码后面可以跟 - : . ) 或一串 ･・ 之类的点（「004･･････ 篇名 …… 作者」）
TOC_LINE = re.compile(r"^\s*(?:p\.?\s*|P\s*)?(\d{1,4})(?:\s*[-–—:：.、)）･・·•]+\s*|\s*[pP]\b\s*|\s+)(.+?)\s*$")
LEADER =re.compile(r"\s*(?:[.．・･·…‥]{2,}|─{2,}|-{3,})\s*")


def translated_pages(item: Item) -> list[int]:
    """原书里被翻译的页码：result 目录里的文件名保留了原始编号。"""
    pages = []
    if item.result_dir.is_dir():
        for f in item.result_dir.iterdir():
            if f.suffix.lower() in IMAGE_LIMIT_MB and f.stem.isdigit():
                pages.append(int(f.stem))
    return sorted(pages)


COOKIE_FILE = CACHE_DIR / "cookies.json"


def save_cookies(ctx) -> None:
    """把浏览器里的 e-hentai / exhentai 登录 cookie 存到本地，供读取 exhentai 画廊评论用。"""
    CACHE_DIR.mkdir(exist_ok=True)
    cookies = [c for c in ctx.cookies(["https://e-hentai.org", "https://exhentai.org"])
               if c["name"] in ("ipb_member_id", "ipb_pass_hash", "igneous", "sk", "star")]
    COOKIE_FILE.write_text(json.dumps(cookies, ensure_ascii=False), encoding="utf-8")


def http_get(url: str, use_cookies: bool, data: dict | None = None) -> str:
    """GET（给了 data 就 POST 表单），可带上登录 cookie。"""
    import http.cookiejar
    import urllib.parse
    jar = http.cookiejar.CookieJar()
    if use_cookies and COOKIE_FILE.exists():
        for c in json.loads(COOKIE_FILE.read_text(encoding="utf-8")):
            for domain in (".e-hentai.org", ".exhentai.org"):
                jar.set_cookie(http.cookiejar.Cookie(0, c["name"], c["value"], None, False, domain, True, True, "/", True,
                                                     False, None, False, None, None, {}))
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    opener.addheaders = [("User-Agent", "Mozilla/5.0")]
    body = urllib.parse.urlencode(data).encode() if data else None

    def call():
        with opener.open(url, data=body, timeout=30) as r:
            return r.read().decode("utf-8", "replace")
    # 发评论之类的 POST 不重试：超时时其实可能已经提交成功，重试会重复发
    return call() if body else net_retry(call, re.sub(r"^https?://([^/]+).*", r"\1", url))


def comments_fresh(gid: int) -> bool:
    cache = CACHE_DIR / f"comments_{gid}.json"
    try:
        data = json.loads(cache.read_text(encoding="utf-8"))
        return datetime.now() - datetime.fromisoformat(data["fetched"]) < timedelta(hours=24)
    except (OSError, ValueError, KeyError, TypeError):
        return False


def prefetch_comments(items: list) -> None:
    """扫描前把缓存过期的原画廊评论并行读好（每天第一次扫描要读一百多个画廊，一个个读要好几分钟）。"""
    from concurrent.futures import ThreadPoolExecutor, as_completed
    need = list({i.gid: i for i in items if not comments_fresh(i.gid)}.values())
    if not need:
        return
    log(f"读取 {len(need)} 个原画廊的评论（缓存超过 24 小时）")
    with ThreadPoolExecutor(4) as ex:   # 4 路并发，每路读完还会停 1.2 秒，不至于刷太快
        futs = [ex.submit(fetch_comments, i.gid, i.token) for i in need]
        for k, _ in enumerate(as_completed(futs), 1):
            emit("scan_progress", text=f"正在读取原画廊评论 {k}/{len(need)}…（每天第一次扫描会慢一些）")


def fetch_comments(gid: int, token: str, offline: bool = False) -> list[dict]:
    if is_nosrc(gid):
        return []
    return _fetch_comments(gid, token, offline)


def _fetch_comments(gid: int, token: str, offline: bool = False) -> list[dict]:
    """原画廊的全部评论（含原上传者简介，作者/时间/正文），在本地缓存 24 小时；
    e-hentai 上没有的画廊，用登录 cookie 去 exhentai 读。
    offline：已上传完成 / 永久排除的作品不再联网，有缓存（不管多旧）就用缓存，没有就当没评论。"""
    CACHE_DIR.mkdir(exist_ok=True)
    cache = CACHE_DIR / f"comments_{gid}.json"
    if cache.exists():
        data = json.loads(cache.read_text(encoding="utf-8"))
        if isinstance(data, dict) and (offline or datetime.now() - datetime.fromisoformat(data["fetched"]) < timedelta(hours=24)):
            return data["comments"]
    if offline:
        return []
    page, err = "", None
    for host, cookies in (("e-hentai.org", False), ("exhentai.org", True)):
        if cookies and not COOKIE_FILE.exists():
            err = err or "需要先在 EXE 里登录才能读取 exhentai 画廊"
            break
        try:
            page = http_get(f"https://{host}/g/{gid}/{token}/?hc=1&nw=always", cookies)
            if 'class="c6"' in page or 'id="cdiv"' in page:
                break
            page, err = "", f"{host} 没有返回画廊页面"
        except Exception as e:
            page, err = "", f"{host}：{e}"
    if not page:
        log(f"  读取原画廊 {gid} 的评论失败（{err}）")
        return []
    comments = parse_comments(page)
    cache.write_text(json.dumps({"fetched": datetime.now().isoformat(timespec="seconds"), "comments": comments, "host": host},
                                ensure_ascii=False), encoding="utf-8")
    time.sleep(1.2)
    return comments


DEFAULT_NOTICE_KEYWORDS = [
    "禁止", "勿扰", "勿擾", "请勿", "請勿", "勿翻", "拒绝机翻", "拒絕機翻",
    "预定", "預定", "预订", "占坑", "认领", "認領",
    "正在翻译", "正在翻譯", "翻译中", "翻譯中", "汉化中", "漢化中", "正在汉化", "在翻了", "我来翻", "我會翻",
]


DEFAULT_REVIEW_KEYWORDS = ["组内", "組內", "翻译", "翻譯", "AI"]


def keyword_pos(text: str, word: str) -> int:
    """关键词在评论里的位置（找不到为 -1）；纯英文词按整词匹配，不分大小写，避免 said、again 之类误判。"""
    w = word.strip()
    if not w:
        return -1
    if re.fullmatch(r"[A-Za-z]+", w):
        m = re.search(rf"(?<![A-Za-z]){re.escape(w)}(?![A-Za-z])", text, re.I)
        return m.start() if m else -1
    return text.lower().find(w.lower())


def find_notices(comments: list[dict], cfg: dict) -> list[dict]:
    """原画廊评论（含原上传者简介）里的限制说法：
    block = 禁止、勿扰、已预定之类，review = 组内、翻译、AI 之类需要看原评论判断的。"""
    levels = (("block", cfg.get("notice_keywords") or DEFAULT_NOTICE_KEYWORDS),
              ("review", cfg.get("review_keywords") or DEFAULT_REVIEW_KEYWORDS))
    out = []
    for c in comments:
        for level, words in levels:
            hit = next(((w, p) for w in words if (p := keyword_pos(c["text"], w)) >= 0), None)
            if hit:
                i = max(0, hit[1] - 30)
                out.append({"id": c.get("id", ""), "level": level, "keyword": hit[0].strip(),
                            "author": "原上传者" if c.get("uploader") else c.get("author", ""),
                            "posted": c.get("posted", "")[:10], "snippet": " ".join(c["text"][i:i + 120].split()),
                            "text": c["text"]})
                break
    return out


def parse_toc_entry(page: int, rest: str) -> dict:
    """「03 - Nunnu / ぬんぬ ....『タイトル』」→ 页码、作者（英/日）、篇名。"""
    rest = re.sub(r"^[・･·•■◆□●\-–—\s]+", "", rest)   # 有的目录在作者前面加了 ・ ■ 之类的符号
    author, title = "", rest
    parts = [p for p in LEADER.split(rest) if p.strip()]
    if len(parts) >= 2:
        author, title = parts[0], " ".join(parts[1:])
        # 也有「篇名 …… 作者 / Romaji」的写法：只有最后一段带「 / 」时，作者在后面
        if "/" in parts[-1] and "/" not in parts[0]:
            author, title = parts[-1], " ".join(parts[:-1])
    else:
        m = re.search(r"[『「](.+?)[』」]", rest)
        if m:
            title, author = m.group(1), (rest[:m.start()] + rest[m.end():])
    author = author.strip(" -–—:：/").strip()
    title = re.sub(r"^[『「]|[』」]$", "", title.strip()).strip()
    if "/" in author:
        en, jp = (s.strip() for s in author.split("/", 1))
        if JAPANESE.search(en) and not JAPANESE.search(jp):
            en, jp = jp, en   # 有的目录写成「日文名 / 罗马音」
    else:
        en = jp = author
    return {"page": page, "author_en": en, "author_jp": jp, "title": title, "raw": f"{page:02d} - {rest}"}


def parse_toc(comments: list[dict]) -> list[dict]:
    """在评论里找「页码 - 作者 … 篇名」形式的目录，取条目最多的一条。"""
    best = []
    for c in comments:
        entries = []
        for line in c["text"].splitlines():
            m = TOC_LINE.match(line)
            if m and not m.group(2).startswith("http"):
                entries.append(parse_toc_entry(int(m.group(1)), m.group(2)))
        # 目录末尾偶尔会多出页码倒退的一行（预告、附录之类），跳过这种个别行，其余照常用
        # 这种行标上 out_of_order：点选列表里照样列出来可以手动选，只是不参与按页码自动匹配
        kept = []
        for e in entries:
            if not kept or e["page"] > kept[-1]["page"]:
                kept.append(e)
            else:
                e["out_of_order"] = True
        if len(kept) >= 3 and len(kept) >= 0.8 * len(entries) and len(kept) > len([x for x in best if not x.get("out_of_order")]):
            best = entries
    return best


def match_toc(item: Item, toc: list[dict]) -> list[dict]:
    """按翻译的原始页码找出对应的目录条目（每条带上它的结束页）。"""
    done = set(item.pages)
    toc = [e for e in toc if not e.get("out_of_order")]   # 页码倒退的行不参与自动匹配
    if not done or not toc:
        return []
    hits = []
    for i, e in enumerate(toc):
        end = toc[i + 1]["page"] - 1 if i + 1 < len(toc) else max(item.orig_pages, e["page"])
        overlap = len(done & set(range(e["page"], end + 1)))
        if overlap >= max(2, 0.2 * min(len(done), end - e["page"] + 1)):
            hits.append({**e, "end": end})
    return hits


# ---------------------------------------------------------------- 罗马音

JAPANESE = re.compile(r"[぀-ヿ㐀-鿿ｦ-ﾟ]")
ROMAJI_CACHE = CACHE_DIR / "romaji.json"
ROMAJI_PROMPT = """You convert Japanese doujinshi / manga titles and author names into the romanized form used in E-Hentai English gallery titles.
Rules:
- Use Hepburn romanization with long vowels written out (ou, uu), e.g. 催眠 -> Saimin, 先輩 -> Senpai, 後輩 -> Kouhai.
- Katakana loanwords become the original English (or other source language) word: ヘッドホン -> Headphone, アプリ -> Appli, メイド -> Maid.
- Capitalize each word; particles stay lowercase: no, wa, ga, o (for を), ni, de, to, mo, e (for へ).
- Keep numbers, Latin letters and symbols such as ♥ ~ ! ? as they are; drop 「」『』 and turn full-width punctuation into ASCII.
- A personal or circle name is romanized as a name (in the order written).
- For a full gallery title keep its structure: (Event) [Circle (Artist)] Title (Parody); romanize every part.
- Parody / series names follow E-Hentai custom: the romanized Japanese title (葬送のフリーレン -> Sousou no Frieren, 五等分の花嫁 -> Gotoubun no Hanayome); use English only when the official name itself is English or katakana English (ブルーアーカイブ -> Blue Archive, 東方Project -> Touhou Project).
- Censored characters such as 〇 ● ○ stand for one hidden kana/kanji; restore the intended word from context (催〇 -> Saimin, エロ〇キ -> Ero Gaki, 〇〇ちゃん stays as it is if unknowable).
- Romanize carefully letter by letter: マゾ -> Mazo, メス -> Mesu, ガキ -> Gaki; do not guess other words.
Reply with a JSON object that maps every input string to its romanization, nothing else."""


def llm_client(cfg: dict) -> tuple[str, str, str]:
    att = cfg.get("att_config") or (DEFAULT_SOURCE and str(DEFAULT_SOURCE / "att-mz" / "runtime" / "config.toml"))
    if not att:
        raise RuntimeError("config.toml 里没有设置 att_config（翻译 API 配置文件）")
    with Path(att).open("rb") as f:
        c = tomllib.load(f)["llm"]["clients"][cfg.get("romaji_client", "extra")]
    url = c["url"].rstrip("/")
    if not url.endswith("chat/completions"):
        url += "/chat/completions"
    return url, c["api_key"], c["model"]


def romanize(texts: list[str], cfg: dict) -> dict[str, str]:
    """把日文篇名、作者名转成罗马音（调用 att 配置里的翻译 API，结果缓存在本地）。"""
    CACHE_DIR.mkdir(exist_ok=True)
    cache = json.loads(ROMAJI_CACHE.read_text(encoding="utf-8")) if ROMAJI_CACHE.exists() else {}
    todo = sorted({t for t in texts if t and JAPANESE.search(t) and t not in cache})
    if todo:
        try:
            url, key, model = llm_client(cfg)
            body = json.dumps({"model": model, "temperature": 0, "response_format": {"type": "json_object"},
                               "messages": [{"role": "system", "content": ROMAJI_PROMPT},
                                            {"role": "user", "content": json.dumps(todo, ensure_ascii=False)}]}).encode()
            req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json",
                                                                  "Authorization": f"Bearer {key}"})
            with urllib.request.urlopen(req, timeout=120) as r:
                content = json.load(r)["choices"][0]["message"]["content"]
            got = json.loads(re.sub(r"^```(?:json)?|```$", "", content.strip()).strip())
            for t in todo:
                if isinstance(got.get(t), str) and got[t].strip() and not JAPANESE.search(got[t]):
                    cache[t] = re.sub(r"\s+", " ", got[t]).strip()
            ROMAJI_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
        except Exception as e:
            log(f"  罗马音转换失败，英文标题里保留日文：{e}")
    return {t: cache.get(t, t) for t in texts}


OCR_GUESS_CACHE = CACHE_DIR / "ocr_guess.json"
OCR_GUESS_PROMPT = """You identify which story of a Japanese manga anthology or magazine a set of pages belongs to.
You get OCR text from the first pages of one story (it often starts with a title page showing the story title and the author name)
and the list of artists credited in the whole anthology, written as romanized E-Hentai artist tags.
Decide which candidate is the author of this story and, if it appears in the text, the story's Japanese title.
The author must be one of the candidates; answer null when the text does not let you tell. Do not guess from art style.
Reply with JSON only: {"author_tag": "<candidate or null>", "author_jp": "<author name as written in the text, or empty>",
"title_jp": "<story title as written, or empty>", "confidence": <0-1>, "reason": "<one short sentence in Chinese>"}"""


def ocr_text(item: Item, pages: list[int], limit: int = 2) -> str:
    """这一篇前几页的 OCR 原文（manga-translator-ui 的 json 里每个文字区域的 text）。"""
    out = []
    json_dir = item.result_dir.parent / "json"
    for p in pages[:limit]:
        f = json_dir / f"{p:08d}_translations.json"
        if not f.exists():
            continue
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except ValueError:
            continue
        texts = [r.get("text", "").strip() for page in data.values() if isinstance(page, dict)
                 for r in page.get("regions", []) if r.get("text", "").strip()]
        if texts:
            out.append(f"--- 第 {p} 页 ---\n" + "\n".join(texts))
    return "\n".join(out)


def ocr_guess(item: Item, cfg: dict) -> dict | None:
    """合集评论区没有目录时：用这一篇前两页的 OCR 文字 + 原画廊的作者名单，让模型判断作者和篇名（结果缓存）。"""
    candidates = [t.split(":", 1)[1] for t in item.meta["default_tags"] if t.startswith("artist:")]
    pages = item.pages
    if len(candidates) < 2 or not pages:
        return None
    key = f"{item.gid}-p{pages[0]}"
    cache = json.loads(OCR_GUESS_CACHE.read_text(encoding="utf-8")) if OCR_GUESS_CACHE.exists() else {}
    if key in cache:
        return cache[key]
    text = ocr_text(item, pages)
    if not text:
        return None
    try:
        url, api_key, model = llm_client(cfg)
        body = json.dumps({"model": model, "temperature": 0, "response_format": {"type": "json_object"},
                           "messages": [{"role": "system", "content": OCR_GUESS_PROMPT},
                                        {"role": "user", "content": json.dumps({"candidates": candidates, "ocr": text[:6000]},
                                                                               ensure_ascii=False)}]}).encode()
        req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"})
        with urllib.request.urlopen(req, timeout=120) as r:
            content = json.load(r)["choices"][0]["message"]["content"]
        guess = json.loads(re.sub(r"^```(?:json)?|```$", "", content.strip()).strip())
    except Exception as e:
        log(f"  用 OCR 推测 {item.gid} 的作者失败：{e}")
        return None
    if guess.get("author_tag") not in candidates:
        guess["author_tag"] = None
    CACHE_DIR.mkdir(exist_ok=True)
    cache[key] = guess
    OCR_GUESS_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
    return guess


def page_segments(pages: list[int]) -> list[list[int]]:
    """翻译页码按连续分段：[81..106, 108..126, 185..195]。"""
    segs: list[list[int]] = []
    for p in pages:
        if segs and p == segs[-1][-1] + 1:
            segs[-1].append(p)
        else:
            segs.append([p])
    return segs


TITLE_GUESS_PROMPT = """You name an excerpt of a single-author Japanese manga volume (tankoubon) for an E-Hentai gallery title.
The translator only translated some chapters. You get the volume title and OCR text from the first page of each translated page range
(the first one is usually a chapter title page). Give the Japanese title of the translated part as it should appear in the gallery title:
the chapter title with its episode marker (5話 / 第5話 / 後編 ...) if it is one chapter; if several ranges belong to the same chapter or series,
combine them briefly (e.g. "お嬢様学校の変態教師 5話・後日談"). Use only words that appear in the OCR text; OCR often misses large
stylized title lettering, so keep what is legible and never invent words. Do not repeat the volume title.
Reply with JSON only: {"title_jp": "<title or empty>", "confidence": <0-1>, "reason": "<one short sentence in Chinese>"}"""


def ocr_title_guess(item: Item, cfg: dict) -> dict | None:
    """单行本节选（作者只有一位、评论区没目录）：用每一段第一页的 OCR 文字让模型推测篇名（结果缓存，需用户确认）。"""
    segs = page_segments(item.pages)
    if not segs:
        return None
    key = f"{item.gid}-title-p{segs[0][0]}-{len(segs)}"
    cache = json.loads(OCR_GUESS_CACHE.read_text(encoding="utf-8")) if OCR_GUESS_CACHE.exists() else {}
    if key in cache:
        return cache[key]
    text = "\n".join(t for t in (ocr_text(item, [s[0]], 1) for s in segs[:6]) if t)
    if not text:
        return None
    guess = llm_json(cfg, TITLE_GUESS_PROMPT, {"volume_title": item.meta.get("default_jp", ""), "ocr": text[:6000]},
                     f"用 OCR 推测 {item.gid} 的篇名")
    if guess is None:
        return None
    cache[key] = guess
    CACHE_DIR.mkdir(exist_ok=True)
    OCR_GUESS_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
    return guess


TOC_PAGE_PROMPT = """You read the OCR text of a table-of-contents page (目次 / CONTENTS) from a Japanese manga volume or magazine.
Extract every story entry with its starting page number, title and author (author only if the page lists one).
Ignore headings, the volume title and items without a page number. Keep titles exactly as written (fix obvious OCR spacing only).
Reply with JSON only: {"entries": [{"page": <int>, "title": "<title>", "author": "<author or empty>"}]}"""


def find_toc_page(item: Item) -> int | None:
    """翻译的前几页里有没有目次页（OCR 文字里有「目次 / もくじ / CONTENTS」）。只看本地 OCR 结果，不联网。"""
    for p in item.pages[:3]:
        if re.search(r"目\s*次|もくじ|CONTENTS", ocr_text(item, [p], 1), re.I):
            return p
    return None


MTU_DIR = _path_setting("mtu_dir")   # 本地 manga-translator-ui（只借它的日文 OCR；config.toml 的 mtu_dir，不填就不识别目次页）
TOC_OCR_CACHE = Path(__file__).with_name("cache") / "toc_ocr.json"
IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".webp")


def ocr_images(paths: list[Path]) -> dict[str, str]:
    """用本地 manga-translator-ui 的「只导出原文」模式识别几张图里的日文（目次页用），按文件和修改时间缓存。
    图片先复制到 cache\\ocr_tmp 再识别，不会碰作品目录里已有的翻译数据。"""
    import os
    import subprocess
    cache = json.loads(TOC_OCR_CACHE.read_text(encoding="utf-8")) if TOC_OCR_CACHE.exists() else {}
    keyf = lambda p: f"{p}|{int(p.stat().st_mtime)}|{p.stat().st_size}"
    todo = [p for p in paths if keyf(p) not in cache]
    py = MTU_DIR / ".venv" / "Scripts" / "python.exe" if MTU_DIR else None
    if todo and py and py.exists():
        tmp = CACHE_DIR / "ocr_tmp"
        shutil.rmtree(tmp, ignore_errors=True)
        tmp.mkdir(parents=True)
        names = {}
        for i, p in enumerate(todo):
            dst = tmp / f"{i:03d}{p.suffix.lower()}"
            shutil.copy2(p, dst)
            names[dst.stem] = p
        conf = json.loads((MTU_DIR / "config" / "config.json").read_text(encoding="utf-8"))
        conf.setdefault("cli", {}).update(template=True, save_text=True, load_text=False, translate_json_only=False,
                                          generate_and_export=False, save_to_source_dir=True)
        conf.setdefault("translator", {})["translator"] = "none"
        conf_file = CACHE_DIR / "ocr_only.json"
        conf_file.write_text(json.dumps(conf, ensure_ascii=False), encoding="utf-8")
        emit("scan_progress", text=f"正在识别 {len(todo)} 张目次候选页的文字（第一次约 1 分钟）…")
        log(f"  用 manga-translator-ui 识别 {len(todo)} 张目次候选页")
        try:
            subprocess.run([str(py), "-X", "utf8", "-m", "manga_translator", "local", "-i", str(tmp), "--config", str(conf_file), "--overwrite"],
                           cwd=MTU_DIR, env=dict(os.environ, PYTHONUTF8="1"), capture_output=True, timeout=900)
        except Exception as e:
            log(f"  识别目次页失败：{e}")
        for stem, p in names.items():
            f = tmp / "manga_translator_work" / "json" / f"{stem}_translations.json"
            text = ""
            if f.exists():
                data = json.loads(f.read_text(encoding="utf-8"))
                regs = [r for v in data.values() if isinstance(v, dict) for r in v.get("regions", [])]
                regs.sort(key=lambda r: (r.get("center", [0, 0])[1], r.get("center", [0, 0])[0]))
                text = "\n".join(r.get("text", "").strip() for r in regs if r.get("text", "").strip())
            cache[keyf(p)] = text
        CACHE_DIR.mkdir(exist_ok=True)
        TOC_OCR_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
    return {str(p): cache.get(keyf(p), "") for p in paths}


def find_raw_toc(item: Item, partial: bool) -> tuple[Path, str] | None:
    """原图文件夹里没翻译的图（比如单独放进去的目次页 004.webp）：识别文字，有「目次 / Contents」的就是目次页。
    候选最多 6 张：优先不是 8 位页码命名的，其次靠近开头或结尾的。"""
    src = item.result_dir.parent.parent
    if not src.is_dir():
        return None
    done = set(item.pages)
    num = lambda p: int(re.sub(r"\D", "", p.stem) or -1)
    # 还不确定是节选时，只看单独放进去、不是 8 位页码命名的图（避免把没翻译的差分页都识别一遍）
    cand = [p for p in src.iterdir() if p.suffix.lower() in IMAGE_EXTS and num(p) not in done
            and (partial or not re.fullmatch(r"\d{8}", p.stem))]
    if not cand:
        return None
    last = item.orig_pages or max(done | {0})
    cand.sort(key=lambda p: (bool(re.fullmatch(r"\d{8}", p.stem)), min(abs(num(p)), abs(last - num(p)))))
    for p, text in ocr_images(cand[:6]).items():
        if re.search(r"目\s*次|もくじ|contents", text, re.I):
            return Path(p), text
    return None


PAGE_NO = re.compile(r"(?:[0-9０-９]{3}|[OＯo][0-9０-９]{2})")   # 目次里的三位页码（OCR 常把开头的 0 认成字母 O）


def parse_toc_text(text: str) -> list[dict]:
    """目次页文字按「三位页码＋篇名」直接切分（一行里挤了好几条也能切开）。页码要递增、至少 3 条，否则返回空让模型来整理。"""
    out = []
    for line in text.splitlines():
        marks = list(PAGE_NO.finditer(line))
        for i, m in enumerate(marks):
            title = line[m.end(): marks[i + 1].start() if i + 1 < len(marks) else len(line)].strip(" ・.…:：-")
            page = int(m.group(0).translate(str.maketrans("０１２３４５６７８９OＯo", "0123456789000")))
            if title:
                out.append({"page": page, "title": title, "author": ""})
    pages = [e["page"] for e in out]
    return out if len(out) >= 3 and pages == sorted(pages) else []


def toc_from_text(item: Item, text: str, tag: str, cfg: dict) -> list[dict]:
    """把目次页的文字整理成目录条目（格式和评论区目录一样），结果缓存。"""
    key = f"{item.gid}-toc-{tag}"
    cache = json.loads(OCR_GUESS_CACHE.read_text(encoding="utf-8")) if OCR_GUESS_CACHE.exists() else {}
    direct = parse_toc_text(text)
    if direct:
        cache[key] = {"entries": direct}   # 规整的「页码＋篇名」直接切分，篇名照搬 OCR 原文，不经过模型
    if key not in cache:
        got = llm_json(cfg, TOC_PAGE_PROMPT, {"ocr": text[:6000]}, f"整理 {item.gid} 的目次页")
        if got is None:
            return []
        cache[key] = got
        CACHE_DIR.mkdir(exist_ok=True)
        OCR_GUESS_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
    out = []
    for e in cache[key].get("entries", []):
        try:
            p = int(e.get("page"))
        except (TypeError, ValueError):
            continue
        title, author = str(e.get("title") or "").strip(), str(e.get("author") or "").strip()
        if title:
            out.append({"page": p, "author_en": author, "author_jp": author, "title": title, "raw": f"{p:02d} - {title}（目次页）"})
    out.sort(key=lambda x: x["page"])
    return out


def llm_json(cfg: dict, system: str, user: dict, what: str) -> dict | None:
    """调 DeepSeek（extra 客户端）拿一个 JSON 结果；失败时记日志返回 None。"""
    try:
        url, api_key, model = llm_client(cfg)
        body = json.dumps({"model": model, "temperature": 0, "response_format": {"type": "json_object"},
                           "messages": [{"role": "system", "content": system},
                                        {"role": "user", "content": json.dumps(user, ensure_ascii=False)}]}).encode()
        req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"})
        with urllib.request.urlopen(req, timeout=120) as r:
            content = json.load(r)["choices"][0]["message"]["content"]
        return json.loads(re.sub(r"^```(?:json)?|```$", "", content.strip()).strip())
    except Exception as e:
        log(f"  {what}失败：{e}")
        return None


# ---------------------------------------------------------------- 节选与拆分

def load_override(item: Item) -> dict | None:
    """upload_meta.json：整本作品直接存字段，拆分出的单篇存在 parts["起始页"] 下。"""
    path = item.folder / OVERRIDE_FILE
    if not path.exists():
        return None
    data = json.loads(path.read_text(encoding="utf-8"))
    if item.part:
        return data.get("parts", {}).get(str(item.part["page"]))
    # 只有真的保存过标题 / 标签才算自定义；确认过的评论、整本标记、批量加的标签不算
    ov = {k: v for k, v in data.items() if k in ("en", "jp", "tags")}
    return ov or None


def name_part(item: Item, hit: dict | None, cfg: dict) -> None:
    """节选的建议标题：有目录条目就自动填作者、篇名（英文标题转罗马音），否则留占位符。"""
    m = item.meta
    m["suggest_en"] = partial_title(m["default_en"], EN_QUALIFIERS, ("[Chinese]",))
    m["suggest_jp"] = partial_title(m["default_jp"], JP_QUALIFIERS, ("中国翻訳",))
    m["suggest_tags"] = [t for t in m["default_tags"] if t not in WHOLE_BOOK_TAGS]
    if m.get("anthology"):
        m["suggest_tags"] = basic_tags(m["suggest_tags"], cfg)
    m["auto_named"], m["ocr_guessed"] = [], False
    if not hit and m.get("anthology"):
        # 评论区没有目录：用这一篇前两页的 OCR 文字推测作者和篇名，需要用户确认
        g = ocr_guess(item, cfg)
        if g and g.get("author_tag"):
            hit = {"title": g.get("title_jp", "") or "", "author_en": g["author_tag"].title(),
                   "author_jp": g.get("author_jp", "") or g["author_tag"].title(),
                   "raw": f"根据第一页文字推测：{g.get('reason', '')}", "ocr": True}
    if not hit and not m.get("anthology"):
        # 单行本节选（作者只有一位）：用每一段第一页的 OCR 文字推测篇名，需要用户确认
        g = ocr_title_guess(item, cfg)
        if g and (g.get("title_jp") or "").strip():
            hit = {"title": g["title_jp"].strip(), "author_en": "", "author_jp": "",
                   "raw": f"根据各段第一页文字推测：{g.get('reason', '')}", "ocr": True}
    if not hit or not (hit["title"] or hit.get("ocr")):
        return
    hit = dict(hit, title=clean_title(hit["title"]))
    roma = romanize([x for x in (hit["title"], hit["author_en"]) if x], cfg)
    roma = {k: clean_title(v) if k == hit["title"] else v for k, v in roma.items()}
    # 作者对到原画廊的 artist 标签上，用标签里的标准写法（目录里的罗马音常有错字）
    candidates = [t.split(":", 1)[1] for t in m["default_tags"] if t.startswith("artist:")]
    matched = match_artist([hit["author_en"], roma.get(hit["author_en"], ""), hit["author_jp"]], candidates)
    author_en = matched.title() if matched else roma.get(hit["author_en"], hit["author_en"])
    author_jp = hit["author_jp"]
    if matched and (not author_jp or re.fullmatch(r"[\x00-\x7f]+", author_jp)):
        author_jp = matched.title()
    # 英文标题里不留日文专用的合字符号（⁉ ‼ → !? !!）
    roma = {k: v.replace("⁉", "!?").replace("‼", "!!").replace("⁈", "?!") for k, v in roma.items()}
    for key, title, author in (("suggest_en", roma.get(hit["title"], ""), author_en),
                               ("suggest_jp", hit["title"], author_jp)):
        t = m[key].replace(PLACEHOLDER, title) if title else m[key]
        if AUTHOR_PLACEHOLDER in t:
            t = t.replace(AUTHOR_PLACEHOLDER, f"[{author}]" if author else "").strip()
        m[key] = t
    m["ocr_guessed"] = bool(hit.get("ocr"))
    # 合集只保留这一篇作者的 artist 标签
    wanted = {matched} if matched else {roma.get(hit["author_en"], hit["author_en"]).lower(), hit["author_en"].lower()} - {""}
    if wanted & {t.split(":", 1)[1] for t in m["suggest_tags"] if t.startswith("artist:")}:
        m["suggest_tags"] = [t for t in m["suggest_tags"] if not t.startswith("artist:") or t.split(":", 1)[1] in wanted]
    m["auto_named"] = [hit["raw"]]


def toc_options(item: Item, toc: list[dict], cfg: dict) -> list[dict]:
    """目录里每一篇对应的建议标题和标签（和自动命名同一套规则），编辑窗口里点哪篇就直接填哪篇。"""
    toc = [e for e in toc if e["title"]]
    if not toc:
        return []
    romanize([x for e in toc for x in (clean_title(e["title"]), e["author_en"]) if x], cfg)   # 一次请求，后面都走缓存
    out = []
    for e in toc:
        tmp = Item(item.gid, item.token, item.folder, item.cbz, item.result_dir, item.orig_pages,
                   meta=dict(item.meta, tags=list(item.meta["tags"]), default_tags=list(item.meta["default_tags"])))
        name_part(tmp, e, cfg)
        out.append({"page": e["page"], "odd": bool(e.get("out_of_order")), "author_en": e["author_en"], "author_jp": e["author_jp"], "title": e["title"],
                    "en": tmp.meta["suggest_en"], "jp": tmp.meta["suggest_jp"], "tags": tmp.meta["suggest_tags"]})
    return out


EHTAG_DB = CACHE_DIR / "ehtag_db.json"
EHTAG_URL = "https://github.com/EhTagTranslation/Database/releases/latest/download/db.text.json"
_artist_names: dict | None = None


def artist_names() -> dict[str, str]:
    """artist 标签 → 参考名（EhTagTranslation 标签库，多为日文原名，纯汉字的有时是简体）。本地缓存，7 天更新一次。"""
    global _artist_names
    if _artist_names is not None:
        return _artist_names
    try:
        stale = not EHTAG_DB.exists() or time.time() - EHTAG_DB.stat().st_mtime > 7 * 86400
        if stale:
            try:
                req = urllib.request.Request(EHTAG_URL, headers={"User-Agent": "Mozilla/5.0"})
                with urllib.request.urlopen(req, timeout=60) as r:
                    data = r.read()
                json.loads(data)   # 确认下载完整再替换
                CACHE_DIR.mkdir(exist_ok=True)
                EHTAG_DB.write_bytes(data)
            except Exception as e:
                log(f"更新标签库失败，继续用旧的：{e}")
        db = json.loads(EHTAG_DB.read_text(encoding="utf-8"))
        art = next(x for x in db["data"] if x["namespace"] == "artist")["data"]
        _artist_names = {k: v["name"] for k, v in art.items() if v.get("name")}
    except Exception:
        _artist_names = {}
    return _artist_names


JP_GUESS_CACHE = CACHE_DIR / "artist_jp_guess.json"
JP_GUESS_PROMPT = """You convert romanized Japanese artist / circle names (E-Hentai artist tags, lowercase) back to how the artist writes the name in Japanese.
Use the real spelling if you know this doujin / manga artist; otherwise give the most plausible spelling (kanji, hiragana or katakana as is typical for such pen names).
Keep symbols such as @ or ＠ when the tag implies them ("piyo at" -> "ピヨ＠").
Reply with a JSON object mapping every input name to its Japanese spelling, nothing else."""


def guess_jp_names(names: list[str], cfg: dict) -> dict[str, str]:
    """标签库里查不到日文名的作者：让 DeepSeek 按罗马音推测一个日文写法（结果缓存，只作参考）。"""
    cache = json.loads(JP_GUESS_CACHE.read_text(encoding="utf-8")) if JP_GUESS_CACHE.exists() else {}
    todo = sorted({n for n in names if n and n not in cache})
    if todo:
        try:
            url, key, model = llm_client(cfg)
            body = json.dumps({"model": model, "temperature": 0, "response_format": {"type": "json_object"},
                               "messages": [{"role": "system", "content": JP_GUESS_PROMPT},
                                            {"role": "user", "content": json.dumps(todo, ensure_ascii=False)}]}).encode()
            req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
            with urllib.request.urlopen(req, timeout=120) as r:
                got = json.loads(re.sub(r"^```(?:json)?|```$", "", json.load(r)["choices"][0]["message"]["content"].strip()).strip())
            for n in todo:
                if isinstance(got.get(n), str) and got[n].strip():
                    cache[n] = got[n].strip()
            CACHE_DIR.mkdir(exist_ok=True)
            JP_GUESS_CACHE.write_text(json.dumps(cache, ensure_ascii=False, indent=1), encoding="utf-8")
        except Exception as e:
            log(f"推测作者日文名失败：{e}")
    return {n: cache[n] for n in names if n in cache}


def match_artist(names: list[str], candidates: list[str]) -> str | None:
    """把目录 / OCR 里的作者名对到原画廊的 artist 标签上：同时比对罗马音和标签库里的日文名，
    容忍一两个字的差别（目录里常有错字）。"""
    import difflib
    norm = lambda s: re.sub(r"[\s\-_.・]", "", s.lower())
    db = artist_names()
    best, score = None, 0.0
    for n in filter(None, names):
        for c in candidates:
            for form in filter(None, (c, db.get(c, ""))):
                r = difflib.SequenceMatcher(None, norm(n), norm(form)).ratio()
                if r > score:
                    best, score = c, r
    return best if score >= 0.8 else None


def clean_title(t: str) -> str:
    """目录里的篇名常带【】『』「」，标题里不需要。"""
    # 只去掉把整个篇名包起来的括号；「学園【第1話】」这种篇名里自带的括号要留着
    pairs = {"【": "】", "『": "』", "「": "」", "[": "]"}
    t = (t or "").strip()
    while len(t) >= 2 and t[0] in pairs and t.endswith(pairs[t[0]]) and t.count(t[0]) == 1:
        t = t[1:-1].strip()
    return re.sub(r"(?<=[ぁ-んァ-ヶ])一", "ー", t)   # 目录里常把长音「ー」打成汉字「一」（が一る → がーる）


def basic_tags(tags: list[str], cfg: dict) -> list[str]:
    """合集节选只加基础标签：这一篇作者的 artist 标签 + 额外标签（语言标签由上传时选的语言自动带上）。
    原画廊的属性标签描述的是整本合集，不一定属于这一篇，所以不复制。"""
    artists = [t for t in tags if t.startswith("artist:")]
    return artists + [t for t in cfg["extra_tags"] if t not in artists]


def finish_meta(item: Item) -> None:
    """套用 upload_meta.json；节选没有可用标题时拦下。最后补上作品列表里「给勾选的加标签」加的标签。"""
    _finish_meta(item)
    try:
        path = item.folder / OVERRIDE_FILE
        extra = json.loads(path.read_text(encoding="utf-8")).get("add_tags", []) if path.exists() else []
    except ValueError:
        extra = []
    item.meta["tags"] = item.meta.get("tags", []) + [t for t in extra if t not in item.meta.get("tags", [])]
    item.meta["added_tags"] = extra


def _finish_meta(item: Item) -> None:
    m = item.meta
    try:
        ov = load_override(item)
    except ValueError as e:
        item.problems.append(f"{OVERRIDE_FILE} 格式错误：{e}")
        return
    m["override"] = ov is not None
    if ov:
        m["en"] = ov.get("en", m["en"]).strip()
        m["jp"] = ov.get("jp", m["jp"]).strip()
        if isinstance(ov.get("tags"), list):
            m["tags"] = [t.strip() for t in ov["tags"] if t.strip()]
        if "◆" in m["en"] or "◆" in m["jp"]:
            item.problems.append("标题里的 ◆篇名◆ / ◆作者◆ 还没换成实际内容")
    elif m["partial"] and m.get("ocr_guessed"):
        # 根据第一页 OCR 文字推测的作者 / 篇名：先填好，但要用户在编辑窗口确认后才能上传
        m["en"], m["jp"], m["tags"] = m["suggest_en"], m["suggest_jp"], list(m["suggest_tags"])
        item.problems.append(("作者和篇名是根据这一篇第一页的文字推测的" if m.get("anthology") else "篇名是根据各段第一页的文字推测的")
                             + "，请点「编辑上传信息」看一眼扉页确认后保存"
                             + ("（篇名没认出来，需要补上）" if "◆" in m["en"] + m["jp"] else ""))
    elif m["partial"] and m["auto_named"] and "◆" not in m["suggest_en"] + m["suggest_jp"]:
        # 从原画廊评论的目录自动命名成功，直接使用（卡片上会提示来源）
        m["en"], m["jp"], m["tags"] = m["suggest_en"], m["suggest_jp"], list(m["suggest_tags"])
    elif m["partial"]:
        item.problems.append(f"单行本/杂志节选（{len(item.images)}/{item.orig_pages} 页）：原画廊评论里没找到对应目录，"
                             "请点卡片上的「编辑上传信息」填写篇名，并删掉不属于这一篇的标签")
    elif m.get("nosrc") and m.get("nosrc_from"):
        item.problems.append(f"原画廊在 e-hentai 上找不到（可能已被删除），分类、标签和发布时间取自 hitomi 上的同一本（{m['nosrc_from']}），"
                             "请点「编辑上传信息」确认后保存")
    elif m.get("nosrc"):
        item.problems.append("原画廊在 e-hentai 上找不到（可能已被删除）：标题和标签是按文件里的标题生成的，"
                             "请点「编辑上传信息」确认标题、补上作品的属性标签后保存")


def plan_tasks(item: Item, cfg: dict) -> list[Item]:
    """识别节选；一本里翻译了多篇时，按目录拆成多个上传任务。"""
    m = item.meta
    item.pages = translated_pages(item)
    # CG 集常有有字/无字等差分，只传一部分是正常的，不算节选
    short = (bool(item.orig_pages) and len(item.images) < item.orig_pages * 0.9
             and m["category_name"] not in ("Artist CG", "Game CG", "Image Set", "Cosplay"))
    # 3 个以上作者，或标题是合集/杂志（[アンソロジー] 或开头没有作者）：多人合集，页数不全就是节选。
    # 1~2 个作者：页数不全一般是没翻译的差分；只有不是从开头译起（单行本里靠后的一篇）才算节选。
    artists = [t for t in m["tags"] if t.startswith("artist:")]
    lead = re.match(r"^\s*(?:\([^)]*\)\s*)?\[([^\]]+)\]", m["jp"] or m["en"])
    anthology = len(artists) >= 3 or not lead or lead.group(1).strip().lower() in ANTHOLOGY_LEADS
    # 单行本里保留了目次页（翻译了的放在前几页，或者没翻译、单独放在原图文件夹里，位置不限）：说明是挑了其中几篇翻译
    toc_page = find_toc_page(item) if short and not anthology and item.pages else None
    m["partial"] = short and (anthology or bool(toc_page) or (bool(item.pages) and item.pages[0] > 3))
    raw_toc = find_raw_toc(item, m["partial"]) if short and item.pages and not toc_page else None
    m["partial"] = m["partial"] or bool(raw_toc)
    m["anthology"] = anthology
    # 用户在编辑窗口点过「按完整作品上传」：不算节选，标题和标签照常自动生成
    try:
        meta_file = item.folder / OVERRIDE_FILE
        if meta_file.exists() and json.loads(meta_file.read_text(encoding="utf-8")).get("whole"):
            m["partial"] = False
    except ValueError:
        pass
    m["default_en"], m["default_jp"], m["default_tags"] = m["en"], m["jp"], list(m["tags"])
    m["suggest_en"], m["suggest_jp"], m["suggest_tags"] = m["en"], m["jp"], list(m["tags"])
    m["orig_pages"], m["toc"], m["auto_named"], m["split"] = item.orig_pages, [], [], 0
    # 原画廊评论里写了禁止机翻、已预定翻译等：先拦下，确认后可在编辑窗口里放行
    comments = fetch_comments(item.gid, item.token, offline=m.get("settled", False))
    m["notices"] = find_notices(comments, cfg)
    # 已确认过的评论记在 upload_meta.json 的 confirmed_comments 里；之后新出现的同类评论会重新提示
    try:
        meta_file = item.folder / OVERRIDE_FILE
        confirmed = set(json.loads(meta_file.read_text(encoding="utf-8")).get("confirmed_comments", [])) if meta_file.exists() else set()
    except ValueError:
        confirmed = set()
    for n in m["notices"]:
        n["confirmed"] = n["id"] in confirmed
    pending = [n for n in m["notices"] if not n["confirmed"]]
    m["notices_ignored"] = bool(m["notices"]) and not pending
    if pending:
        n = next((x for x in pending if x["level"] == "block"), pending[0])
        head = "原画廊评论提到" if n["level"] == "block" else "原画廊评论里有「" + n["keyword"] + "」，请看原评论确认"
        item.problems.append((f"{head}「{n['keyword']}」" if n["level"] == "block" else head)
                             + f"（{n['author']} {n['posted']}）：{n['snippet']}"
                             + (f"（另有 {len(pending) - 1} 条）" if len(pending) > 1 else ""))
    if not m["partial"]:
        finish_meta(item)
        return [item]
    toc = parse_toc(comments)
    if not toc and raw_toc:
        # 评论区没有目录：用原图文件夹里单独放的目次页（它没翻译，本来就不在 cbz 里）
        toc = toc_from_text(item, raw_toc[1], raw_toc[0].name, cfg)
        log(f"  {item.gid} 用目次页 {raw_toc[0].name} 整理出 {len(toc)} 条目录")
    if not toc and toc_page:
        # 评论区没有目录，就用翻译了的目次页；目次页本身不上传
        toc = toc_from_text(item, ocr_text(item, [toc_page], 1), f"p{toc_page}", cfg)
        if len(item.pages) == len(item.images):
            i = item.pages.index(toc_page)
            item.pages.pop(i)
            item.images.pop(i)
        log(f"  {item.gid} 用目次页（第 {toc_page} 页）整理出 {len(toc)} 条目录，目次页本身不上传")
    m["toc"] = [e["raw"] for e in toc]
    m["toc_options"] = toc_options(item, toc, cfg)
    hits = [h for h in match_toc(item, toc) if h["title"]]
    if len(hits) < 2:
        name_part(item, hits[0] if hits else None, cfg)
        finish_meta(item)
        return [item]
    if len(item.pages) != len(item.images):
        name_part(item, None, cfg)
        item.problems.append(f"翻译了 {len(hits)} 篇，但 cbz 图片数和翻译页数对不上，无法自动拆分")
        finish_meta(item)
        return [item]
    tasks = []
    for hit in hits:
        # 每一页归到起始页不晚于它的最后一篇；对不上任何一篇的页（比如后记）跟着前一篇走，不会被漏掉
        owner = lambda p: max((h for h in hits if h["page"] <= p), key=lambda h: h["page"], default=hits[0])
        sel = [(img, p) for img, p in zip(item.images, item.pages) if owner(p) is hit]
        meta = dict(m, toc=list(m["toc"]), tags=list(m["tags"]), default_tags=list(m["default_tags"]), split=len(hits))
        part = Item(item.gid, item.token, item.folder, item.cbz, item.result_dir, item.orig_pages, meta=meta,
                    images=[s[0] for s in sel], problems=list(item.problems), key=f"{item.gid}-p{hit['page']}",
                    part=hit, pages=[s[1] for s in sel])
        name_part(part, hit, cfg)
        finish_meta(part)
        tasks.append(part)
    log(f"  {item.gid} 翻译了 {len(hits)} 篇，拆成 {len(hits)} 个上传任务")
    return tasks


def parse_age(text: str) -> timedelta:
    m = re.fullmatch(r"(\d+)\s*([dmy])", text.strip().lower())
    if not m:
        raise argparse.ArgumentTypeError("格式示例：90d、3m、6m、1y")
    return timedelta(days=int(m.group(1)) * {"d": 1, "m": 30, "y": 365}[m.group(2)])


def parse_date(text: str) -> datetime:
    try:
        return datetime.fromisoformat(text)
    except ValueError:
        raise argparse.ArgumentTypeError("日期格式示例：2026-10-01")


def adopt_old_keys(items: list[Item]) -> None:
    """作品的任务键变了（比如当初找不到原画廊、用临时编号上传，后来在备份站找到了原画廊）时，
    按文件夹把旧的上传记录接到新键上，不然已上传的会被当成没传过。拆篇的记录（-p 页码）不动。"""
    state = load_state()
    by_folder: dict[str, list[str]] = {}
    for k, v in state.items():
        if "-p" not in k:
            by_folder.setdefault(v.get("folder", ""), []).append(k)
    changed = False
    for i in items:
        if i.key in state:
            continue
        olds = by_folder.get(str(i.folder), [])
        if len(olds) != 1:
            continue
        st = state.pop(olds[0])
        if not i.nosrc and i.token:
            st["source"] = f"https://exhentai.org/g/{i.gid}/{i.token}/"
        state[i.key] = st
        changed = True
        log(f"  {i.folder.name}：上传记录从旧编号 {olds[0]} 接到 {i.key}")
    if changed:
        save_state(state)


def prepare(paths: list[str], cfg: dict, refilled_after: datetime | None = None,
            source_age: timedelta | None = None) -> list[Item]:
    if not paths and not DEFAULT_SOURCE:
        raise SystemExit("没有指定作品目录：请在命令行给出目录，或在 config.toml 里设置 source_dir")
    items = find_items(paths or [str(DEFAULT_SOURCE)])
    adopt_old_keys(items)
    untranslated = [i for i in items if not i.result_dir.is_dir()]
    if untranslated:
        items = [i for i in items if i.result_dir.is_dir()]
        log(f"跳过 {len(untranslated)} 部还没翻译的作品（没有 manga_translator_work/result）")
    if refilled_after:
        before = len(items)
        items = [i for i in items if datetime.fromtimestamp(i.cbz.stat().st_mtime) >= refilled_after]
        log(f"回填时间筛选：{before} 部中有 {len(items)} 部在 {refilled_after:%Y-%m-%d %H:%M} 之后回填")
    if not items:
        return []
    emit("scan_progress", text=f"正在查询 {len(items)} 部作品的原画廊信息…")
    meta = api_gdata([(i.gid, i.token) for i in items if not i.nosrc])
    for i in items:
        if i.nosrc:
            meta[i.gid] = nosrc_gallery(i, cfg)
    # 原画廊的「英文标题」里有日文时，整体转成罗马音（一次请求，结果缓存）
    clean = {gid: re.sub(r"\s+", " ", html.unescape(g["title"])).strip() for gid, g in meta.items()}
    for gid, t in clean.items():
        # 有的上传者把日文标题填在了英文标题栏、日文标题栏空着：这时把它当日文标题用
        if not (meta[gid].get("title_jpn") or "").strip() and JAPANESE.search(t):
            meta[gid] = dict(meta[gid], title_jpn=t)
        # 原标题开头没有「[社团 (作者)]」：按原画廊的 group / artist 标签补上（日文用标签库里的日文名）
        lead_re = r"^\s*(?:\([^)]*\)\s*)?\["
        if not re.match(lead_re, t):
            tg = meta[gid].get("tags", [])
            groups = [x.split(":", 1)[1] for x in tg if x.startswith("group:")][:1]
            artists = [x.split(":", 1)[1] for x in tg if x.startswith("artist:")][:2]
            if groups or artists:
                cap = lambda s: " ".join(w.capitalize() for w in s.split())
                def lead(names_g, names_a):
                    a = ", ".join(names_a)
                    return f"[{names_g[0]} ({a})] " if names_g and a else f"[{(names_g or [a])[0]}] "
                en_lead = lead([cap(x) for x in groups], [cap(x) for x in artists])
                jp_lead = lead([ehtag_name(x, "group") or cap(x) for x in groups], [artist_names().get(x) or cap(x) for x in artists])
                clean[gid] = en_lead + t
                jt = (meta[gid].get("title_jpn") or "").strip()
                meta[gid] = dict(meta[gid], title=en_lead + t,
                                 title_jpn=(jp_lead + jt) if jt and not re.match(lead_re, jt) else jt)
    roma = romanize([t for t in clean.values() if JAPANESE.search(t)], cfg)
    for gid, t in clean.items():
        if t in roma:
            meta[gid] = dict(meta[gid], title=roma[t])
    for item in items:
        if item.gid in meta:
            build_meta(item, meta[item.gid], cfg)
        else:
            item.problems.append("API 没有返回原画廊信息")
    if source_age:
        cutoff = datetime.now() - source_age
        before = len(items)
        items = [i for i in items if i.meta and i.meta["posted"] <= cutoff]
        log(f"原作发布时间筛选：{before} 部中有 {len(items)} 部在 {cutoff:%Y-%m-%d} 之前发布")
    # 已上传完成（这一本的所有任务都 done）或永久排除的作品，不用再查原画廊评论里的限制说法
    state, excluded = load_state(), load_excluded()
    for i in items:
        keys = [k for k in state if k == str(i.gid) or k.startswith(f"{i.gid}-p")]
        done = bool(keys) and all(state[k].get("status") == "done" for k in keys)
        if i.meta and (done or str(i.gid) in excluded or any(k.startswith(f"{i.gid}-p") for k in excluded)):
            i.meta["settled"] = True
    settled = sum(1 for i in items if i.meta.get("settled"))
    if settled:
        log(f"{settled} 部已上传或已排除，不再读取原画廊评论")
    prefetch_comments([i for i in items if i.meta and not i.meta.get("settled")])
    tasks = []
    for k, item in enumerate(items, 1):
        # 合集第一次扫描要给目录里每一篇生成罗马音标题（请求 DeepSeek），会慢一些，逐部显示进度
        emit("scan_progress", text=f"正在核对回填结果和节选篇目 {k}/{len(items)}…" + (f"（{item.meta.get('jp') or item.meta.get('en', '')}）"[:60] if item.meta else ""))
        check_ready(item, cfg)
        tasks += plan_tasks(item, cfg) if item.meta else [item]
    return tasks


# ---------------------------------------------------------------- 浏览器操作

def open_browser(pw, cfg: dict, headless: bool):
    PROFILE_DIR.mkdir(exist_ok=True)
    browser = {"executable_path": cfg["browser_path"]} if cfg.get("browser_path") else {"channel": "msedge"}
    ctx = pw.chromium.launch_persistent_context(
        str(PROFILE_DIR), headless=headless, viewport={"width": 1280, "height": 900}, **browser)
    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    page.set_default_timeout(60_000)
    page.on("dialog", lambda d: d.accept())
    return ctx, page


def logged_in(page) -> bool:
    page.goto(f"{UPLD}/manage")
    return page.locator('a[href*="managegallery?act=new"]').count() > 0


def existing_titles(page) -> dict[str, str]:
    """上传列表里已有的画廊：标题 → 未发布画廊的 ulgid（已发布的为空字符串）。"""
    page.goto(f"{UPLD}/manage")
    rows = page.evaluate("""() => [...document.querySelectorAll('tr')].map(tr => {
        const a = [...tr.querySelectorAll('a[href*="managegallery?"]')];
        const t = a.map(x => x.textContent.trim()).find(x => x && !['Manage', 'Delete', 'Publish'].includes(x));
        const pub = a.find(x => x.href.includes('act=publish'));
        const m = pub && pub.href.match(/ulgid=(\\d+)/);
        return [t || '', m ? m[1] : ''];
    }).filter(x => x[0])""")
    out = {}
    for t, u in rows:
        if u or t not in out:   # 同名的既有已发布又有未发布时，记未发布那个
            out[t] = u
    return out


def ensure_tos(page) -> None:
    if not page.is_checked("#tos"):
        page.check("#tos")


def gallery_pages(page) -> list[str]:
    """按当前页序返回已上传文件所在格子的文字。"""
    return page.evaluate("""() => [...document.querySelectorAll('input[id^="pagesel_"]')]
        .map(e => [parseInt(e.value), (document.getElementById('cell_' + e.id.slice(8)) || {}).innerText || ''])
        .filter(x => x[0] > 0).sort((a, b) => a[0] - b[0]).map(x => x[1])""")


def uploaded_names(item: Item, cells: list[str]) -> set[str]:
    return {name for _, name, _ in item.images if any(name in c for c in cells)}


def delete_cells(page, ids: list[int], steps) -> None:
    """照网页的操作删格子：点格子上的 x，再点 Apply Changes。"""
    ensure_tos(page)
    page.evaluate("""(ids) => { for (const n of ids) { show_dropbox(n); current_dropbox.onchange = '';
        delete_current = true; hide_dropbox(); } }""", ids)
    pause(steps)
    with page.expect_navigation():
        page.click("#reorder_manual")
    page.wait_for_load_state("domcontentloaded")


def drop_extra_cells(page, item: Item, steps) -> None:
    """未发布画廊里多出来的、本地已经没有的图（比如之前误传的临时裁切图）删掉。
    只在本地每张图都已在网站上找到时才删，免得文件名显示方式不同而误删正文页。"""
    info = page.evaluate("""() => [...document.querySelectorAll('div[id^="cell_"]')]
        .map(c => [parseInt(c.id.slice(5)), c.innerText || ''])""")
    names = [name for _, name, _ in item.images]
    extra = [(i, t) for i, t in info if not any(n in t for n in names)]
    if not extra or len(info) - len(extra) != len(names) or uploaded_names(item, [t for _, t in info]) != set(names):
        return
    log(f"  删除网站上多出来的 {len(extra)} 张图：{', '.join(t.strip().splitlines()[0] if t.strip() else '?' for _, t in extra[:5])}")
    emit("stage", gid=item.key, stage=f"删除多余的 {len(extra)} 张图", percent=100)
    delete_cells(page, [i for i, _ in extra], steps)


def shrink_image(path: Path, limit_bytes: int) -> None:
    """超过网站大小限制的图片重新压缩（只改上传用的临时副本）：先降 JPEG/WebP 质量，不够再缩小尺寸。"""
    from PIL import Image
    with Image.open(path) as im:
        im.load()
        fmt = {".png": "PNG", ".webp": "WEBP"}.get(path.suffix.lower(), "JPEG")
        img = im.convert("RGB") if fmt == "JPEG" and im.mode not in ("RGB", "L") else im.copy()
    scale = 1.0
    for _ in range(12):
        cur = img if scale == 1.0 else img.resize((int(img.width * scale), int(img.height * scale)), Image.LANCZOS)
        for q in ((95, 92, 90, 87, 84) if fmt != "PNG" else (None,)):
            opts = {"optimize": True} if fmt == "PNG" else {"quality": q, "optimize": True}
            cur.save(path, fmt, **opts)
            if path.stat().st_size <= limit_bytes:
                log(f"  {path.name} 超过上传限制，已压缩到 {path.stat().st_size / 1024 / 1024:.1f}MB"
                    + (f"（缩小到 {cur.width}x{cur.height}）" if scale < 1 else f"（质量 {q}）" if q else ""))
                return
        scale *= 0.9
    raise RuntimeError(f"{path.name} 压缩后仍然超过上传限制")


def upload_batch(page, item: Item, batch: list, cfg: dict, done_bytes: int = 0) -> None:
    """上传一批图片；上传过程中读取页面自带的进度显示并发给界面。"""
    work = TMP_DIR / item.key
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    files = []
    with zipfile.ZipFile(item.cbz) as z:
        for src, name, size in batch:
            dst = work / name
            with z.open(src) as s, dst.open("wb") as d:
                shutil.copyfileobj(s, d)
            limit = IMAGE_LIMIT_MB[dst.suffix.lower()] * 1024 * 1024
            if size > limit:
                shrink_image(dst, int(limit * 0.97))
            files.append(str(dst))
    total = sum(s for *_, s in item.images) or 1
    size = sum(s for *_, s in batch)
    try:
        ensure_tos(page)
        page.set_input_files("#uploadfiles", files)
        # 表单提交后页面在等服务器响应，页面里的进度框不一定会更新；直接问网站的 uploadprogress 接口
        # （和页面自己用的是同一个：apiuid / apikey / progresskey 都在上传页里）
        api = page.evaluate("""() => ({url: (typeof base_url !== 'undefined' ? base_url : 'https://upld.e-hentai.org/') + 'api',
            uid: typeof apiuid !== 'undefined' ? apiuid : 0, key: typeof apikey !== 'undefined' ? apikey : '',
            pk: (document.getElementById('progresskey') || {}).value || ''})""")
        cookie = "; ".join(f"{c['name']}={c['value']}" for c in page.context.cookies(api["url"]))
        timeout = cfg["upload_timeout_min"] * 60
        deadline = time.time() + timeout
        shown = warned = False
        navigated = []
        on_nav = lambda f: navigated.append(1) if f == page.main_frame else None
        page.on("framenavigated", on_nav)
        with page.expect_navigation(timeout=timeout * 1000):
            page.click("#uploadbutton", no_wait_after=True)
            page.wait_for_timeout(1500)
            while time.time() < deadline and api["pk"]:
                # 用 urllib 发（会走系统代理；Playwright 自带的请求不走代理，会一直超时），带上浏览器当前的 cookie（含 PHPSESSID）
                try:
                    req = urllib.request.Request(api["url"], data=json.dumps(
                        {"method": "uploadprogress", "apiuid": api["uid"], "apikey": api["key"], "progresskey": api["pk"]}).encode(),
                        headers={"Content-Type": "application/json", "User-Agent": "Mozilla/5.0", "Cookie": cookie})
                    with urllib.request.urlopen(req, timeout=10) as r:
                        a = json.loads(r.read().decode("utf-8", "replace"))
                except Exception as ex:
                    a = {}
                    if not warned:
                        log(f"  读取上传进度失败：{type(ex).__name__}")
                        warned = True
                txt = " ".join(html.unescape(re.sub(r"<[^>]+>", " ", str(a.get("progress", "")))).split())
                if txt and not shown:
                    log(f"  网站进度：{txt}")   # 第一次记下网站返回的格式，方便核对
                    shown = True
                m = re.search(r"(\d+(?:\.\d+)?)\s*%", txt)
                b = re.search(r"([\d,.]+)\s*(KB|MB|GB|B)?\s*(?:/|of)\s*([\d,.]+)\s*(KB|MB|GB|B)?", txt, re.I)
                frac = None
                if m:
                    frac = float(m.group(1)) / 100
                elif b:
                    unit = {"": 1, "B": 1, "KB": 1024, "MB": 1024 ** 2, "GB": 1024 ** 3}
                    got = float(b.group(1).replace(",", "")) * unit[(b.group(2) or "").upper()]
                    of = float(b.group(3).replace(",", "")) * unit[(b.group(4) or b.group(2) or "").upper()]
                    frac = got / of if of else None
                if frac is not None:
                    frac = max(0.0, min(frac, 1.0))
                    emit("progress", gid=item.key, percent=round((done_bytes + frac * size) / total * 100, 1),
                         text=f"本批 {frac * 100:.0f}%")
                if a.get("done") is not None:
                    break
                if navigated:   # 页面已经跳到上传结果页，不用再查了
                    break
                page.wait_for_timeout(1500)
        page.remove_listener("framenavigated", on_nav)
        page.wait_for_load_state("domcontentloaded")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def raw_link(item: Item) -> str:
    """简介里的 raw（未翻译原画廊）链接：一般用 e-hentai 的；只在 exhentai 上能看到的画廊用 exhentai 的。原画廊已删除的返回空。"""
    if item.nosrc:
        return item.nosrc_from   # e-hentai 上已删除：有 hitomi 上的同一本就链到那里，没有就空
    cache = CACHE_DIR / f"comments_{item.gid}.json"
    try:
        data = json.loads(cache.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        data = {}
    host = data.get("host") if isinstance(data, dict) else None
    if not host:
        # 旧缓存没记来源站：看 e-hentai 上能不能打开（不带登录信息，和普通读者看到的一样）
        sure = True
        try:
            page = http_get(f"https://e-hentai.org/g/{item.gid}/{item.token}/", False)
            host = "e-hentai.org" if ('id="gdt"' in page or 'class="c6"' in page) else "exhentai.org"
        except urllib.error.HTTPError as ex:
            host = "exhentai.org" if ex.code == 404 else "e-hentai.org"   # 只在 exhentai 上的画廊，e-hentai 返回 404
            sure = ex.code == 404
            if ex.code == 404:
                try:   # exhentai 也 404：画廊页面已经被删了（接口里还有资料），读者点进去打不开
                    http_get(f"https://exhentai.org/g/{item.gid}/{item.token}/", True)
                except urllib.error.HTTPError as ex2:
                    if ex2.code == 404:
                        host = "gone"
                except Exception:
                    pass
        except Exception:
            host, sure = "e-hentai.org", False   # 网络问题：先用 e-hentai，不记缓存
        if sure and isinstance(data, dict) and data:
            data["host"] = host
            cache.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    if host == "gone":
        # 原画廊页面已被删除：有备份站上的页面就用它，没有就不放 raw 链接
        try:
            links = json.loads(SOURCE_LINKS.read_text(encoding="utf-8")) if SOURCE_LINKS.exists() else {}
        except ValueError:
            links = {}
        return (links.get(item.folder.name) or {}).get("raw", "")
    return f"https://{host}/g/{item.gid}/{item.token}/"


def fill_new_form(page, item: Item) -> None:
    m = item.meta
    page.goto(f"{UPLD}/managegallery?act=new")
    page.fill("#gname_en", m["en"])
    page.fill("#gname_jp", m["jp"])
    page.select_option("#category", m["category"])
    page.select_option("#langtag", LANG_CHINESE)
    page.check("#langtype_1")
    # AI 翻译，不能勾“专业人工翻译”；也不是 AI 生成图片
    page.evaluate("""() => { for (const id of ['langctl', 'aigen']) { const e = document.getElementById(id); if (e) e.checked = false; } }""")
    # 简介模板里的 {raw} 换成未翻译原画廊的链接（模板里删掉这个占位符就不加）
    comment = m["comment"]
    if "{raw}" in comment:
        raw = raw_link(item)
        # 原画廊已删除：没有 raw 链接，整行去掉
        comment = comment.replace("{raw}", raw) if raw else "\n".join(l for l in comment.splitlines() if "{raw}" not in l)
    page.fill("#ulcomment", comment)
    ensure_tos(page)


def find_published(page, en_title: str):
    page.goto(f"{UPLD}/manage")
    return page.evaluate("""(t) => {
        for (const tr of document.querySelectorAll('tr')) {
            const a = [...tr.querySelectorAll('a')];
            if (!a.some(x => x.textContent.trim() === t)) continue;
            for (const x of a) { const m = x.href.match(/\\/g\\/(\\d+)\\/([0-9a-f]{10})/); if (m) return [m[1], m[2]]; }
        }
        return null; }""", en_title)


def add_tags(page, gid: int, token: str, tags: list[str], cfg: dict) -> tuple[list[str], list[str]]:
    # parody:original 由网站在没有 parody/character 标签四小时后自动加，不能手动投；
    # 画廊上已经有的（比如上传时选的语言类型自动带上的 other:rough translation）也不用再投
    tags = [t for t in tags if t != "parody:original"]
    have = set(api_gdata([(gid, token)]).get(gid, {}).get("tags", []))
    todo = [t for t in tags if t not in have]
    if not todo:
        return [], []
    page.goto(f"https://e-hentai.org/g/{gid}/{token}/")
    chunks, cur = [], ""
    for t in todo:
        nxt = f"{cur}, {t}" if cur else t
        if len(nxt) > 190:
            chunks.append(cur)
            nxt = t
        cur = nxt
    if cur:
        chunks.append(cur)
    # 第一次点进标签输入框时，网站会弹出「标签须知」挡住按钮；用户已同意点「Alright Already」（网站会记住一年）
    page.click("#newtagfield")
    popup = page.locator("#tagpopup")
    try:
        popup.wait_for(state="visible", timeout=3000)
        popup.get_by_text("Alright Already").click()
        popup.wait_for(state="detached", timeout=5000)
        log("  已确认网站的标签须知")
    except Exception:
        pass   # 没有弹窗（之前已经确认过）
    # 网站拒绝某个标签时会弹提示框（Could not vote for tag: 原因），记下原因
    reasons = []
    on_dialog = lambda d: reasons.append(d.message)
    page.on("dialog", on_dialog)
    for c in chunks:
        page.fill("#newtagfield", c)
        page.click("#newtagbutton")
        page.wait_for_timeout(3000)
        pause(cfg["delay_between_steps"])
    page.remove_listener("dialog", on_dialog)
    for r in reasons:
        log(f"  网站提示：{r}")
    time.sleep(5)
    have = set(api_gdata([(gid, token)]).get(gid, {}).get("tags", []))
    return [t for t in tags if t not in have], reasons


LAST_COMMENT = Path(__file__).with_name("cache") / "last_comment.txt"
COMMENT_GAP = 65   # 网站规定两条评论之间至少隔 60 秒（You have to wait 60 seconds between each comment），多留 5 秒


def wait_comment_slot() -> None:
    """距离上一条评论（不管是自动发的还是在 EXE 里手动回复的）不足 65 秒就先等够。"""
    try:
        last = float(LAST_COMMENT.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        last = 0
    wait = last + COMMENT_GAP - time.time()
    if wait > 0:
        log(f"  网站要求两条评论至少隔 60 秒，先等 {wait:.0f} 秒")
        time.sleep(wait)


def mark_comment_sent() -> None:
    LAST_COMMENT.parent.mkdir(exist_ok=True)
    LAST_COMMENT.write_text(str(time.time()), encoding="utf-8")


def post_comment(page, url: str, text: str) -> None:
    """在画廊下发表一条评论，并确认它出现在评论区。"""
    head = " ".join(text.split())[:20]
    reason = ""

    def posted() -> bool:
        # 只看别人/自己的普通评论，不算画廊简介（comment_0），免得简介里碰巧有同样的开头
        body = page.evaluate("""() => [...document.querySelectorAll('#cdiv div.c1')]
            .filter(c => !c.querySelector('#comment_0')).map(c => c.innerText).join(' ')""")
        return head in " ".join(body.split())

    for attempt in range(2):
        if attempt:
            log(f"  评论没发出去（{reason or '网站没说原因'}），再试一次")
        page.goto(url, wait_until="domcontentloaded")
        # 上次其实发出去了、只是等页面超时被当成失败的：不再重复发
        if posted():
            log("  评论区里已经有这条经验分享评论，不重复发")
            return
        wait_comment_slot()
        page.goto(url, wait_until="domcontentloaded")
        page.evaluate("() => { if (typeof display_comment_field === 'function') display_comment_field(); }")
        page.fill('textarea[name="commenttext_new"]', text)
        try:
            # 只等页面主体加载完；等整页（广告、缩略图）加载完有时会超过 60 秒
            with page.expect_navigation(wait_until="domcontentloaded", timeout=90_000):
                page.click('form:has(textarea[name="commenttext_new"]) [type="submit"]')
        except Exception as e:
            log(f"  提交评论后页面迟迟没加载完（{str(e).splitlines()[0][:80]}），重新打开画廊确认")
            mark_comment_sent()
            page.goto(url, wait_until="domcontentloaded")
        mark_comment_sent()
        if posted():
            return
        # 网站拒绝时会在页面上写原因（比如发得太快、和之前的评论重复），记下来
        whole = page.inner_text("body") if page.locator("body").count() else ""
        hits = re.findall(r"[^\n.]*(?:comment|wait|too (?:fast|soon|quickly)|flood|duplicate|identical|posted)[^\n.]*", whole, re.I)
        reason = "；".join(dict.fromkeys(h.strip() for h in hits if 8 < len(h.strip()) < 200
                                        and not h.strip().startswith(("Posted on", "Last edited"))))[:300]
        if head in " ".join(whole.split()):
            return   # 评论区结构变了但内容已经在页面上
    raise RuntimeError("评论提交后没有在评论区找到，网站可能拒绝了" + (f"：{reason}" if reason else "（没给原因）") + "；重新上传会再补发")


# ---------------------------------------------------------------- 已发布画廊的更新（替换改过的页）

def local_hashes(item: Item) -> dict[str, str | None]:
    """本地每张图（按上传文件名）的 SHA-1 前 10 位；e-hentai 页面地址里的 /s/xxxxxxxxxx/ 就是原图的这个值。
    超过大小限制、上传时被压缩过的图对不上，记为 None（不参与比对）。"""
    import hashlib
    out = {}
    with zipfile.ZipFile(item.cbz) as z:
        for src, name, size in item.images:
            limit = IMAGE_LIMIT_MB[Path(name).suffix.lower()] * 1024 * 1024
            out[name] = None if size > limit else hashlib.sha1(z.read(src)).hexdigest()[:10]
    return out


def remote_pages(st: dict) -> list[tuple[str, str | None]]:
    """已发布画廊每一页的（文件名, 原图 SHA-1 前 10 位），按页序。文件名读管理页，哈希读公开画廊页。"""
    mg = http_get(f"{UPLD}/managegallery?ulgid={st['ulgid']}", True)
    cells = re.findall(r'id="pagesel_\d+"[^>]*value="(\d+)".*?<div class="n">(.*?)</div>', mg, re.S)
    names = [html.unescape(n).strip() for v, n in sorted(cells, key=lambda c: int(c[0])) if int(v) > 0]
    gid, token = st["gid"], st["token"]
    hashes: dict[int, str] = {}
    for host in ("e-hentai.org", "exhentai.org"):
        for p in range(200):
            try:
                body = http_get(f"https://{host}/g/{gid}/{token}/?p={p}", True)   # 加 nw=always 会让翻页失效
            except Exception:
                break
            found = {int(n): h for h, n in re.findall(rf"/s/([0-9a-f]{{10}})/{gid}-(\d+)", body)}
            if not found or all(k in hashes for k in found):
                break
            hashes.update(found)
            if len(hashes) >= len(names):
                break
        if hashes:
            break
    return [(n, hashes.get(i + 1)) for i, n in enumerate(names)]


def check_update(item: Item, st: dict) -> dict | None:
    """已上传完成的作品，cbz 在上传之后又改过时，和已发布画廊逐页比对原图哈希，找出换过 / 新增 / 少了的页。
    结果记在 st["update_check"]（同一个 cbz 修改时间只比一次）；没有变化返回 None。"""
    if st.get("status") != "done" or not st.get("gid") or not st.get("ulgid"):
        return None
    mtime = int(item.cbz.stat().st_mtime)
    base = st.get("synced_mtime") or (datetime.fromisoformat(st["finished"]).timestamp() if st.get("finished") else 0)
    if mtime <= base + 60:
        return None
    uc = st.get("update_check")
    if not (uc and uc.get("mtime") == mtime):
        remote = remote_pages(st)
        local = local_hashes(item)
        rmap = dict(remote)
        uc = {"mtime": mtime, "checked": datetime.now().isoformat(timespec="seconds"),
              "changed": [n for n, h in local.items() if n in rmap and h and rmap[n] and h != rmap[n]],
              "added": [n for n in local if n not in rmap],
              "removed": [n for n, _ in remote if n not in local],
              "unknown": sum(1 for h in local.values() if h is None)}
        st["update_check"] = uc
        if not (uc["changed"] or uc["added"] or uc["removed"]):
            st["synced_mtime"] = mtime   # cbz 改过但图片没变（比如只是重新打包）
    return uc if (uc["changed"] or uc["added"] or uc["removed"]) else None


def update_pages(item: Item, uc: dict) -> list[int]:
    """改过 / 新增的图在这个作品里是第几页。"""
    names = set(uc["changed"]) | set(uc["added"])
    return [i for i, (_, n, _) in enumerate(item.images, 1) if n in names]


def published_link(page, ulgid: str) -> tuple[str, str] | None:
    """画廊管理页上「Show Public Gallery」指向的 gid / token。"""
    page.goto(f"{UPLD}/managegallery?ulgid={ulgid}")
    # 页面上还有指向旧版本的 Parent Gallery 链接，只认「Show Public Gallery」
    href = page.evaluate("""() => { const a = [...document.querySelectorAll('a')].find(x => x.textContent.includes('Show Public Gallery'));
        return a ? a.href : ''; }""")
    m = GALLERY_RE.search(href or "")
    return (m.group(1), m.group(2)) if m else None


def process_update(page, item: Item, state: dict, cfg: dict) -> None:
    """已发布画廊的更新：Create New Version → 删掉改过的旧页 → 传新图 → 核对页序 → 发布 → 补标签。
    评论在新旧版本之间是共用的（实测新版本里能看到旧版本的全部评论），所以不再发经验分享评论。"""
    st = state[item.key]
    uc = st["update_check"]
    up = st.setdefault("upd", {})
    steps = cfg["delay_between_steps"]
    names = set(uc["changed"]) | set(uc["added"])
    if uc["removed"]:
        raise RuntimeError(f"本地比已发布的少了 {len(uc['removed'])} 页，网站不允许删减有效内容，请手动处理")
    # 1. 新版本（可编辑的副本）
    if not up.get("ulgid"):
        emit("stage", gid=item.key, stage="创建新版本", percent=0, url=st.get("url", ""))
        page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}")
        ensure_tos(page)
        pause(steps)
        with page.expect_navigation():
            page.click("#unlockbutton")
        page.wait_for_load_state("domcontentloaded")
        m = re.search(r"ulgid=(\d+)", page.url) or re.search(r"ulgid=(\d+)", page.get_attribute("#uploadform", "action") or "")
        if not m or m.group(1) == str(st["ulgid"]):
            raise RuntimeError(f"创建新版本后没拿到新的 ulgid，当前页面：{page.url}")
        up.update(ulgid=m.group(1), status="new")
        save_state(state)
        log(f"  已创建新版本 ulgid={up['ulgid']}")
    ulgid = up["ulgid"]
    if up["status"] == "new" and not up.get("titled"):
        # 已发布画廊的标题只能随新版本一起改：编辑窗口里的标题和网站上不一样时，顺带写进新版本
        page.goto(f"{UPLD}/managegallery?ulgid={ulgid}")
        if page.input_value("#gname_en").strip() != item.meta["en"] or page.input_value("#gname_jp").strip() != item.meta["jp"]:
            log(f"  新版本顺带改标题：{item.meta['jp'] or item.meta['en']}")
            page.fill("#gname_en", item.meta["en"])
            page.fill("#gname_jp", item.meta["jp"])
            ensure_tos(page)
            with page.expect_navigation(timeout=60_000):
                page.click("#savebutton")
            page.wait_for_load_state("domcontentloaded")
            st["title"], st["title_jp"] = item.meta["en"], item.meta["jp"]
        up["titled"] = True
        save_state(state)
    if up["status"] == "new":
        # 2. 删掉改过的旧页（照网页的操作：点格子上的 x，再点 Apply Changes）
        if not up.get("deleted"):
            page.goto(f"{UPLD}/managegallery?ulgid={ulgid}")
            ids = page.evaluate("""(names) => [...document.querySelectorAll('div[id^="cell_"]')]
                .filter(c => names.includes(((c.querySelector('div.n') || {}).textContent || '').trim()))
                .map(c => parseInt(c.id.slice(5)))""", sorted(uc["changed"]))
            if ids:
                emit("stage", gid=item.key, stage=f"删除改过的旧页（{len(ids)} 页）", percent=0)
                delete_cells(page, ids, steps)
                log(f"  已删除 {len(ids)} 张旧图")
            up["deleted"] = True
            save_state(state)
        # 3. 传新图
        page.goto(f"{UPLD}/managegallery?ulgid={ulgid}")
        have = uploaded_names(item, gallery_pages(page))
        todo = [img for img in item.images if img[1] in names and img[1] not in have]
        batches = make_batches(todo, cfg) if todo else []
        for n, batch in enumerate(batches, 1):
            pause(steps)
            log(f"  上传新图第 {n}/{len(batches)} 批（{len(batch)} 张）")
            emit("stage", gid=item.key, stage=f"上传改过的页 {n}/{len(batches)} 批（{len(todo)} 张）", percent=0)
            upload_batch(page, item, batch, cfg)
        # 4. 核对页数与页序
        emit("stage", gid=item.key, stage="核对页数与页序", percent=100)
        page.goto(f"{UPLD}/managegallery?ulgid={ulgid}")
        cells = gallery_pages(page)
        missing = [name for _, name, _ in item.images if name not in uploaded_names(item, cells)]
        if missing or len(cells) != len(item.images):
            raise RuntimeError(f"页数不一致：新版本 {len(cells)} 页，本地 {len(item.images)} 张，缺少 {missing[:5]}")
        if any(name not in cells[i] for i, (_, name, _) in enumerate(item.images)):
            log("  页序和文件名顺序不一致，执行 Natural Sort")
            ensure_tos(page)
            page.select_option("#reorder_select", "natural")
            with page.expect_navigation():
                page.click("#reorder_auto")
            cells = gallery_pages(page)
            if any(name not in cells[i] for i, (_, name, _) in enumerate(item.images)):
                raise RuntimeError("自动排序后页序仍不正确，请手动检查")
        up["status"] = "uploaded"
        save_state(state)
    # 5. 发布新版本
    if up["status"] == "uploaded":
        emit("stage", gid=item.key, stage="发布新版本", percent=100)
        pause(steps)
        page.goto(f"{UPLD}/managegallery?ulgid={ulgid}&act=publish&from=gallery")
        page.wait_for_load_state("domcontentloaded")
        confirm = page.locator('input[name="qa_publish"]')
        if confirm.count():
            pause(steps)
            with page.expect_navigation():
                confirm.first.click()
            page.wait_for_load_state("domcontentloaded")
        pause(steps)
        found = published_link(page, ulgid)
        if not found or found[0] == str(st["gid"]):
            raise RuntimeError("发布新版本后没找到新画廊的链接，请到网站上确认")
        st.setdefault("versions", []).append({"gid": st["gid"], "token": st["token"], "ulgid": st["ulgid"]})
        st.update(gid=found[0], token=found[1], url=f"https://e-hentai.org/g/{found[0]}/{found[1]}/", ulgid=ulgid)
        up["status"] = "published"
        save_state(state)
        log(f"  新版本已发布：{st['url']}")
    # 6. 标签（新版本一般会带上旧版本的标签，缺的照原规则补）
    if up["status"] == "published":
        emit("stage", gid=item.key, stage="核对标签", percent=100, url=st["url"])
        pause(steps)
        missing, reasons = add_tags(page, int(st["gid"]), st["token"], item.meta["tags"], cfg)
        st["missing_tags"], st["tag_errors"] = missing, reasons
        st["synced_mtime"] = uc["mtime"]
        st["updated"] = datetime.now().isoformat(timespec="seconds")
        st["updated_pages"] = update_pages(item, uc)
        st.pop("update_check", None)
        st.pop("upd", None)
        save_state(state)
        log("  更新完成" + (f"，未生效标签：{', '.join(missing)}" if missing else ""))


def process(page, item: Item, state: dict, cfg: dict, post_share: bool = False) -> None:
    key = item.key
    st = state.setdefault(key, {"source": "" if item.nosrc else f"https://exhentai.org/g/{item.gid}/{item.token}/", "folder": str(item.folder)})
    st["title"] = item.meta["en"]
    st["title_jp"] = item.meta["jp"]
    steps = cfg["delay_between_steps"]

    def done_bytes(names: set[str]) -> int:
        return sum(s for _, n, s in item.images if n in names)

    # 1. 新建画廊或续传
    if st.get("status") in (None, "new"):
        batches = make_batches(item.images, cfg)
        if "ulgid" not in st:
            emit("stage", gid=item.key, stage="创建画廊并上传第 1/%d 批" % len(batches), percent=0)
            fill_new_form(page, item)
            log(f"  上传第 1/{len(batches)} 批（{len(batches[0])} 张）")
            upload_batch(page, item, batches[0], cfg)
            m = re.search(r"ulgid=(\d+)", page.url) or re.search(r"ulgid=(\d+)", page.get_attribute("#uploadform", "action") or "")
            if not m:
                raise RuntimeError(f"上传后没拿到 ulgid，当前页面：{page.url}")
            st.update(ulgid=m.group(1), status="new")
            save_state(state)
            log(f"  已创建画廊 ulgid={st['ulgid']}")
        page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}")
        done = uploaded_names(item, gallery_pages(page))
        rest_batches = make_batches([img for img in item.images if img[1] not in done], cfg)
        for n, batch in enumerate(rest_batches, 1):
            pause(steps)
            log(f"  续传第 {n} 批（{len(batch)} 张，已有 {len(done)} 张）")
            emit("stage", gid=item.key, stage=f"上传剩余图片 {n}/{len(rest_batches)} 批（已有 {len(done)}/{len(item.images)} 张）",
                 percent=round(done_bytes(done) / max(1, done_bytes({i[1] for i in item.images})) * 100, 1))
            upload_batch(page, item, batch, cfg, done_bytes(done))
            done = uploaded_names(item, gallery_pages(page))

        # 2. 核对页数与顺序
        emit("stage", gid=item.key, stage="核对页数与页序", percent=100)
        page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}")
        drop_extra_cells(page, item, steps)
        page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}")
        cells = gallery_pages(page)
        missing = [name for _, name, _ in item.images if name not in uploaded_names(item, cells)]
        if missing or len(cells) != len(item.images):
            raise RuntimeError(f"页数不一致：网站 {len(cells)} 页，本地 {len(item.images)} 张，缺少 {missing[:5]}")
        if any(name not in cells[i] for i, (_, name, _) in enumerate(item.images)):
            log("  页序和文件名顺序不一致，执行 Natural Sort")
            ensure_tos(page)
            page.select_option("#reorder_select", "natural")
            with page.expect_navigation():
                page.click("#reorder_auto")
            cells = gallery_pages(page)
            if any(name not in cells[i] for i, (_, name, _) in enumerate(item.images)):
                raise RuntimeError("自动排序后页序仍不正确，请手动检查")
        st["status"] = "uploaded"
        save_state(state)
        log(f"  上传完成，共 {len(cells)} 页")

    # 3. 发布
    if st["status"] == "uploaded":
        emit("stage", gid=item.key, stage="发布画廊", percent=100)
        pause(steps)
        page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}&act=publish&from=gallery")
        page.wait_for_load_state("domcontentloaded")
        # 网站会先显示确认页（发布后锁定、不能再改文件），要再点一次 Publish
        confirm = page.locator('input[name="qa_publish"]')
        if confirm.count():
            pause(steps)
            with page.expect_navigation():
                confirm.first.click()
            page.wait_for_load_state("domcontentloaded")
        pause(steps)
        found = find_published(page, item.meta["en"])
        if not found:
            snap = LOG_DIR / f"publish_{item.key}.html"
            page.goto(f"{UPLD}/managegallery?ulgid={st['ulgid']}")
            snap.write_text(page.content(), encoding="utf-8")
            raise RuntimeError(f"发布后没在已发布列表里找到画廊，页面快照：{snap}")
        st.update(gid=found[0], token=found[1], url=f"https://e-hentai.org/g/{found[0]}/{found[1]}/", status="published")
        save_state(state)
        log(f"  已发布：{st['url']}")

    # 4. 标签
    if st["status"] == "published":
        emit("stage", gid=item.key, stage=f"添加标签（{len(item.meta['tags'])} 个）", percent=100, url=st["url"])
        pause(steps)
        missing, reasons = add_tags(page, int(st["gid"]), st["token"], item.meta["tags"], cfg)
        st["missing_tags"] = missing
        st["tag_errors"] = reasons
        st["status"] = "tagged"
        save_state(state)
        log(f"  标签完成" + (f"，未生效：{', '.join(missing)}" if missing else ""))

    # 5. 经验分享评论
    if st["status"] == "tagged":
        text = cfg.get("share_comment", "").strip()
        if post_share and text and not st.get("comment_posted"):
            emit("stage", gid=item.key, stage="发表经验分享评论", percent=100, url=st["url"])
            pause(steps)
            post_comment(page, st["url"], text)
            st["comment_posted"] = True
            log("  已发表经验分享评论")
        st["status"] = "done"
        st["finished"] = datetime.now().isoformat(timespec="seconds")
        save_state(state)


# ---------------------------------------------------------------- 命令

def browser_exe(cfg: dict) -> str:
    if cfg.get("browser_path"):
        return cfg["browser_path"]
    for p in (r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"):
        if Path(p).exists():
            return p
    raise RuntimeError("找不到浏览器，请在设置页填写浏览器程序路径")


def profile_in_use() -> bool:
    """独立用户资料是否还被浏览器占用（浏览器运行时会锁住 lockfile）。"""
    lock = PROFILE_DIR / "lockfile"
    if not lock.exists():
        return False
    try:
        lock.unlink()
        return False
    except OSError:
        return True


def add_ex_cookie() -> None:
    """有 exhentai 权限的账号访问一次 exhentai 会拿到 igneous cookie，补进本地 cookie 文件。"""
    import http.cookiejar
    cookies = json.loads(COOKIE_FILE.read_text(encoding="utf-8"))
    jar = http.cookiejar.CookieJar()
    for c in cookies:
        jar.set_cookie(http.cookiejar.Cookie(0, c["name"], c["value"], None, False, ".exhentai.org", True, True, "/", True,
                                             False, None, False, None, None, {}))
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    opener.addheaders = [("User-Agent", "Mozilla/5.0")]
    try:
        opener.open("https://exhentai.org/", timeout=30).read()
    except Exception:
        pass
    got = [{"name": c.name, "value": c.value} for c in jar if c.name == "igneous" and c.value and c.value != "mystery"]
    if got:
        cookies = [c for c in cookies if c["name"] != "igneous"] + got
        COOKIE_FILE.write_text(json.dumps(cookies, ensure_ascii=False), encoding="utf-8")


def cmd_login(cfg: dict) -> None:
    """用普通浏览器窗口（不受自动化控制）打开登录页，由用户自己完成网站验证和登录；
    用户关掉窗口后，再从独立用户资料里读出登录 cookie。"""
    import subprocess
    from playwright.sync_api import sync_playwright
    PROFILE_DIR.mkdir(exist_ok=True)
    if profile_in_use():
        log("登录用的浏览器窗口已经开着，请在那个窗口里登录，完成后关掉它")
    else:
        subprocess.Popen([browser_exe(cfg), f"--user-data-dir={PROFILE_DIR}", "--no-first-run",
                          "--no-default-browser-check", "--new-window", LOGIN_URL])
        log("已打开登录页：请在弹出的浏览器窗口里完成验证并登录，登录成功后关掉这个浏览器窗口（最多等 15 分钟）")
    emit("login_waiting")
    time.sleep(5)
    deadline = time.time() + 900
    while profile_in_use() and time.time() < deadline:
        time.sleep(2)
    if profile_in_use():
        log("等待超时：浏览器窗口还开着")
        emit("login", ok=False, reason="等待超时，请登录后关掉那个浏览器窗口再试")
        return
    with sync_playwright() as pw:
        ctx, _ = open_browser(pw, cfg, headless=True)
        names = {c["name"] for c in ctx.cookies(["https://e-hentai.org"])}
        ok = {"ipb_member_id", "ipb_pass_hash"} <= names
        if ok:
            save_cookies(ctx)
        ctx.close()
    if ok:
        add_ex_cookie()
    log("登录成功" if ok else "没有在浏览器里找到登录信息，请确认已登录成功后再关窗口")
    emit("login", ok=ok, reason="" if ok else "没有找到登录信息，请确认登录成功后再关掉浏览器窗口")


def first_page_image(it: Item) -> str:
    """这一篇第一页的日文原图（翻译工程目录里的原始图片），给编辑窗口看扉页用。"""
    if not it.meta.get("partial") or not it.pages:
        return ""
    src = it.result_dir.parent.parent
    for ext in (".jpg", ".jpeg", ".png", ".webp"):
        f = src / f"{it.pages[0]:08d}{ext}"
        if f.exists():
            return str(f)
    return ""


def detect_updates(items: list[Item]) -> None:
    """已上传完成、之后 cbz 又改过的作品：找出改过的页，记到 item.meta["update"]。
    这类作品已经发布过，原画廊评论里的限制说法不再拦它。"""
    state = load_state()
    dirty = False
    emit("scan_progress", text="正在检查已上传的作品有没有改过页…")
    for it in items:
        st = state.get(it.key)
        if not st or st.get("status") != "done" or not it.images:
            continue
        before = json.dumps(st, sort_keys=True)
        try:
            uc = check_update(it, st)
        except Exception as e:
            log(f"检查 {it.key} 的更新失败：{e}")
            continue
        dirty = dirty or json.dumps(st, sort_keys=True) != before
        if uc:
            it.meta["update"] = {"pages": update_pages(it, uc), "changed": len(uc["changed"]), "added": len(uc["added"]),
                                 "removed": len(uc["removed"]), "unknown": uc.get("unknown", 0)}
            it.problems = [p for p in it.problems if not p.startswith("原画廊评论")]
            if uc["removed"]:
                it.problems.append(f"本地比已发布的少了 {len(uc['removed'])} 页，网站不允许删减有效内容，请到网站上手动处理")
            log(f"{it.key} 有更新：第 {'、'.join(map(str, it.meta['update']['pages'][:20]))} 页")
    if dirty:
        save_state(state)


def scan_json(items: list[Item], state: dict, cfg: dict) -> None:
    out = []
    excluded = load_excluded()
    # 节选作品的候选作者里，标签库没有日文名的，一次性让模型推测
    missing = [t.split(":", 1)[1] for it in items if it.meta.get("partial") for t in it.meta.get("default_tags", [])
               if t.startswith("artist:") and t.split(":", 1)[1] not in artist_names()]
    if missing:
        emit("scan_progress", text="正在整理作者的日文名…")
    guessed = guess_jp_names(missing, cfg) if missing else {}
    for it in items:
        st = state.get(it.key, {})
        out.append({
            "gid": it.gid, "key": it.key, "split": it.meta.get("split", 0),
            "excluded": it.key in excluded or str(it.gid) in excluded,
            "part_page": it.part["page"] if it.part else 0, "folder": str(it.folder), "cbz": str(it.cbz), "work_dir": str(it.result_dir.parent.parent),
            "thumb_entry": it.images[0][0] if it.images else "",
            "first_page": first_page_image(it), "ocr_guessed": it.meta.get("ocr_guessed", False),
            "candidates": [t.split(":", 1)[1] for t in it.meta.get("default_tags", []) if t.startswith("artist:")],
            "candidate_names": {c: artist_names().get(c) or ("推测：" + guessed[c] if c in guessed else "") for c in
                                (t.split(":", 1)[1] for t in it.meta.get("default_tags", []) if t.startswith("artist:"))}
                               if it.meta.get("partial") else {},
            "source_url": it.nosrc_from if it.nosrc else f"https://exhentai.org/g/{it.gid}/{it.token}/", "nosrc": bool(it.nosrc),
            "refilled": datetime.fromtimestamp(it.cbz.stat().st_mtime).isoformat(timespec="minutes"),
            "posted": it.meta["posted"].isoformat(timespec="minutes") if it.meta and it.meta["posted"].year > 1971 else None,
            "pages": len(it.images), "mb": round(sum(s for *_, s in it.images) / 1024 / 1024, 1),
            "batches": len(make_batches(it.images, cfg)),
            "en": it.meta.get("en", ""), "jp": it.meta.get("jp", ""), "category": it.meta.get("category_name", ""),
            "tags": len(it.meta.get("tags", [])), "problems": it.problems,
            "partial": it.meta.get("partial", False), "orig_pages": it.orig_pages, "override": it.meta.get("override", False),
            "tag_list": it.meta.get("tags", []), "suggest_en": it.meta.get("suggest_en", ""),
            "suggest_jp": it.meta.get("suggest_jp", ""), "suggest_tags": it.meta.get("suggest_tags", []),
            "toc": it.meta.get("toc", []), "toc_options": it.meta.get("toc_options", []), "auto_named": it.meta.get("auto_named", []),
            "default_en": it.meta.get("default_en", ""), "default_jp": it.meta.get("default_jp", ""),
            "default_tags": it.meta.get("default_tags", []),
            "notices": it.meta.get("notices", []), "notices_ignored": it.meta.get("notices_ignored", False),
            "translated_pages": it.pages if it.meta.get("partial") else [],
            "artists": [t.split(":", 1)[1] for t in it.meta.get("tags", []) if t.startswith("artist:")],
            "groups": [t.split(":", 1)[1] for t in it.meta.get("tags", []) if t.startswith("group:")],
            "status": st.get("status", ""), "url": st.get("url", ""),
            "update": it.meta.get("update"),
        })
    emit("scan", items=out)


def print_scan(items: list[Item], state: dict, cfg: dict) -> None:
    for it in items:
        st = state.get(it.key, {})
        mb = sum(s for *_, s in it.images) / 1024 / 1024
        flag = "已完成" if st.get("status") == "done" else ("有问题" if it.problems else "可上传")
        print(f"\n[{flag}] {it.gid}  {len(it.images)} 张 / {mb:.0f}MB / {len(make_batches(it.images, cfg))} 批")
        refilled = datetime.fromtimestamp(it.cbz.stat().st_mtime)
        print(f"  回填：{refilled:%Y-%m-%d %H:%M}" + (f"  原作发布：{it.meta['posted']:%Y-%m-%d}" if it.meta else ""))
        if it.meta:
            print(f"  英文：{it.meta['en']}\n  日文：{it.meta['jp']}\n  分类：{it.meta['category_name']}  标签 {len(it.meta['tags'])} 个")
        if it.meta.get("update"):
            print(f"  有更新：第 {'、'.join(map(str, it.meta['update']['pages']))} 页")
        for p in it.problems:
            print(f"  ⚠ {p}")


def cmd_upload(items: list[Item], cfg: dict, limit: int | None, force: bool) -> None:
    from playwright.sync_api import sync_playwright
    state = load_state()
    excluded = load_excluded()
    for i in [i for i in items if i.key in excluded or str(i.gid) in excluded]:
        log(f"跳过 {i.key}：已永久排除")
        emit("item_failed", gid=i.key, error="已永久排除，不上传")
    items = [i for i in items if not (i.key in excluded or str(i.gid) in excluded)]
    def is_update(i: Item) -> bool:   # 已发布、但本地改过页的作品，上传它的新版本
        return state.get(i.key, {}).get("status") == "done" and bool(i.meta.get("update")) and bool(state[i.key].get("update_check"))

    todo = [i for i in items if state.get(i.key, {}).get("status") != "done" or is_update(i)]
    blocked = [i for i in todo if i.problems and not (force and i.meta)]
    todo = [i for i in todo if i not in blocked]
    for i in blocked:
        log(f"跳过 {i.gid}：{'；'.join(i.problems)}")
        emit("item_failed", gid=i.key, error="；".join(i.problems))
    for i in items:
        if state.get(i.key, {}).get("status") == "done" and not is_update(i):
            emit("item_done", gid=i.key, url=state[i.key].get("url", ""), skipped=True)
    if limit:
        todo = todo[:limit]
    if not todo:
        log("没有需要上传的作品")
        emit("finished", ok=0, failed=len(blocked))
        return
    if not cfg.get("comment", "").strip():
        log("提示：config.toml 的 comment 为空，本次上传不填写简介")
    ok, failed = [], [i.gid for i in blocked]
    with sync_playwright() as pw:
        ctx, page = open_browser(pw, cfg, cfg["headless"])
        if not logged_in(page):
            ctx.close()
            emit("fatal", error="未登录 e-hentai，请先在设置里登录")
            sys.exit("未登录，请先运行：python eh_upload.py login")
        save_cookies(ctx)
        titles = existing_titles(page)
        mode = cfg.get("share_comment_mode", "first")
        shared = False
        for n, item in enumerate(todo, 1):
            log(f"({n}/{len(todo)}) {item.gid} {item.meta['en']}")
            emit("item_start", gid=item.key, index=n, total=len(todo), title=item.meta["en"])
            if titles.get(item.meta["en"]) and item.key not in state:
                # 上次上传中途被中断：网站上已经建好了画廊但还没发布，本地记录没来得及保存，接着往这个画廊里续传
                state[item.key] = {"source": f"https://exhentai.org/g/{item.gid}/{item.token}/", "folder": str(item.folder),
                                   "ulgid": titles[item.meta["en"]], "status": "new"}
                save_state(state)
                log(f"  上传列表里有上次没传完的同名画廊（ulgid={titles[item.meta['en']]}，未发布），接着续传")
            if item.meta["en"] in titles and item.key not in state:
                log("  你的上传列表里已有同名画廊，跳过")
                emit("item_failed", gid=item.key, error="上传列表里已有同名画廊，已跳过")
                failed.append(item.gid)
                continue
            try:
                if is_update(item):
                    log(f"  更新已发布的画廊：第 {'、'.join(map(str, item.meta['update']['pages'][:30]))} 页改过")
                    process_update(page, item, state, cfg)
                else:
                    process(page, item, state, cfg, post_share=mode == "all" or (mode == "first" and not shared))
                    shared = shared or bool(state[item.key].get("comment_posted"))
                ok.append(item.gid)
                st = state[item.key]
                emit("item_done", gid=item.key, url=st.get("url", ""), missing_tags=st.get("missing_tags", []))
            except Exception as e:  # 单部失败不影响后续
                log(f"  失败：{e}")
                emit("item_failed", gid=item.key, error=str(e).splitlines()[0][:300])
                failed.append(item.gid)
            if n < len(todo):
                wait = random.uniform(*cfg["delay_between_galleries"])
                log(f"  等待 {wait:.0f} 秒后上传下一部")
                emit("waiting", seconds=round(wait), next_gid=todo[n].key)
                time.sleep(wait)
        ctx.close()
    log(f"结束：成功 {len(ok)}，失败 {len(failed)}" + (f"（{', '.join(map(str, failed))}，重新运行即可续传）" if failed else ""))
    emit("finished", ok=len(ok), failed=len(failed))


# ---------------------------------------------------------------- 已上传管理

def need_login() -> None:
    if not COOKIE_FILE.exists():
        emit("fatal", error="还没有登录信息，请先在设置页点「打开登录窗口」登录 e-hentai")
        sys.exit(1)


def parse_comments(page: str) -> list[dict]:
    """画廊页里的全部评论：作者、时间（UTC）、得分、正文；comment_0 是上传者简介。"""
    out = []
    for chunk in page.split('<div class="c1">')[1:]:
        body = re.search(r'<div class="c6" id="comment_(\d+)"[^>]*>(.*?)</div>', chunk, re.S)
        head = re.search(r'<div class="c3">(.*?)</div>', chunk, re.S)
        if not body or not head:
            continue
        head_text = html.unescape(re.sub(r"<[^>]+>", "", head.group(1))).replace("\xa0", " ")
        posted = re.search(r"Posted on (.+?) by", head_text)
        author = re.search(r"by:\s*(.+)", head_text)
        score = re.search(r'id="comment_score_\d+"[^>]*>([^<]*)<', chunk)
        text = html.unescape(re.sub(r"<[^>]+>", "", re.sub(r"<br\s*/?>", "\n", body.group(2)))).strip()
        when = None
        if posted:
            try:
                when = datetime.strptime(posted.group(1).strip(), "%d %B %Y, %H:%M").replace(tzinfo=timezone.utc).astimezone()
            except ValueError:
                pass
        out.append({"id": body.group(1), "uploader": body.group(1) == "0", "author": author.group(1).strip() if author else "",
                    "posted": when.isoformat(timespec="minutes") if when else "", "score": score.group(1).strip() if score else "",
                    "text": text})
    return out


MY_COMMENTS = CACHE_DIR / "my_comments.json"


MY_GALLERIES = CACHE_DIR / "my_galleries.json"
THUMB_DIR = CACHE_DIR / "thumbs"


def cache_thumb(gid: int, url: str, download: bool) -> str:
    """画廊封面缩略图存成本地 JPG（e-hentai 的缩略图是 WebP，EXE 显示不了），返回本地路径。"""
    path = THUMB_DIR / f"{gid}.jpg"
    if path.exists() or not download or not url:
        return str(path) if path.exists() else ""
    try:
        import io
        from PIL import Image
        THUMB_DIR.mkdir(parents=True, exist_ok=True)
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=30) as r:
            Image.open(io.BytesIO(r.read())).convert("RGB").save(path, "JPEG", quality=90)
        time.sleep(0.2)
        return str(path)
    except Exception as e:
        log(f"下载 {gid} 的封面失败：{e}")
        return ""


def comments_due(it: dict, c: dict) -> bool:
    """按热度决定这个画廊的评论要不要重新读：刚发布或最近有人评论的查得勤，冷门的一天最多查一次。"""
    if not c.get("fetched"):
        return True
    now = datetime.now()
    since = now - datetime.fromisoformat(c["fetched"])

    def within(iso: str, days: int) -> bool:
        if not iso:
            return False
        t = datetime.fromisoformat(iso)
        t = t.astimezone().replace(tzinfo=None) if t.tzinfo else t
        return now - t < timedelta(days=days)

    last = max((x.get("posted", "") for x in c.get("items", [])), default="")
    if within(it.get("posted", ""), 3) or within(last, 3):
        return since > timedelta(hours=1)
    if within(last, 30):
        return since > timedelta(hours=6)
    return since > timedelta(hours=24)


def cmd_mine(n: int, refresh: str) -> None:
    """「我的上传」里已发布的画廊（标题、封面、发布时间）和最新评论。

    标题、封面、页数这些不会变的数据只在画廊第一次出现时读取，之后一直用本地缓存。
    refresh：cache 只显示缓存、不联网；smart 先显示缓存，再读画廊列表找新画廊，评论按热度只查「到时间」的；
    all 评论全部重读。
    """
    cache = json.loads(MY_COMMENTS.read_text(encoding="utf-8")) if MY_COMMENTS.exists() else {}
    saved = json.loads(MY_GALLERIES.read_text(encoding="utf-8")) if MY_GALLERIES.exists() else {"fetched": "", "items": []}

    def with_comments(it: dict) -> dict:
        c = cache.get(str(it["gid"]), {})
        return dict(it, comments=c.get("items", []), total=c.get("total", 0), fetched=c.get("fetched", ""),
                    thumb_file=cache_thumb(it["gid"], it.get("thumb", ""), False))

    if saved["items"]:
        emit("mine", items=[with_comments(i) for i in saved["items"]], list_fetched=saved["fetched"], cached=True)
    if refresh == "cache" and saved["items"]:
        return
    need_login()
    page = http_get(f"{UPLD}/manage?ss=d&sd=d", True)
    if "managegallery" not in page:
        emit("fatal", error="读取「我的上传」失败，登录可能已过期，请在设置页重新登录")
        sys.exit(1)
    pairs, seen = [], set()
    for row in re.findall(r"<tr[^>]*>(.*?)</tr>", page, re.S):
        g = re.search(r"e-hentai\.org/g/(\d+)/([0-9a-f]{10})", row)
        if g and g.group(1) not in seen:
            seen.add(g.group(1))
            pairs.append((int(g.group(1)), g.group(2)))
    known = {i["gid"]: i for i in saved["items"]}
    new_pairs = [p for p in pairs if p[0] not in known]
    meta = api_gdata(new_pairs) if new_pairs else {}
    mine = {str(st.get("gid")) for st in load_state().values() if st.get("gid")}
    items = []
    for gid, token in pairs:
        if gid in known:
            it = dict(known[gid], by_tool=str(gid) in mine)
            cache_thumb(gid, it.get("thumb", ""), True)   # 以前封面下载失败的，这次补上（已有的直接跳过）
        else:
            g = meta.get(gid, {})
            it = {
                "gid": gid, "token": token, "url": f"https://e-hentai.org/g/{gid}/{token}/",
                "title": html.unescape(g.get("title", "")), "title_jpn": html.unescape(g.get("title_jpn", "")),
                "thumb": g.get("thumb", ""), "category": g.get("category", ""), "pages": int(g.get("filecount", 0) or 0),
                "rating": g.get("rating", ""), "by_tool": str(gid) in mine,
                "posted": datetime.fromtimestamp(int(g.get("posted", 0) or 0)).isoformat(timespec="minutes"),
            }
            cache_thumb(gid, it["thumb"], True)
        items.append(it)
    items.sort(key=lambda x: x["posted"], reverse=True)
    now = datetime.now().isoformat(timespec="seconds")
    CACHE_DIR.mkdir(exist_ok=True)
    MY_GALLERIES.write_text(json.dumps({"fetched": now, "items": items}, ensure_ascii=False), encoding="utf-8")
    if new_pairs or len(items) != len(saved["items"]) or not saved["items"]:
        emit("mine", items=[with_comments(i) for i in items], list_fetched=now, cached=False)
    if new_pairs:
        log(f"发现 {len(new_pairs)} 个新画廊")
    todo = [i for i in items if refresh == "all" or comments_due(i, cache.get(str(i["gid"]), {}))]
    log(f"这次检查 {len(todo)} 个画廊的评论（其余的还没到检查时间）")
    for k, it in enumerate(todo, 1):
        emit("comments_progress", done=k - 1, total=len(todo), gid=it["gid"])
        try:
            comments = sorted(gallery_comments(it["gid"], it["token"]), key=lambda c: c["posted"], reverse=True)
        except Exception as e:
            log(f"读取 {it['gid']} 的评论失败：{e}")
            continue
        now = datetime.now().isoformat(timespec="seconds")
        cache[str(it["gid"])] = {"fetched": now, "total": len(comments), "items": comments}
        MY_COMMENTS.write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
        emit("gallery_comments", gid=it["gid"], total=len(comments), items=comments, fetched=now)
        if k < len(todo):
            time.sleep(random.uniform(1.5, 3))
    emit("comments_progress", done=len(todo), total=len(todo), gid=0, checked=len(todo), skipped=len(items) - len(todo))


def gallery_comments(gid: int, token: str) -> list[dict]:
    """画廊的评论（不含上传者简介）；e-hentai 上看不到的画廊（只在 exhentai 显示），改用登录信息读 exhentai。"""
    page = ""
    for host in ("e-hentai.org", "exhentai.org"):
        try:
            page = http_get(f"https://{host}/g/{gid}/{token}/?hc=1", True)
        except Exception:
            page = ""
        if 'id="cdiv"' in page or 'class="c1"' in page:
            break
    return [c for c in parse_comments(page) if not c["uploader"]]


def cmd_comments(gid: int, token: str, n: int) -> None:
    need_login()
    comments = sorted(gallery_comments(gid, token), key=lambda c: c["posted"], reverse=True)
    emit("comments", gid=gid, total=len(comments), items=comments)


def cmd_post_comment(gid: int, token: str, text_file: str, n: int) -> None:
    """在画廊下发表评论，发完重新读取评论区确认。"""
    need_login()
    text = Path(text_file).read_text(encoding="utf-8").strip()
    if len(text) < 10:
        emit("fatal", error="评论太短，e-hentai 要求至少 10 个字符")
        sys.exit(1)
    url = f"https://e-hentai.org/g/{gid}/{token}/"
    head = " ".join(text.split())[:20]
    # 上次超时被当成失败、其实已经发出去的：再点发送时不重复发（网站会把连发的两条合并成一条，内容出现两遍）
    before = gallery_comments(gid, token)
    whole, recent = " ".join(text.split()), (datetime.now() - timedelta(minutes=30)).isoformat(timespec="minutes")
    if any(whole in " ".join(c["text"].split()) and c["posted"][:16] >= recent for c in before):
        log(f"{gid} 的评论区里已经有这条评论，不重复发")
        comments = sorted(before, key=lambda c: c["posted"], reverse=True)
        ok = True
    else:
        wait_comment_slot()
        try:
            http_get(url, True, {"commenttext_new": text})
        except Exception as e:
            # 提交后等回应超时，评论往往已经发出去了；不报失败，下面重新读评论区确认
            log(f"提交评论后网站迟迟没回应（{e}），重新读取评论区确认")
            time.sleep(5)
        mark_comment_sent()
        comments = sorted(gallery_comments(gid, token), key=lambda c: c["posted"], reverse=True)
        ok = any(head in " ".join(c["text"].split()) for c in comments)
    CACHE_DIR.mkdir(exist_ok=True)
    cache = json.loads(MY_COMMENTS.read_text(encoding="utf-8")) if MY_COMMENTS.exists() else {}
    cache[str(gid)] = {"fetched": datetime.now().isoformat(timespec="seconds"), "total": len(comments), "items": comments}
    MY_COMMENTS.write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
    log(f"在 {gid} 发表评论" + ("成功" if ok else "后没有在评论区找到，可能被网站拒绝（频率限制或内容不符合要求）"))
    emit("comment_posted", gid=gid, ok=ok, total=len(comments), items=comments)


def cmd_addtags(data_file: str, cfg: dict) -> None:
    """给已发布的画廊批量补标签：每个画廊先查已有标签，已有的跳过，只投缺的；全都有的画廊不打开页面。"""
    from playwright.sync_api import sync_playwright
    need_login()
    data = json.loads(Path(data_file).read_text(encoding="utf-8"))
    tags = [t.strip().lower() for t in data.get("tags", []) if t.strip()]
    galleries = [(int(g), str(t)) for g, t in data.get("galleries", [])]
    if not tags or not galleries:
        emit("fatal", error="没有要补的标签或画廊")
        sys.exit(1)
    if profile_in_use():
        emit("fatal", error="上传用的浏览器资料正被占用（可能有登录窗口或上传还开着），关掉后再试")
        sys.exit(1)
    log(f"给 {len(galleries)} 个画廊补标签：{', '.join(tags)}")
    meta = {}
    for k in range(0, len(galleries), 25):
        meta.update(api_gdata(galleries[k:k + 25]))
    todo = [(g, t) for g, t in galleries if any(x not in meta.get(g, {}).get("tags", []) for x in tags)]
    skipped = len(galleries) - len(todo)
    log(f"  其中 {skipped} 个已经有这些标签，跳过；要补的 {len(todo)} 个")
    done, failed = 0, []
    if todo:
        with sync_playwright() as pw:
            ctx, page = open_browser(pw, cfg, cfg["headless"])
            page.set_default_timeout(30_000)
            try:
                for n, (gid, token) in enumerate(todo, 1):
                    emit("addtags_progress", text=f"正在补标签 {n}/{len(todo)}…（跳过 {skipped} 个已有的）")
                    try:
                        try:
                            missing, reasons = add_tags(page, gid, token, tags, cfg)
                        except Exception as ex:   # 网络卡一下（页面加载超时之类）：等 10 秒再试一次
                            log(f"  {gid}：{str(ex).splitlines()[0][:80]}，10 秒后重试")
                            time.sleep(10)
                            missing, reasons = add_tags(page, gid, token, tags, cfg)
                        done += 1
                        if missing:
                            failed.append((gid, missing, reasons))
                            log(f"  {gid}：未生效 {', '.join(missing)}" + (f"（{'；'.join(reasons)}）" if reasons else ""))
                        else:
                            log(f"  {gid}：已补齐")
                    except Exception as ex:
                        failed.append((gid, tags, [str(ex).splitlines()[0][:120]]))
                        log(f"  {gid}：失败 {ex}")
                    if n < len(todo):
                        pause(cfg["delay_between_steps"])
            finally:
                ctx.close()
    log(f"补标签结束：处理 {done} 个，跳过 {skipped} 个，有未生效的 {len(failed)} 个")
    emit("addtags_done", done=done, skipped=skipped, failed=[{"gid": g, "missing": m, "reasons": r} for g, m, r in failed])


def cmd_tag(keys: list[str], cfg: dict) -> None:
    """按上传任务键（gid，或拆分出的 gid-p起始页）重新补标签。"""
    from playwright.sync_api import sync_playwright
    state = load_state()
    with sync_playwright() as pw:
        ctx, page = open_browser(pw, cfg, cfg["headless"])
        for g in keys:
            st = state.get(g)
            if not st or "gid" not in st:
                log(f"{g}：没有已发布记录")
                continue
            item = next((t for t in prepare([st["folder"]], cfg) if t.key == g), None)
            if not item or not item.meta:
                log(f"{g}：找不到对应的作品，无法重新生成标签")
                continue
            missing, reasons = add_tags(page, int(st["gid"]), st["token"], item.meta["tags"], cfg)
            st["missing_tags"] = missing
            st["tag_errors"] = reasons
            save_state(state)
            log(f"{g}：" + (f"未生效 {', '.join(missing)}" if missing else "标签齐全"))
        ctx.close()


def main() -> None:
    ap = argparse.ArgumentParser(description="e-hentai 翻译画廊自动上传")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("login")
    for name in ("scan", "upload"):
        p = sub.add_parser(name)
        p.add_argument("paths", nargs="*", help=f"批次或作品目录，默认 {DEFAULT_SOURCE}")
        p.add_argument("--refilled-after", type=parse_date, metavar="日期", help="只处理此日期之后回填的 cbz，如 2026-10-01")
        p.add_argument("--source-age", type=parse_age, metavar="时长", help="原画廊发布至少多久，如 3m、6m、90d")
        p.add_argument("--only", nargs="+", type=int, metavar="GID", help="只处理指定的原画廊 gid")
        p.add_argument("--json", action="store_true", help="输出给 EXE 界面用的结构化结果")
        p.add_argument("--keys", nargs="+", metavar="KEY", help="只处理指定的上传任务（gid 或 gid-p起始页）")
        if name == "upload":
            p.add_argument("--limit", type=int, help="最多上传几部")
            p.add_argument("--force", action="store_true", help="忽略回填检查等警告")
    p = sub.add_parser("tag")
    p.add_argument("gids", nargs="+")
    p = sub.add_parser("config", help="输出配置；--set 用 JSON 文件更新配置")
    p.add_argument("--set", metavar="JSON文件")
    p = sub.add_parser("mine", help="列出「我的上传」里已发布的画廊和最新评论")
    p.add_argument("--n", type=int, default=3, help="每个画廊显示几条最新评论")
    p.add_argument("--refresh", choices=["cache", "smart", "all"], default="smart")
    p = sub.add_parser("comments", help="读取画廊最新的评论")
    p.add_argument("gid", type=int)
    p.add_argument("token")
    p.add_argument("--n", type=int, default=5)
    p = sub.add_parser("addtags", help="给已发布的画廊批量补标签（JSON 文件：tags 列表 + galleries 的 [gid, token] 列表）")
    p.add_argument("--file", required=True)
    p = sub.add_parser("post-comment", help="在画廊下发表评论（正文放在 UTF-8 文本文件里）")
    p.add_argument("gid", type=int)
    p.add_argument("token")
    p.add_argument("--file", required=True)
    p.add_argument("--n", type=int, default=5)
    args = ap.parse_args()
    cfg = load_config()
    if args.cmd == "mine":
        cmd_mine(args.n, args.refresh)
    elif args.cmd == "comments":
        cmd_comments(args.gid, args.token, args.n)
    elif args.cmd == "addtags":
        cmd_addtags(args.file, cfg)
    elif args.cmd == "post-comment":
        cmd_post_comment(args.gid, args.token, args.file, args.n)
    elif args.cmd == "config":
        if args.set:
            new = json.loads(Path(args.set).read_text(encoding="utf-8"))
            cfg.update({k: v for k, v in new.items() if k in CONFIG_DOCS})
            write_config(cfg)
        emit("config", **cfg)
    elif args.cmd == "login":
        cmd_login(cfg)
    elif args.cmd == "tag":
        cmd_tag(args.gids, cfg)
    else:
        items = prepare(args.paths, cfg, args.refilled_after, args.source_age)
        if args.only:
            items = [i for i in items if i.gid in args.only]
        if args.keys:
            items = [i for i in items if i.key in args.keys]
        detect_updates(items)
        if args.cmd == "scan":
            (scan_json if args.json else print_scan)(items, load_state(), cfg)
        else:
            cmd_upload(items, cfg, args.limit, args.force)


if __name__ == "__main__":
    main()
