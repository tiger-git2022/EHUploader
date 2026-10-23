# 更新日志

每个版本一节，标题写成 `## v版本号`。发布时，GitHub Actions 会把对应的一节作为 Release 说明。

## v1.0.2

- 自动检查更新：启动 20 秒后和之后每 12 小时去 GitHub 查一次新版本，有新版本时托盘弹出提示。点提示或托盘菜单里的“更新到 v版本号”，确认后自动下载、校验并替换程序文件，完成后自动重新打开。设置、上传记录、日志等文件不会动。
- 托盘菜单新增“检查更新…”，可以随时手动检查。
- 正在上传时不会更新，等上传结束再点。程序放在不能写入的文件夹（如 Program Files）时会改为打开下载页面。

## v1.0.1

- 托盘图标的右键菜单改成亚克力样式：Windows 11 系统菜单那种磨砂半透明背景、圆角和 Fluent 图标，随深浅色主题变化，文字更清楚（和 Claude 自动审批的托盘菜单一致）。Windows 10 上用纯色背景。
- 托盘菜单可以用方向键选择、回车执行、Esc 关闭。

## v1.0.0

- 首次在 GitHub 发布，由 GitHub Actions 自动编译并打包成 zip（`EHUploader.exe` + `eh_upload.py` + 示例配置）。
- 程序标题栏显示版本号。
- 漫画根目录、manga-translator-ui 目录、翻译 API 配置文件改为在 `config.toml` 里设置（`source_dir`、`mtu_dir`、`att_config`），不再写死在程序里。
- 没有 `config.toml` 时，第一次运行自动从 `config.example.toml` 生成。
