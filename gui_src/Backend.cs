using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EHUploader;

/// <summary>Runs eh_upload.py. Lines starting with "@@" are JSON events, everything else is log text.</summary>
public sealed class Backend
{
	private Process _proc;

	public string Python { get; set; } = "python";

	public string Script { get; }

	public bool Running => _proc != null && !_proc.HasExited;

	public Backend(string script)
	{
		Script = script;
	}

	/// <summary>所有还在运行的脚本进程；程序退出时一起结束，不然扫描之类的会变成后台孤儿进程继续跑。</summary>
	private static readonly ConcurrentDictionary<Backend, byte> Active = new ConcurrentDictionary<Backend, byte>();

	public static void StopAll()
	{
		foreach (Backend b in Active.Keys.ToList())
		{
			b.Stop();
		}
	}

	public static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

	/// <summary>Starts the script; callbacks arrive on a background thread.</summary>
	public void Start(IEnumerable<string> args, Action<Dictionary<string, object>> onEvent, Action<string> onLog, Action<int> onExit)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName = Python,
			Arguments = "-u " + Quote(Script) + " " + string.Join(" ", args.Select(Quote)),
			WorkingDirectory = Path.GetDirectoryName(Script),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
		psi.EnvironmentVariables["PYTHONUTF8"] = "1";
		Process p = new Process { StartInfo = psi, EnableRaisingEvents = true };
		DataReceivedEventHandler handler = (s, e) =>
		{
			if (e.Data == null)
			{
				return;
			}
			if (e.Data.StartsWith("@@"))
			{
				Dictionary<string, object> ev = null;
				try
				{
					ev = Json.Parse(e.Data.Substring(2));
				}
				catch
				{
				}
				if (ev != null)
				{
					onEvent(ev);
					return;
				}
			}
			onLog(e.Data);
		};
		p.OutputDataReceived += handler;
		p.ErrorDataReceived += handler;
		p.Exited += (s, e) =>
		{
			p.WaitForExit();
			Active.TryRemove(this, out _);
			onExit(p.ExitCode);
		};
		p.Start();
		p.BeginOutputReadLine();
		p.BeginErrorReadLine();
		_proc = p;
		Active[this] = 0;
	}

	/// <summary>Runs the script to the end and returns the last event of the given kind.</summary>
	public Task<(Dictionary<string, object> ev, string log, int code)> RunAsync(IEnumerable<string> args, string wanted, Action<Dictionary<string, object>> onOther = null)
	{
		TaskCompletionSource<(Dictionary<string, object>, string, int)> tcs = new TaskCompletionSource<(Dictionary<string, object>, string, int)>();
		Dictionary<string, object> found = null;
		StringBuilder log = new StringBuilder();
		Backend b = new Backend(Script) { Python = Python };
		try
		{
			// fatal 也当作结果返回（调用方看 ev 字段区分），否则拿不到出错原因
			b.Start(args, ev => { string k = Json.Str(ev, "ev"); if (k == wanted || (k == "fatal" && found == null)) found = ev; else onOther?.Invoke(ev); }, line => { lock (log) log.AppendLine(line); },
				code => tcs.TrySetResult((found, log.ToString(), code)));
		}
		catch (Exception ex)
		{
			tcs.TrySetResult((null, "无法启动 Python：" + ex.Message, -1));
		}
		return tcs.Task;
	}

	/// <summary>Stops the script together with the browser it started.</summary>
	public void Stop()
	{
		if (!Running)
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + _proc.Id) { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
		}
		catch
		{
		}
	}
}

/// <summary>Cover thumbnails: the first image (natural order) of each cbz, decoded small.</summary>
public static class Thumbnails
{
	private static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };
	private static readonly ConcurrentDictionary<string, ImageSource> Cache = new ConcurrentDictionary<string, ImageSource>();
	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(4);

	/// <summary>Cover of a cbz; <paramref name="entry"/> picks a specific page (the first page of a split-out story).</summary>
	public static async Task<ImageSource> LoadAsync(string cbz, int width, string entry = null)
	{
		string key;
		try
		{
			key = cbz + "|" + entry + "|" + File.GetLastWriteTimeUtc(cbz).Ticks;
		}
		catch
		{
			return null;
		}
		if (Cache.TryGetValue(key, out ImageSource hit))
		{
			return hit;
		}
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			ImageSource img = await Task.Run(() => Decode(cbz, width, entry)).ConfigureAwait(false);
			if (img != null)
			{
				Cache[key] = img;
			}
			return img;
		}
		finally
		{
			Gate.Release();
		}
	}

	private static ImageSource Decode(string cbz, int width, string entry)
	{
		try
		{
			using ZipArchive zip = ZipFile.OpenRead(cbz);
			ZipArchiveEntry first = (string.IsNullOrEmpty(entry) ? null : zip.GetEntry(entry)) ?? zip.Entries
				.Where(e => Exts.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))
				.OrderBy(e => e.FullName, NaturalComparer.Instance)
				.FirstOrDefault();
			if (first == null)
			{
				return null;
			}
			MemoryStream ms = new MemoryStream();
			using (Stream s = first.Open())
			{
				s.CopyTo(ms);
			}
			ms.Position = 0;
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.DecodePixelWidth = width;
			bmp.StreamSource = ms;
			bmp.EndInit();
			bmp.Freeze();
			return bmp;
		}
		catch
		{
			return null;
		}
	}
}
