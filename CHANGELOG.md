# 更新日志

每个版本一节，标题写成 `## v版本号`。打标签发布时，GitHub Actions 会把对应的一节作为 Release 说明。

## v1.0.0

- 首次在 GitHub 发布，由 GitHub Actions 自动编译并打包成 zip（`EHUploader.exe` + `eh_upload.py` + 示例配置）。
- 程序标题栏显示版本号。
- 漫画根目录、manga-translator-ui 目录、翻译 API 配置文件改为在 `config.toml` 里设置（`source_dir`、`mtu_dir`、`att_config`），不再写死在程序里。
- 没有 `config.toml` 时，第一次运行自动从 `config.example.toml` 生成。
