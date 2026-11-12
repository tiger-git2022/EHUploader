using System.Collections.Generic;
using System.Linq;

namespace EHUploader;

/// <summary>「平板收发」的「复制提示词」：给在电脑上翻译这一本的 agent 的提示词，模板可以改，占位符换成这一本在电脑上的路径。</summary>
public static class TabletPrompt
{
	/// <summary>占位符和它会换成什么，显示在模板编辑框下面。</summary>
	public static readonly (string Key, string Desc)[] Placeholders =
	{
		("{根目录}", "漫画根目录"),
		("{批次}", "批次文件夹"),
		("{编号}", "画廊编号"),
		("{标题}", "标题"),
		("{作品文件夹}", "批次里 cbz 所在的文件夹名（编号-标题）"),
		("{翻译文件夹}", "解压出的原图文件夹（根目录\\批次\\编号）"),
	};

	public const string DefaultTemplate = @"你现在要帮我搭建并使用 A.T.T MZ 翻译 漫画json文本。所有命令必须以当前仓库 README、skills 和 CLI --help 为准，不要凭空猜测命令。
目标：

1. 克隆或更新 yexi-by/att-mz 项目。
2. 在 Windows 上建立可运行环境。
3. 保持目录分离： 源码目录：att-mz-source 漫画目录：我指定的目录 工作区和报告目录：workspace
4. 安装并同步 Codex skills 到本地 skills 目录，不能只保留在源码仓库中。
5. 创建根目录批处理脚本，可完成注册、准备、规则导入、翻译、质检、写回、漏翻检查
环境要求： Windows PowerShell Python，优先使用 uv 管理 Rust / rustup Visual Studio Build Tools 或可用的 C++ 编译环境 模型 API Key 在配置文件中，不能写进脚本、日志或报告
示例环境变量：
目录约定： 待翻目标：{翻译文件夹} 工作区：att-mz\workspace\<目标文件夹>
基础流程：
6. 更新源码
进入 att-mz-source
执行 git pull
查看 README、skills 和 CLI --help 是否有流程变化
7. 检查环境
执行 doctor 命令，确认 Python、uv、Rust、依赖和扩展可用
8. 读取 E:\漫画源文件\AGENTS.md，按它执行；流程细节见 E:\漫画源文件\att-mz\skills\manga-json-translation 的 SKILL.md 和 references。
9. 准备工作区 为每个漫画创建独立 workspace 生成术语表、规则文件、报告和缓存 旧漫画的工作区和术语表不得覆盖，新漫画使用新文件夹
10. 术语表和规则 换漫画时重新建立术语表，不能直接套用旧漫画 更新版本时优先复用旧版本翻译记忆和术语，避免重翻大部分文本
11. 翻译前对通过ocr标注文本信息，生成指示图，然后通过mtui_json_tool.py 与 translate.py 生成翻译文件并发送给翻译api进行翻译
12. 翻译后质检，并根据质检结果对翻译结果进行修正
13. 人称和称呼校对
翻译完成后必须检查人名、称呼、桑、酱、先生、様、殿等内容
日文中的泛用敬称不能机械翻译
根据角色关系改成人名、你、您、姐姐、老师、小姐等自然中文表达
同一角色称呼必须前后一致
14. 写回漫画 json文件
重要规则： 每次修复新类型漏翻或者翻译规则，经过审核后把经验写入本地 skills。";

	public static string Fill(string template, string root, TabletItem item)
	{
		string r = (root ?? "").TrimEnd('\\', '/');
		Dictionary<string, string> values = new Dictionary<string, string>
		{
			["{根目录}"] = r,
			["{批次}"] = item.Folder ?? "",
			["{编号}"] = item.Gid.ToString(),
			["{标题}"] = item.Title ?? "",
			["{作品文件夹}"] = item.Dir ?? "",
			["{翻译文件夹}"] = $@"{r}\{item.Folder}\{item.Gid}",
		};
		return values.Aggregate(template.Replace("\r\n", "\n").Replace("\n", "\r\n"), (s, kv) => s.Replace(kv.Key, kv.Value));
	}
}
