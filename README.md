# EH 上传助手（EHUploader）

[![最新版本](https://img.shields.io/github/v/release/tiger-git2022/EHUploader?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/tiger-git2022/EHUploader/releases/latest)
[![编译发布](https://github.com/tiger-git2022/EHUploader/actions/workflows/release.yml/badge.svg)](https://github.com/tiger-git2022/EHUploader/actions/workflows/release.yml)
![平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows%2010%20%2F%2011-blue)

把翻译好的漫画（manga-translator-ui 回填后的 cbz）上传为 E-Hentai 的中文翻译画廊：自动找到原画廊，复制分类和标签，生成带 `[Chinese]` 的标题，分批上传图片并发布，发布后补标签、发评论。图形界面是 WPF 程序 `EHUploader.exe`，实际干活的是同目录下的 Python 脚本 `eh_upload.py`。

```
漫画根目录\批次\123456-[作品名]\123456.cbz   ← 回填好译图的 cbz
漫画根目录\批次\123456\manga_translator_work\result\  ← 翻译结果（用来判断哪些页翻过）
            │
            ▼  扫描：查原画廊（gid/token、标签、标题）、检查原画廊评论里有没有「禁止翻译 / 已预定」
EHUploader.exe ──调用──► eh_upload.py ──Playwright 控制浏览器──► upld.e-hentai.org 上传、发布、补标签
```

## 下载

到 [Releases](https://github.com/tiger-git2022/EHUploader/releases/latest) 下载最新的 `EHUploader-版本号.zip`，解压到任意文件夹（路径里最好不要有空格以外的特殊符号），里面有：

| 文件 | 说明 |
| --- | --- |
| `EHUploader.exe` | 图形界面，双击运行 |
| `eh_upload.py` | 后端脚本，**必须和 exe 放在同一个文件夹** |
| `config.example.toml` | 示例配置，第一次运行时自动复制成 `config.toml` |
| `requirements.txt` | Python 依赖清单 |
| 其余 `.dll` | 界面库，不要删 |

只支持 Windows 10 / 11。首次运行如果 SmartScreen 拦截，点“更多信息 → 仍要运行”。各版本改了什么见 [更新日志](CHANGELOG.md)。

## 运行环境

1. **.NET Framework 4.8**：Windows 10 1903 以后、Windows 11 自带，一般不用装。
2. **Python 3.11 或更新版本**（脚本用到 `tomllib`）：从 [python.org](https://www.python.org/downloads/) 安装，勾选 “Add python.exe to PATH”。没加到 PATH 的话，在程序“设置”页把 Python 路径填成 `python.exe` 的完整路径。
3. **Python 依赖**：在解压出的文件夹里打开终端，运行

   ```bash
   python -m pip install -r requirements.txt
   ```

   会装上 Playwright（控制浏览器）和 Pillow（处理图片）。
4. **浏览器**：默认用系统自带的 **Microsoft Edge**，所以**不需要**再运行 `playwright install`。想用别的 Chromium 内核浏览器（Chrome、Cent Browser 等），在设置页或 `config.toml` 的 `browser_path` 里填它的 exe 路径。

## 代理设置

E-Hentai 在部分地区需要代理才能访问。程序有两种联网方式，都要能连上：

- **浏览器**（登录、上传、发布、补标签）：Edge / Chromium 默认跟随 **Windows 系统代理**。
- **脚本自己发的请求**（查原画廊、读评论、查上传进度）：Python `urllib` 同样读取 Windows 系统代理；也可以用环境变量 `HTTPS_PROXY` / `HTTP_PROXY` 指定。

最简单的做法：在代理软件（Clash、v2rayN 等）里打开“系统代理”。如果只想给本程序设代理，可以在启动前设置环境变量（端口换成你的代理端口）：

```bash
set HTTPS_PROXY=http://127.0.0.1:端口
```

注意浏览器不读这个环境变量，此时浏览器那一侧仍需系统代理。

## 第一次使用

1. 双击 `EHUploader.exe`，打开 **设置** 页：
   - **漫画根目录**：选放回填好作品的文件夹（程序会往下找两层，识别 `数字-[标题]\数字.cbz` 这样的作品文件夹）。
   - **Python 路径**：默认 `python`。
   - 上传简介、评论、额外标签、每部之间的等待时间等，保存后写进 `config.toml`。
2. 点 **登录**：会弹出一个独立的浏览器窗口，在里面手动登录 E-Hentai 论坛账号，登录成功后**关掉这个窗口**。登录信息保存在程序目录的 `browser_profile\` 里，之后不用再登录；程序不会读取或保存你的密码。
3. 回到 **作品** 页点 **扫描**，检查列表里的标题、标签和提示，勾选后点上传。

### 可选设置（`config.toml`）

| 键 | 说明 |
| --- | --- |
| `source_dir` | 命令行不指定目录时默认扫描的漫画根目录 |
| `mtu_dir` | 本地 manga-translator-ui 目录。单行本 / 杂志拆篇时用它识别目次页的日文；不填就跳过识别 |
| `att_config` | 一个含 `[llm.clients.<名称>]`（`url`、`api_key`、`model`，OpenAI 兼容接口）的 TOML 文件，用来把日文篇名、作者名转成罗马音。不填时找 `source_dir\att-mz\runtime\config.toml`，都没有就保留日文 |
| `romaji_client` | 上面文件里用哪个客户端，默认 `extra` |

`config.toml`、`browser_profile\`、`cache\`、`logs\`、`upload_state.json` 等都是你本机的数据（里面有登录 cookie、上传记录），**不要分享给别人，也不要提交到仓库**。

## 功能

- **自动找原画廊**：从 cbz 的 ComicInfo 或文件夹名拿到原画廊编号；找不到时按标题搜索，原画廊被删的作品会尝试用其他站的同一本作参考。
- **原画廊评论检查**：评论里出现“禁止机翻”“已预定”“翻译中”等词时先拦下，确认后才能上传（关键词可在配置里改）。
- **标题和标签**：在原标题里插入 `[Chinese]` / `[中国翻訳]`，复制原画廊标签（`language:` 除外）并加上 `extra_tags`；发布后自动补标签，没生效的会记在上传记录里。
- **分批上传**：按 `batch_max_mb` / `batch_max_files` 分批，避开网站单次 500MB 的限制。
- **单行本拆篇**：识别目次页，把单行本、杂志按篇拆成多个画廊上传。
- **更新已发布画廊**：本地改过译图后再上传，会发布新版本，保留原有评论。
- **我的上传**：列出已发布画廊和最新评论，可以直接回复、批量加标签、打开改名投票页。
- **托盘运行**：关窗口时最小化到托盘，上传在后台继续。
- **自动更新**：定时检查 GitHub 上的新版本，有新版本时托盘提示，点一下就能下载并替换程序文件（设置和记录不动），完成后自动重新打开；托盘菜单里也可以手动“检查更新…”。新版本的 `requirements.txt` 有变化时，更新后再执行一次 `python -m pip install -r requirements.txt`。

## 命令行用法

不想用界面时，脚本也能单独运行：

```bash
python eh_upload.py login
```

```bash
python eh_upload.py scan 漫画根目录或批次目录
```

```bash
python eh_upload.py upload 漫画根目录或批次目录 --limit 3
```

`python eh_upload.py -h` 查看全部子命令（`tag`、`mine`、`comments`、`addtags`、`post-comment`、`config`）。

## 从源码编译

需要 [.NET SDK 8](https://dotnet.microsoft.com/download)（Windows）：

```bash
dotnet publish gui_src/EHUploader.csproj -c Release -o out/EHUploader
```

再把 `eh_upload.py`、`config.example.toml`、`requirements.txt` 复制到 `out/EHUploader` 即可运行。

## 发布新版本（维护者）

1. 改 `gui_src/EHUploader.csproj` 里的 `<Version>`（程序标题栏会显示这个版本号）。
2. 在 `CHANGELOG.md` 顶部加一节 `## v新版本号`。
3. 合并（或推送）到 `main`。

GitHub Actions 发现这个版本号还没有 `v版本号` 标签，就会在 Windows 上编译、打包 zip，以 CHANGELOG 里对应的一节作为说明发布到 Releases，并打上标签。版本号没变时推送到 `main` 只编译检查，产物在 Actions 运行页的 Artifacts 里。也可以自己推标签 `v新版本号` 来发布，标签和 `<Version>` 不一致时编译会直接失败。

## 注意

- 请遵守 E-Hentai 的上传规则，尊重原作者和其他翻译组；评论检查只是辅助，最终是否上传由你决定。
- 本程序只自动化你自己账号的网页操作，不提供任何绕过网站限制的功能。

## 许可证

[MIT](LICENSE)
