using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EHUploader;

/// <summary>GitHub 上的一个新版本。</summary>
public sealed class UpdateInfo
{
	public string Version;
	public string Notes;
	public string PageUrl;
	public string ZipUrl;
	public string Sha256;
}

/// <summary>
/// 检查 GitHub Releases 上的新版本，下载 zip、校验 SHA-256，退出后用一个隐藏的 PowerShell 脚本把新文件复制进程序文件夹再重新打开。
/// 程序文件夹里不在 zip 里的文件（设置、日志、上传记录等）不会动。
/// </summary>
public static class Updater
{
	public const string Repo = "tiger-git2022/EHUploader";
	public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
	const string AppName = "EHUploader";

	static bool IsAsset(string name) => name.StartsWith("EHUploader-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(AppName + "/" + CurrentVersion);
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	public static string CurrentVersion => MainWindow.AppVersion;

	public static string AppDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

	/// <summary>有比当前新的版本就返回它，否则返回 null。</summary>
	public static async Task<UpdateInfo> CheckAsync()
	{
		string json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false);
		var r = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
		string tag = Str(r, "tag_name") ?? "";
		int[] latest = Parse(tag);
		if (latest == null || Compare(latest, Parse(CurrentVersion)) <= 0)
		{
			return null;
		}
		var info = new UpdateInfo { Version = tag.TrimStart('v', 'V'), Notes = Str(r, "body") ?? "", PageUrl = Str(r, "html_url") ?? ReleasesUrl };
		if (r.TryGetValue("assets", out object assets) && assets is ArrayList list)
		{
			foreach (Dictionary<string, object> a in list.OfType<Dictionary<string, object>>())
			{
				if (IsAsset(Str(a, "name") ?? ""))
				{
					info.ZipUrl = Str(a, "browser_download_url");
					string digest = Str(a, "digest") ?? "";
					info.Sha256 = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7).ToLowerInvariant() : null;
					break;
				}
			}
		}
		return info;
	}

	/// <summary>下载并校验 zip，返回本地路径。</summary>
	public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> progress)
	{
		if (info.ZipUrl == null)
		{
			throw new InvalidDataException("这个版本没有可下载的安装包");
		}
		var uri = new Uri(info.ZipUrl);
		if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("下载地址不属于项目仓库");
		}
		if (info.Sha256 == null)
		{
			throw new InvalidDataException("缺少 SHA-256 校验值");
		}
		string work = WorkDir(info.Version);
		string zip = Path.Combine(work, "update.zip");
		using (HttpResponseMessage response = await Http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
		{
			response.EnsureSuccessStatusCode();
			long total = response.Content.Headers.ContentLength ?? -1;
			using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using FileStream output = File.Create(zip);
			var buffer = new byte[81920];
			long read = 0;
			int n;
			while ((n = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
			{
				await output.WriteAsync(buffer, 0, n).ConfigureAwait(false);
				read += n;
				if (total > 0)
				{
					progress?.Report((int)(read * 100 / total));
				}
			}
		}
		string actual;
		using (var sha = SHA256.Create())
		using (FileStream file = File.OpenRead(zip))
		{
			actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
		}
		if (actual != info.Sha256)
		{
			File.Delete(zip);
			throw new InvalidDataException("SHA-256 校验失败，下载的文件可能不完整");
		}
		return zip;
	}

	/// <summary>程序文件夹能不能写（放在 Program Files 里时不能自动更新）。</summary>
	public static bool CanWriteAppDir()
	{
		try
		{
			string probe = Path.Combine(AppDir, ".update-probe-" + Guid.NewGuid().ToString("N"));
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>解压并启动更新脚本。调用后程序要马上退出，脚本等进程结束后复制文件并重新打开程序。</summary>
	public static void StartInstall(string zip)
	{
		string work = Path.GetDirectoryName(zip);
		string extracted = Path.Combine(work, "files");
		if (Directory.Exists(extracted))
		{
			Directory.Delete(extracted, true);
		}
		ZipFile.ExtractToDirectory(zip, extracted);
		// zip 里只有一个顶层文件夹时（如 EHUploader\...），取它里面的内容
		string source = extracted;
		if (Directory.GetFiles(source).Length == 0 && Directory.GetDirectories(source).Length == 1)
		{
			source = Directory.GetDirectories(source)[0];
		}
		string exe = Process.GetCurrentProcess().MainModule.FileName;
		if (!File.Exists(Path.Combine(source, Path.GetFileName(exe))))
		{
			throw new InvalidDataException("安装包里没有 " + Path.GetFileName(exe));
		}
		string script = Path.Combine(work, "update.ps1");
		File.WriteAllText(script, Script, new UTF8Encoding(true));
		string args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script)
			+ " -ProcId " + Process.GetCurrentProcess().Id + " -Source " + Quote(source) + " -Target " + Quote(AppDir) + " -Exe " + Quote(exe) + " -Log " + Quote(Path.Combine(work, "update.log"));
		Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work });
	}

	static string WorkDir(string version)
	{
		string dir = Path.Combine(Path.GetTempPath(), AppName + "-update-" + version);
		Directory.CreateDirectory(dir);
		return dir;
	}

	static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

	static string Str(Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) ? v as string : null;

	static int[] Parse(string v)
	{
		string[] parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.');
		if (parts.Length < 2)
		{
			return null;
		}
		var nums = new int[3];
		for (int i = 0; i < Math.Min(3, parts.Length); i++)
		{
			if (!int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out nums[i]))
			{
				return null;
			}
		}
		return nums;
	}

	static int Compare(int[] a, int[] b)
	{
		b ??= new int[3];
		for (int i = 0; i < 3; i++)
		{
			if (a[i] != b[i])
			{
				return a[i].CompareTo(b[i]);
			}
		}
		return 0;
	}

	// 等旧进程退出 → 文件夹整个替换、文件逐个覆盖（被占用时重试）→ 重新打开程序。出错写进 update.log，仍然尝试打开程序。
	const string Script = @"param([int]$ProcId, [string]$Source, [string]$Target, [string]$Exe, [string]$Log)
$ErrorActionPreference = 'Stop'
function Retry([scriptblock]$Action) {
  for ($i = 0; ; $i++) {
    try { & $Action; return } catch { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 500 }
  }
}
try {
  try { Wait-Process -Id $ProcId -Timeout 120 } catch { }
  Start-Sleep -Milliseconds 500
  foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
    $dest = Join-Path $Target $item.Name
    if ($item.PSIsContainer) {
      if (Test-Path -LiteralPath $dest) { Retry { Remove-Item -LiteralPath $dest -Recurse -Force } }
      Retry { Copy-Item -LiteralPath $item.FullName -Destination $dest -Recurse -Force }
    } else {
      Retry { Copy-Item -LiteralPath $item.FullName -Destination $dest -Force }
    }
  }
  'ok ' + (Get-Date) | Out-File -LiteralPath $Log -Encoding utf8
} catch {
  ('failed ' + (Get-Date) + ': ' + $_) | Out-File -LiteralPath $Log -Encoding utf8
}
Start-Process -FilePath $Exe -WorkingDirectory $Target
";
}
