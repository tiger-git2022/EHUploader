#!/usr/bin/env python3
"""平板收发：在局域网里接收 EhViewer 发来的漫画，解压（可选自动识别原文），翻译后打包回填，等平板取回。

用法：
  python tablet_bridge.py --root 漫画根目录 [--port 38765] [--ocr]

收到的漫画放在 漫画根目录\\批次\\{gid}-[标题]\\{gid}.cbz（和 ComicInfo.xml），解压到 漫画根目录\\批次\\{gid}\\，
和作品列表扫描、manga-translator-ui 用的结构一样。翻好文字后点「渲染并送回」：用 manga-translator-ui 的
「导入翻译并渲染」把译文嵌进图里，放进 {gid}\\manga_translator_work\\result，再按 cbz-refill 的规则写回 {gid}.cbz：
译图不到原图 40% 时只保留译图（重新编号），否则逐页替换。已经自己渲染好的，点「直接打包送回」只做后半步。

通信（HTTP，端口默认 38765，同一端口的 UDP 用来让平板自动找到电脑）：
  POST /pair                              配对码换取令牌
  GET  /api/info | /api/folders           电脑信息、批次文件夹
  PUT  /api/items/{gid}/files/{name}      上传 {gid}.cbz、ComicInfo.xml（?folder=批次&dir=作品文件夹名）
  POST /api/items/{gid}/received          文件传完，开始解压
  GET  /api/items?gids=1,2                状态
  GET  /api/items/{gid}/result            取回打包好的 cbz
  POST /api/items/{gid}/taken             平板已取回
  DELETE /api/items/{gid}                 平板撤销（文件留着，只是不再出现在列表里）
  /local/*                                只接受本机（EXE 界面）的请求：列表、渲染、打包送回、移出列表、换配对码
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import sys
import threading
import time
import tomllib
import zipfile
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from queue import Queue
from urllib.parse import parse_qs, unquote, urlparse

ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "config.toml"
STATE_PATH = ROOT / "tablet_state.json"
CACHE_DIR = ROOT / "cache"
LOG_DIR = ROOT / "logs"
VERSION = 1
DISCOVER = b"EHV-TABLET-DISCOVER"
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".jxl"}
TMP_CROP = re.compile(r"^tmp-\d+-r\d+\.\w+$", re.I)   # 翻译工具修图时留下的小裁切图，不是正文页
SKIP_DIRS = {"已上传", "老文件", "att-mz", "tests", "pipeline_backups"}
SPECIAL_RATIO = 0.4
BAD_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
LOG_DIR.mkdir(exist_ok=True)
_print_lock = threading.Lock()


def log(msg: str) -> None:
    line = f"[{datetime.now():%H:%M:%S}] {msg}"
    with _print_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
        with (LOG_DIR / f"tablet_{datetime.now():%Y%m%d}.log").open("a", encoding="utf-8") as f:
            f.write(line + "\n")


def emit(ev: str, **data) -> None:
    """给 EXE 界面的结构化事件，一行一个，以 @@ 开头。"""
    line = "@@" + json.dumps({"ev": ev, **data}, ensure_ascii=False, default=str)
    with _print_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def natural_key(s: str):
    return [int(t) if t.isdigit() else t for t in re.split(r"(\d+)", s.lower())]


def mtu_dir() -> Path | None:
    try:
        with CONFIG_PATH.open("rb") as f:
            v = tomllib.load(f).get("mtu_dir")
    except (OSError, ValueError):
        v = None
    return Path(v) if v else None


def is_image(name: str) -> bool:
    n = name.replace("\\", "/").rsplit("/", 1)[-1]
    return Path(n).suffix.lower() in IMAGE_EXTS and not TMP_CROP.match(n) and not n.startswith(".")


def safe_name(name: str) -> str:
    """Windows 文件名里不能有的字符换成下划线，去掉结尾的点和空格，太长的截短。"""
    name = BAD_CHARS.sub("_", name).strip().rstrip(". ")
    return name[:150].rstrip(". ") or "_"


# ---------------------------------------------------------------- 状态

class State:
    """tablet_state.json：配对过的平板（令牌）和收到的作品。只有这个进程写它。"""

    def __init__(self, path: Path):
        self.path = path
        self.lock = threading.RLock()
        try:
            self.data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            self.data = {}
        self.data.setdefault("server_id", secrets.token_hex(8))
        self.data.setdefault("tokens", {})
        self.data.setdefault("items", {})
        self.save()

    def save(self) -> None:
        with self.lock:
            tmp = self.path.with_suffix(".tmp")
            tmp.write_text(json.dumps(self.data, ensure_ascii=False, indent=1), encoding="utf-8")
            os.replace(tmp, self.path)

    @property
    def items(self) -> dict:
        return self.data["items"]

    def item(self, gid: str) -> dict | None:
        return self.items.get(str(gid))

    def update(self, gid: str, **kv) -> dict:
        with self.lock:
            it = self.items.setdefault(str(gid), {"gid": int(gid)})
            it.update(kv, updated=datetime.now().isoformat(timespec="seconds"))
            self.save()
            return it


# ---------------------------------------------------------------- 作品

class Bridge:
    def __init__(self, root: Path, port: int, ocr: bool):
        self.root = root
        self.port = port
        self.ocr = ocr
        self.state = State(STATE_PATH)
        self.code = ""
        self.bad_codes = 0
        self.jobs: Queue = Queue()
        self.seen: dict[str, float] = {}   # 平板令牌 -> 最近一次请求的时间
        self.new_code()

    # ---- 路径 ----

    def paths(self, it: dict) -> tuple[Path, Path, Path]:
        """(作品文件夹, cbz, 解压出的工作文件夹)。"""
        folder = self.root / it["folder"] / it["dir"]
        gid = str(it["gid"])
        return folder, folder / f"{gid}.cbz", self.root / it["folder"] / gid

    def original(self, it: dict) -> Path:
        """第一次打包前留一份平板发来的原版，之后每次打包都从它重来，打包几次结果都一样。"""
        folder, _, _ = self.paths(it)
        return folder / f"{it['gid']}.original.cbz"

    def result_images(self, it: dict) -> list[Path]:
        _, _, work = self.paths(it)
        res = work / "manga_translator_work" / "result"
        if not res.is_dir():
            return []
        return sorted((p for p in res.iterdir() if p.is_file() and is_image(p.name)), key=lambda p: natural_key(p.name))

    def view(self, it: dict) -> dict:
        """给平板和界面看的一条：状态加上当前译图数量。"""
        out = {k: it.get(k) for k in ("gid", "title", "folder", "dir", "state", "message", "pages", "translated", "device", "updated")}
        if it.get("state") in ("waiting", "error", "ready", "taken"):
            out["translated_now"] = len(self.result_images(it))
        return out

    def folders(self) -> dict:
        today = datetime.now().strftime("%Y-%m-%d")
        rows = []
        if self.root.is_dir():
            for d in self.root.iterdir():
                if not d.is_dir() or d.name.startswith((".", "_")) or d.name in SKIP_DIRS or d.name.isdigit():
                    continue
                works = [w for w in d.iterdir() if w.is_dir() and re.match(r"^\d+-", w.name)]
                untranslated = 0
                for w in works:
                    res = d / w.name.split("-", 1)[0] / "manga_translator_work" / "result"
                    if not res.is_dir() or not any(res.iterdir()):
                        untranslated += 1
                rows.append({"name": d.name, "count": len(works), "untranslated": untranslated,
                             "mtime": int(d.stat().st_mtime)})
        rows.sort(key=lambda r: natural_key(r["name"]), reverse=True)
        return {"folders": rows, "suggest": today, "root": str(self.root)}

    # ---- 任务 ----

    def worker(self) -> None:
        while True:
            kind, gid = self.jobs.get()
            it = self.state.item(gid)
            if not it:
                continue
            try:
                if kind == "extract":
                    self.extract(it)
                elif kind == "ocr":
                    self.run_ocr(it)
                elif kind == "pack":
                    self.pack(it)
                elif kind == "render":
                    self.render(it)
            except Exception as e:   # 一本出错不影响其他的
                log(f"{gid} {kind} 出错：{e!r}")
                what = {"extract": "解压", "ocr": "识别原文", "pack": "打包", "render": "渲染"}[kind]
                self.state.update(gid, state="error", message=f"{what}失败：{e}")
            self.changed()

    def changed(self) -> None:
        emit("tablet_items", items=[self.view(it) for it in self.sorted_items()])

    def sorted_items(self) -> list[dict]:
        items = [it for it in self.state.items.values() if not it.get("hidden")]
        return sorted(items, key=lambda it: it.get("received", ""), reverse=True)

    def extract(self, it: dict) -> None:
        gid = str(it["gid"])
        _, cbz, work = self.paths(it)
        self.state.update(gid, state="extracting", message="正在解压")
        self.changed()
        work.mkdir(parents=True, exist_ok=True)
        n = 0
        with zipfile.ZipFile(cbz) as z:
            for e in z.infolist():
                if e.is_dir() or not is_image(e.filename):
                    continue
                name = safe_name(e.filename.replace("\\", "/").rsplit("/", 1)[-1])
                with z.open(e) as src, (work / name).open("wb") as dst:
                    shutil.copyfileobj(src, dst, 1 << 20)
                n += 1
        log(f"{gid} 解压 {n} 张 → {work}")
        self.state.update(gid, pages=n)
        py = (mtu_dir() / ".venv" / "Scripts" / "python.exe") if mtu_dir() else None
        if self.ocr and py and py.exists():
            self.jobs.put(("ocr", gid))
            self.state.update(gid, state="ocr_queued", message="等待识别原文")
        else:
            self.state.update(gid, state="waiting", message="等你翻译" if not self.ocr or not py
                              else "等你翻译（没找到 manga-translator-ui，没有自动识别原文）")
            emit("tablet_notice", text=f"收到 {it.get('title') or gid}，已解压到 {it['folder']}\\{gid}")

    def run_mtu(self, gid: str, work: Path, cli: dict, conf_name: str, extra: list[str] = (), no_translator: bool = False,
                label: str = "", done=None) -> None:
        """跑一遍 manga-translator-ui 的命令行（local 模式），cli 是这次要用的工作流开关，其他设置照它自己的 config.json。
        done() 数出已经处理好几页，跑的时候每两秒更新一次状态（「label 12/30 页」）。"""
        mtu = mtu_dir()
        py = mtu / ".venv" / "Scripts" / "python.exe" if mtu else None
        if not py or not py.exists():
            raise RuntimeError("没找到 manga-translator-ui（config.toml 的 mtu_dir）")
        conf = json.loads((mtu / "config" / "config.json").read_text(encoding="utf-8"))
        flags = dict(template=False, save_text=False, load_text=False, translate_json_only=False,
                     generate_and_export=False, replace_translation=False, export_from_local_json=False)
        flags.update(cli)
        conf.setdefault("cli", {}).update(flags)
        if no_translator:
            conf.setdefault("translator", {})["translator"] = "none"
        CACHE_DIR.mkdir(exist_ok=True)
        conf_file = CACHE_DIR / conf_name
        conf_file.write_text(json.dumps(conf, ensure_ascii=False), encoding="utf-8")
        total = len([p for p in work.iterdir() if p.is_file() and is_image(p.name)])
        out_log = LOG_DIR / f"mtu_{Path(conf_name).stem}.log"   # 输出写文件，不用管道，免得输出太多卡住
        with out_log.open("wb") as f:
            proc = subprocess.Popen([str(py), "-X", "utf8", "-m", "manga_translator", "local", "-i", str(work), "--config", str(conf_file), *extra],
                                    cwd=mtu, env=dict(os.environ, PYTHONUTF8="1"), stdout=f, stderr=subprocess.STDOUT,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            end = time.time() + 3 * 3600
            last = -1
            while True:
                try:
                    code = proc.wait(timeout=2)
                    break
                except subprocess.TimeoutExpired:
                    pass
                if time.time() > end:
                    proc.kill()
                    proc.wait()
                    raise RuntimeError("manga-translator-ui 跑了 3 个小时还没完，已停止")
                if done and label:
                    n = min(done(), total) if total else done()
                    if n != last:
                        last = n
                        self.state.update(gid, message=f"{label} {n}/{total} 页" if total else f"{label} {n} 页")
                        self.changed()
        if code != 0:
            tail = out_log.read_bytes().decode("utf-8", "replace").strip().splitlines()[-3:]
            raise RuntimeError(f"manga-translator-ui 退出码 {code}：{' / '.join(tail)}")

    def run_ocr(self, it: dict) -> None:
        """用 manga-translator-ui 的「导出原文」模式识别整本（不翻译、不渲染），结果在 manga_translator_work 里。"""
        gid = str(it["gid"])
        _, _, work = self.paths(it)
        self.state.update(gid, state="ocr", message="正在识别原文")
        self.changed()
        log(f"{gid} 用 manga-translator-ui 识别原文：{work}")
        t = time.time()
        jdir = work / "manga_translator_work" / "json"
        start = time.time() - 1
        # 识别好一页就写一份 json（重新识别时旧的也在，只数这次写的）
        done = lambda: sum(1 for p in jdir.glob("*_translations.json") if p.stat().st_mtime >= start) if jdir.is_dir() else 0
        self.run_mtu(gid, work, dict(template=True, save_text=True, save_to_source_dir=True), "tablet_ocr.json",
                     no_translator=True, label="正在识别原文", done=done)
        log(f"{gid} 识别原文完成，用时 {int(time.time() - t)} 秒")
        self.state.update(gid, state="waiting", message="原文已识别，等你翻译")
        emit("tablet_notice", text=f"{it.get('title') or gid} 的原文识别好了，可以开始翻译")

    def render(self, it: dict) -> None:
        """用 manga-translator-ui 的「导入翻译并渲染」把翻好的文字嵌回图里，译图放进 manga_translator_work\\result，然后打包。"""
        gid = str(it["gid"])
        _, _, work = self.paths(it)
        mtw = work / "manga_translator_work"
        jsons = list((mtw / "json").glob("*_translations.json")) if (mtw / "json").is_dir() else []
        if not jsons:
            self.state.update(gid, state="waiting", message="还没有识别原文（manga_translator_work\\json 是空的），先识别原文再翻译")
            return
        self.state.update(gid, state="rendering", message=f"正在导入翻译并渲染（{len(jsons)} 页）")
        self.changed()
        # 命令行模式不会自己放进 result，先渲染到临时文件夹再挪过去
        out = mtw / "_tablet_render"
        shutil.rmtree(out, ignore_errors=True)
        log(f"{gid} 用 manga-translator-ui 导入翻译并渲染：{work}")
        t = time.time()
        try:
            done = lambda: sum(1 for p in out.rglob("*") if p.is_file() and is_image(p.name)) if out.is_dir() else 0
            self.run_mtu(gid, work, dict(load_text=True, ignore_errors=True, batch_concurrent=False), "tablet_render.json",
                         ["-o", str(out), "--overwrite"], label="正在导入翻译并渲染", done=done)
            made = [p for p in out.rglob("*") if p.is_file() and is_image(p.name)] if out.is_dir() else []
            if not made:
                raise RuntimeError("manga-translator-ui 没有渲染出图片，看看译文 TXT 是不是在 manga_translator_work\\originals 里")
            res = mtw / "result"
            res.mkdir(parents=True, exist_ok=True)
            stems = {p.stem.lower() for p in made}
            for old in res.iterdir():   # 换了输出格式时，同名旧译图（另一种扩展名）不能留着
                if old.is_file() and is_image(old.name) and old.stem.lower() in stems:
                    old.unlink()
            for p in made:
                os.replace(p, res / p.name)
        finally:
            shutil.rmtree(out, ignore_errors=True)
        log(f"{gid} 渲染完成：{len(made)} 张，用时 {int(time.time() - t)} 秒")
        self.pack(self.state.item(gid))

    def pack(self, it: dict) -> None:
        gid = str(it["gid"])
        _, cbz, _ = self.paths(it)
        results = self.result_images(it)
        if not results:
            self.state.update(gid, state="waiting", message="还没有译图（manga_translator_work\\result 是空的）")
            return
        self.state.update(gid, state="packing", message="正在打包")
        self.changed()
        orig = self.original(it)
        if not orig.exists():
            shutil.copy2(cbz, orig)
        with zipfile.ZipFile(orig) as src:
            entries = [e for e in src.infolist() if not e.is_dir()]
            images = [e for e in entries if is_image(e.filename)]
            others = [e for e in entries if not is_image(e.filename) and not TMP_CROP.match(Path(e.filename).name)]
            special = len(results) < len(images) * SPECIAL_RATIO
            tmp = cbz.with_name(cbz.name + ".tmp")
            with zipfile.ZipFile(tmp, "w", zipfile.ZIP_STORED) as out:
                for e in others:
                    out.writestr(e, src.read(e))
                if special:
                    for i, p in enumerate(results, 1):
                        out.write(p, f"{i:08d}{p.suffix.lower()}")
                    pages = len(results)
                else:
                    by_stem = {p.stem.lower(): p for p in results}
                    used = set()
                    for e in sorted(images, key=lambda e: natural_key(e.filename)):
                        stem = Path(e.filename.replace("\\", "/").rsplit("/", 1)[-1]).stem.lower()
                        p = by_stem.get(stem)
                        if p:
                            used.add(p)
                            name = e.filename[: len(e.filename) - len(Path(e.filename).suffix)] + p.suffix.lower()
                            out.write(p, name)
                        else:
                            out.writestr(e, src.read(e))
                    for p in results:
                        if p not in used:
                            out.write(p, p.name)
                    pages = len(images) + len([p for p in results if p not in used])
        os.replace(tmp, cbz)
        mode = "只保留译图" if special else "逐页替换"
        log(f"{gid} 打包：译图 {len(results)}/{len(images)}，{mode}")
        self.state.update(gid, state="ready", message=f"已打包（译图 {len(results)}/{len(images)}，{mode}），等平板取回",
                          translated=len(results), pages=pages, packed=datetime.now().isoformat(timespec="seconds"))

    # ---- 配对 ----

    def new_code(self) -> None:
        self.code = f"{secrets.randbelow(1_000_000):06d}"
        self.bad_codes = 0
        emit("tablet_code", code=self.code)

    def device(self, token: str | None) -> str | None:
        if not token:
            return None
        for t, d in self.state.data["tokens"].items():
            if hmac.compare_digest(t, token):
                self.seen[t] = time.time()
                return d.get("device", "平板")
        return None

    def online(self) -> list[str]:
        now = time.time()
        return [self.state.data["tokens"][t].get("device", "平板") for t, at in self.seen.items()
                if now - at < 90 and t in self.state.data["tokens"]]


# ---------------------------------------------------------------- HTTP

class Handler(BaseHTTPRequestHandler):
    bridge: Bridge = None
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):   # 不往 stderr 打访问日志
        pass

    def send_json(self, data, code: int = 200) -> None:
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def fail(self, code: int, msg: str) -> None:
        self.close_connection = True   # 请求体可能没读完，不能接着用这个连接
        self.send_json({"error": msg}, code)

    def body_json(self) -> dict:
        n = int(self.headers.get("Content-Length") or 0)
        if n > 1 << 20:
            raise ValueError("请求太大")
        return json.loads(self.rfile.read(n) or b"{}") if n else {}

    def route(self, method: str) -> None:
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        parts = [unquote(p) for p in url.path.strip("/").split("/") if p]
        b = self.bridge
        try:
            if parts[:1] == ["local"]:
                if self.client_address[0] not in ("127.0.0.1", "::1"):
                    return self.fail(403, "只接受本机的请求")
                return self.local(method, parts[1:], q)
            if parts == ["pair"] and method == "POST":
                data = self.body_json()
                if not b.code or not hmac.compare_digest(str(data.get("code", "")).strip(), b.code):
                    b.bad_codes += 1
                    if b.bad_codes >= 5:   # 连错 5 次换一个配对码，防止被挨个试
                        log("配对码连续输错 5 次，已换一个新的")
                        b.new_code()
                    return self.fail(403, "配对码不对")
                token = secrets.token_hex(24)
                device = str(data.get("device") or "平板")[:60]
                b.state.data["tokens"][token] = {"device": device, "paired": datetime.now().isoformat(timespec="seconds")}
                b.state.save()
                b.seen[token] = time.time()
                log(f"已和 {device} 配对")
                emit("tablet_notice", text=f"已和 {device} 配对")
                return self.send_json({"token": token, "name": socket.gethostname(), "id": b.state.data["server_id"]})
            auth = self.headers.get("Authorization", "")
            device = b.device(auth[7:] if auth.startswith("Bearer ") else None)
            if not device:
                return self.fail(401, "没有配对，或者配对已失效")
            return self.api(method, parts, q, device)
        except (ValueError, KeyError) as e:
            return self.fail(400, str(e))
        except Exception as e:
            log(f"处理 {method} {url.path} 出错：{e!r}")
            return self.fail(500, str(e))

    def api(self, method: str, parts: list[str], q: dict, device: str) -> None:
        b = self.bridge
        if parts == ["api", "info"]:
            return self.send_json({"name": socket.gethostname(), "id": b.state.data["server_id"], "root": str(b.root),
                                   "version": VERSION, "ocr": b.ocr})
        if parts == ["api", "folders"]:
            return self.send_json(b.folders())
        if parts == ["api", "items"] and method == "GET":
            gids = [g for g in q.get("gids", "").split(",") if g.isdigit()]
            rows = []
            for g in gids:
                it = b.state.item(g)
                rows.append(b.view(it) if it else {"gid": int(g), "state": "gone"})
            return self.send_json({"items": rows})
        if len(parts) < 3 or parts[:2] != ["api", "items"] or not parts[2].isdigit():
            return self.fail(404, "没有这个接口")
        gid = parts[2]
        rest = parts[3:]
        if rest[:1] == ["files"] and len(rest) == 2 and method == "PUT":
            name = rest[1]
            if name not in (f"{gid}.cbz", "ComicInfo.xml"):
                return self.fail(400, "只收 cbz 和 ComicInfo.xml")
            folder, dirname = safe_name(q["folder"]), safe_name(q["dir"])
            if not dirname.startswith(f"{gid}-"):
                dirname = safe_name(f"{gid}-{dirname}")
            if (b.state.item(gid) or {}).get("state") in ("extracting", "ocr", "rendering", "packing"):
                return self.fail(409, "电脑正在处理这一本，等一会儿再发")
            dest = b.root / folder / dirname
            dest.mkdir(parents=True, exist_ok=True)
            n = int(self.headers["Content-Length"])
            part = dest / (name + ".part")
            with part.open("wb") as f:
                left = n
                while left > 0:
                    chunk = self.rfile.read(min(left, 1 << 20))
                    if not chunk:
                        raise ValueError("传输中断")
                    f.write(chunk)
                    left -= len(chunk)
            if name.endswith(".cbz"):
                zipfile.ZipFile(part).close()   # 能打开才算收到
                old = dest / f"{gid}.original.cbz"
                if old.exists():
                    old.unlink()   # 重新发来的：以新的为原版
            os.replace(part, dest / name)
            b.state.update(gid, state="receiving", message="正在接收", folder=folder, dir=dirname, device=device, hidden=False)
            b.changed()
            return self.send_json({"ok": True, "bytes": n})
        if rest == ["received"] and method == "POST":
            data = self.body_json()
            it = b.state.item(gid)
            if not it or not b.paths(it)[1].exists():
                return self.fail(400, "cbz 还没传完")
            it = b.state.update(gid, title=str(data.get("title", ""))[:300], received=datetime.now().isoformat(timespec="seconds"),
                                state="extract_queued", message="等待解压", translated=None)
            log(f"收到 {device} 发来的 {gid}：{it['folder']}\\{it['dir']}")
            b.jobs.put(("extract", gid))
            b.changed()
            return self.send_json(b.view(it))
        it = b.state.item(gid)
        if not it:
            return self.fail(404, "电脑上没有这一本")
        if rest == ["result"] and method == "GET":
            if it.get("state") not in ("ready", "taken"):
                return self.fail(409, "还没打包好")
            cbz = b.paths(it)[1]
            size = cbz.stat().st_size
            self.send_response(200)
            self.send_header("Content-Type", "application/vnd.comicbook+zip")
            self.send_header("Content-Length", str(size))
            self.send_header("X-Pages", str(it.get("pages") or 0))
            self.end_headers()
            with cbz.open("rb") as f:
                shutil.copyfileobj(f, self.wfile, 1 << 20)
            return
        if rest == ["taken"] and method == "POST":
            b.state.update(gid, state="taken", message=f"{device} 已取回")
            emit("tablet_notice", text=f"{device} 已取回 {it.get('title') or gid}")
            b.changed()
            return self.send_json(b.view(it))
        if not rest and method == "DELETE":
            b.state.update(gid, hidden=True, message=f"{device} 撤销了")
            log(f"{device} 撤销了 {gid}（文件留在 {it['folder']}\\{it['dir']}）")
            b.changed()
            return self.send_json({"ok": True})
        return self.fail(404, "没有这个接口")

    def local(self, method: str, parts: list[str], q: dict) -> None:
        b = self.bridge
        if parts == ["items"]:
            return self.send_json({"items": [b.view(it) for it in b.sorted_items()], "code": b.code,
                                   "online": b.online(), "paired": len(b.state.data["tokens"])})
        if parts == ["newcode"] and method == "POST":
            b.new_code()
            return self.send_json({"code": b.code})
        if parts in (["pack"], ["remove"], ["ocr"], ["render"]) and method == "POST":
            gids = [str(g) for g in self.body_json().get("gids", [])]
            queued = {"pack": ("pack_queued", "等待打包"), "ocr": ("ocr_queued", "等待识别原文"),
                      "render": ("render_queued", "等待导入翻译并渲染")}
            for g in gids:
                if not b.state.item(g):
                    continue
                if parts == ["remove"]:
                    b.state.update(g, hidden=True)
                else:
                    state, message = queued[parts[0]]
                    b.state.update(g, state=state, message=message)
                    b.jobs.put((parts[0], g))
            b.changed()
            return self.send_json({"ok": True})
        if parts == ["unpair"] and method == "POST":
            b.state.data["tokens"] = {}
            b.state.save()
            return self.send_json({"ok": True})
        return self.fail(404, "没有这个接口")

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.route("POST")

    def do_PUT(self):
        self.route("PUT")

    def do_DELETE(self):
        self.route("DELETE")


def local_ips() -> list[str]:
    ips = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.add(info[4][0])
    except OSError:
        pass
    try:   # 默认路由那张网卡的地址
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ips.add(s.getsockname()[0])
        s.close()
    except OSError:
        pass
    return sorted(ip for ip in ips if not ip.startswith("127."))


def discovery(bridge: Bridge) -> None:
    """平板在局域网里广播 EHV-TABLET-DISCOVER，这里回一个 JSON 告诉它端口和电脑名。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", bridge.port))
    reply = json.dumps({"app": "ehuploader", "name": socket.gethostname(), "port": bridge.port,
                        "id": bridge.state.data["server_id"], "version": VERSION}).encode("utf-8")
    while True:
        try:
            data, addr = s.recvfrom(256)
            if data.strip() == DISCOVER:
                s.sendto(reply, addr)
        except OSError:
            time.sleep(1)


