using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace EHUploader;

public partial class App : Application
{
	public App()
	{
		// 界面里的异常不要直接闪退：写进 logs\crash.log，并提示出错原因
		DispatcherUnhandledException += OnUnhandled;
	}

	private static Mutex _single;

	/// <summary>再次打开程序时，新进程通过它通知已经在运行的窗口显示出来（比如缩在托盘里时）。</summary>
	public static EventWaitHandle ShowSignal { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		// 只允许开一个：两个窗口会同时后台刷新评论、互相覆盖已读记录。调试截图（--snapshot）不受限制
		if (!e.Args.Contains("--snapshot"))
		{
			_single = new Mutex(true, @"Local\EHUploader_single", out bool first);
			ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\EHUploader_show");
			if (!first)
			{
				ShowSignal.Set();
				Environment.Exit(0);
			}
		}
		base.OnStartup(e);
	}

	private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		try
		{
			string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs");
			Directory.CreateDirectory(dir);
			File.AppendAllText(Path.Combine(dir, "crash.log"), $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
		}
		catch
		{
		}
		Exception inner = e.Exception;
		while (inner.InnerException != null)
		{
			inner = inner.InnerException;
		}
		MessageBox.Show("程序遇到一个错误，已经记录到 logs\\crash.log：\n\n" + inner.Message, "EH 上传助手", MessageBoxButton.OK, MessageBoxImage.Error);
		e.Handled = true;
	}
}