def main() -> None:
    ap = argparse.ArgumentParser(description="平板收发：接收 EhViewer 发来的漫画，翻译后打包等平板取回")
    ap.add_argument("--root", required=True, help="漫画根目录（下面是日期批次文件夹）")
    ap.add_argument("--port", type=int, default=38765)
    ap.add_argument("--ocr", action="store_true", help="解压后用 manga-translator-ui 自动识别原文")
    args = ap.parse_args()
    root = Path(args.root)
    if not root.is_dir():
        emit("fatal", text=f"漫画根目录不存在：{root}")
        sys.exit(1)
    bridge = Bridge(root, args.port, args.ocr)
    Handler.bridge = bridge
    try:
        server = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
    except OSError as e:
        emit("fatal", text=f"端口 {args.port} 打不开（可能已经有一个在运行）：{e}")
        sys.exit(1)
    server.daemon_threads = True
    threading.Thread(target=bridge.worker, daemon=True).start()
    threading.Thread(target=discovery, args=(bridge,), daemon=True).start()
    # 上次没做完的接着做（程序关掉时正在解压、识别或打包的）
    for gid, it in bridge.state.items.items():
        if it.get("state") in ("extract_queued", "extracting"):
            bridge.jobs.put(("extract", gid))
        elif it.get("state") in ("ocr_queued", "ocr"):
            bridge.jobs.put(("ocr", gid))
        elif it.get("state") in ("pack_queued", "packing"):
            bridge.jobs.put(("pack", gid))
        elif it.get("state") in ("render_queued", "rendering"):
            bridge.jobs.put(("render", gid))
        elif it.get("state") == "receiving":
            bridge.state.update(gid, state="error", message="上次没传完，请在平板上重新发送")
    ips = local_ips()
    log(f"平板收发已开启：{', '.join(ips) or '本机'} 端口 {args.port}，收到的漫画放进 {root}")
    emit("tablet_ready", port=args.port, ips=ips, name=socket.gethostname(), code=bridge.code, root=str(root))
    bridge.changed()
    server.serve_forever()


if __name__ == "__main__":
    main()
