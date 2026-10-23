using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace EHUploader;

/// <summary>Main window (WPF-UI): works list with filters, upload progress, upload history, settings.</summary>
public partial class MainWindow : FluentWindow
{
	private static readonly string[] PageNames = { "作品列表", "上传进度", "上传记录", "已上传", "设置" };

	private readonly string _dir;
	private readonly string _settingsPath;
	private readonly GuiSettings _gs;
	private readonly Backend _backend;
	private readonly FrameworkElement[] _pages;
	private readonly ObservableCollection<WorkItem> _works = new ObservableCollection<WorkItem>();
	private readonly ObservableCollection<WorkItem> _queue = new ObservableCollection<WorkItem>();
	private readonly StringBuilder _log = new StringBuilder();
	private readonly DispatcherTimer _tintSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

	private int _page;
	private bool _ready;
	private bool _watching;
	private bool _scanning;
	private bool _uploading;
	private bool _finishedEvent;
	private int _ok;
	private int _failed;
	private WorkItem _current;
	private string _fatal;

	public MainWindow()
	{
		InitializeComponent();
		AppTitleBar.Title = Title = "EH 上传助手 v" + AppVersion;
		_dir = FindScriptDir();
		_settingsPath = Path.Combine(_dir, "eh_gui.json");
		_gs = GuiSettings.Load(_settingsPath);
		_backend = new Backend(Path.Combine(_dir, "eh_upload.py")) { Python = _gs.Python };
		_pages = new FrameworkElement[] { PageWorks, PageProgress, PageHistory, PageMine, PageSettings };
		_mineBackend = new Backend(_backend.Script);
		_seenPath = Path.Combine(_dir, "eh_seen.json");
		_seen = LoadSeen();
		MineList.ItemsSource = _mine;
		WorkList.ItemsSource = _works;
		QueueList.ItemsSource = _queue;

		_gs.Root ??= "";
		RefillDate.SelectedDate = DateTime.TryParse(_gs.RefilledAfter, out DateTime d) ? d : DateTime.Today.AddDays(-7);
		MinMonths.Value = _gs.MinMonths;
		RootPath.Text = _gs.Root;
		PythonPath.Text = _gs.Python;
		ThemeBox.SelectedIndex = _gs.Theme == "light" ? 1 : (_gs.Theme == "dark" ? 2 : 0);
		BackdropBox.SelectedIndex = _gs.Backdrop == "acrylic" ? 1 : (_gs.Backdrop == "none" ? 2 : 0);
		TintSlider.Value = _gs.TintPercent;
		FilterBox.SelectedIndex = Math.Max(0, Math.Min(5, _gs.Filter));
		OnlyNew.IsChecked = _gs.OnlyNewComments;
		ViewBox.SelectedIndex = Math.Max(0, Math.Min(2, _gs.View));

		_tintSave.Tick += delegate
		{
			_tintSave.Stop();
			SaveGui();
		};
		ApplicationThemeManager.Changed += OnThemeChanged;
		Closed += delegate { ApplicationThemeManager.Changed -= OnThemeChanged; };
		CloseToTray.IsChecked = _gs.CloseToTray;
		InitTray();
		InitUpdateCheck();
		UpdateOverall();
		ApplyAppearance();
		_ready = true;
		RefreshFilter();
		SelectPage(0);
		UpdateSide();
		UpdateUploadButton();

		Loaded += async delegate
		{
			if (App.ShowSignal != null)
			{
				// 用户又双击了 exe：新进程会通知这里，把已经在运行的窗口（可能在托盘里）显示出来
				new System.Threading.Thread(() =>
				{
					while (App.ShowSignal.WaitOne())
					{
						Dispatcher.BeginInvoke((Action)ShowFromTray);
					}
				}) { IsBackground = true }.Start();
			}
			await LoadConfig();
			RefreshHistory();
			if (File.Exists(_backend.Script))
			{
				await Scan();
			}
			else
			{
				Summary.Text = "找不到 eh_upload.py，请把 EHUploader.exe 放在 eh_upload 文件夹里。";
			}
			string[] cmd = Environment.GetCommandLineArgs();
			int at = Array.IndexOf(cmd, "--snapshot");
			if (at > 0 && at + 1 < cmd.Length)
			{
				await Snapshot(cmd[at + 1]);
				return;
			}
			// 评论在后台自动检查：启动后查一次，之后每 15 分钟一次（最小化到托盘时也照常查），不用点进「已上传」
			if (_gs.LoginOk == true)
			{
				LoadMine("smart");
			}
			_mineTimer.Tick += delegate
			{
				if (_gs.LoginOk == true && (_mineLoadedAt == null || _mineStale || DateTime.Now - _mineLoadedAt >= MineInterval))
				{
					LoadMine("smart");
					_mineStale = false;
				}
			};
			_mineTimer.Start();
		};
		Closing += OnClosing;
	}

	/// <summary>The folder with eh_upload.py: next to the exe, or one of its parents (during development).</summary>
	private static string FindScriptDir()
	{
		string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
		for (string d = dir; d != null; d = Directory.GetParent(d)?.FullName)
		{
			if (File.Exists(Path.Combine(d, "eh_upload.py")))
			{
				return d;
			}
		}
		return dir;
	}

	/// <summary>Debug aid: renders every page to PNG files in the given folder, then closes.</summary>
	/// <summary>Debug aid: renders the window content to a PNG (on a dark background, since the Mica backdrop isn't captured).</summary>
	private void RenderTo(string file)
	{
		FrameworkElement root = (FrameworkElement)Content;
		root.UpdateLayout();
		System.Windows.Media.Matrix m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
		System.Windows.Media.Imaging.RenderTargetBitmap rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
			(int)(root.ActualWidth * m.M11), (int)(root.ActualHeight * m.M22), 96 * m.M11, 96 * m.M22, System.Windows.Media.PixelFormats.Pbgra32);
		System.Windows.Media.DrawingVisual bg = new System.Windows.Media.DrawingVisual();
		using (System.Windows.Media.DrawingContext dc = bg.RenderOpen())
		{
			dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
		}
		rtb.Render(bg);
		rtb.Render(root);
		System.Windows.Media.Imaging.PngBitmapEncoder enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
		enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(file);
		enc.Save(fs);
	}

	private async Task Snapshot(string folder)
	{
		Directory.CreateDirectory(folder);
		await Task.Delay(1500);
		// 作品列表的三种视图都渲染一遍（网格 / 紧凑列表 / 大封面），模板有问题时能马上发现
		int view = _gs.View;
		SelectPage(0);
		for (int v = 0; v < 3; v++)
		{
			_gs.View = v;
			RefreshFilter();
			await Task.Delay(800);
			RenderTo(Path.Combine(folder, $"works_view{v}.png"));
		}
		_gs.View = view;
		RefreshFilter();
		// 上传进度页：放几条示例队列，分别渲染「队列」和「运行日志」两个标签
		if (_queue.Count == 0 && _works.Count >= 3)
		{
			foreach (WorkItem w in _works.Take(3))
			{
				w.QueueReset();
				_queue.Add(w);
			}
			_queue[0].QueueFailed("失败：示例");
			_queue[1].QueueRunning("创建画廊并上传第 1/2 批");
			CurrentCard.Visibility = Visibility.Visible;
			CurrentTitle.Text = "(2/3) " + _queue[1].Title;
			CurrentStage.Text = "创建画廊并上传第 1/2 批";
			CurrentDetail.Text = "";
			SetCurrentPercent(0);
			for (int k = 0; k < 30; k++)
			{
				AppendLog($"[02:0{k % 10}:00] 示例日志第 {k + 1} 行");
			}
			_uploading = true;
			UpdateOverall();
			SelectPage(1);
			await Task.Delay(800);
			RenderTo(Path.Combine(folder, "progress_split.png"));
			LogToggle_Click(LogToggle, null);
			await Task.Delay(500);
			RenderTo(Path.Combine(folder, "progress_logcollapsed.png"));
			LogToggle_Click(LogToggle, null);
			_uploading = false;
			_queue.Clear();
			CurrentCard.Visibility = Visibility.Collapsed;
			UpdateOverall();
		}
		for (int i = 0; i < _pages.Length; i++)
		{
			SelectPage(i);
			await Task.Delay(800);
			if (i == 3 && _mine.Count == 0)
			{
				// 调试截图：没登录时放一个示例画廊看排版
				GalleryItem demo = new GalleryItem { Gid = 1, Title = "[サンプル作者] サンプル作品 [中国翻訳]", En = "[Sample Artist] Sample Work [Chinese]", Info = "发布 2026-01-01 · 24 页 · 评分 4.5 · Doujinshi · 本工具上传" };
				_seen["1"] = 2;
				demo.SetComments(new object[]
				{
					new Dictionary<string, object> { ["id"] = "4", ["author"] = "user_c", ["posted"] = "2026-01-10T22:47", ["score"] = "+13", ["text"] = "示例评论：第 12 页好像缺了一张" },
					new Dictionary<string, object> { ["id"] = "3", ["author"] = "user_b", ["posted"] = "2026-01-03T08:41", ["score"] = "+6", ["text"] = "示例评论：感谢翻译" },
					new Dictionary<string, object> { ["id"] = "2", ["author"] = "user_a", ["posted"] = "2026-01-01T22:40", ["score"] = "+58", ["text"] = "示例评论：这是一条用于检查排版的长评论，看看换行和对齐是否正常" },
				}, 5, "2026-01-12T07:30", _seen);
				demo.EditorOpen = true;
				_mine.Add(demo);
				UpdateMineSummary();
				await Task.Delay(500);
			}
			if (i == 3)
			{
				// 侧边栏的新评论数字也渲染出来，检查对齐
				MineNewBadge.Visibility = Visibility.Visible;
				MineNewText.Text = Environment.GetEnvironmentVariable("EHUP_BADGE") ?? "2";
				await Task.Delay(300);
			}
			RenderTo(Path.Combine(folder, $"page{i}.png"));
		}
		WorkItem partial = _works.FirstOrDefault(w => w.Partial);
		if (partial != null)
		{
			EditMetaWindow dlg = new EditMetaWindow(partial) { Owner = this };
			dlg.Show();
			await Task.Delay(1000);
			FrameworkElement r = (FrameworkElement)dlg.Content;
			System.Windows.Media.Matrix dm = PresentationSource.FromVisual(dlg).CompositionTarget.TransformToDevice;
			System.Windows.Media.Imaging.RenderTargetBitmap bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
				(int)(r.ActualWidth * dm.M11), (int)(r.ActualHeight * dm.M22), 96 * dm.M11, 96 * dm.M22, System.Windows.Media.PixelFormats.Pbgra32);
			System.Windows.Media.DrawingVisual bg = new System.Windows.Media.DrawingVisual();
			using (System.Windows.Media.DrawingContext dc = bg.RenderOpen())
			{
				dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, r.ActualWidth, r.ActualHeight));
			}
			bmp.Render(bg);
			bmp.Render(r);
			System.Windows.Media.Imaging.PngBitmapEncoder pe = new System.Windows.Media.Imaging.PngBitmapEncoder();
			pe.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
			using (FileStream fs = File.Create(Path.Combine(folder, "edit.png")))
			{
				pe.Save(fs);
			}
			dlg.Close();
		}
		_exiting = true;
		Close();
	}

	// ---- navigation ----

	public void SelectPage(int idx)
	{
		_page = idx;
		for (int i = 0; i < _pages.Length; i++)
		{
			_pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
		}
		int n = 0;
		foreach (object child in Nav.Children)
		{
			if (child is Wpf.Ui.Controls.Button b)
			{
				b.Appearance = n == idx ? ControlAppearance.Secondary : ControlAppearance.Transparent;
				n++;
			}
		}
		Header.Text = PageNames[idx];
		FooterWorks.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
		FooterProgress.Visibility = idx == 1 && _uploading ? Visibility.Visible : Visibility.Collapsed;
		FooterSettings.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
		FooterStatus.Text = "";
		if (idx == 2)
		{
			RefreshHistory();
		}
		if (idx == 3 && (_mineLoadedAt == null || _mineStale || DateTime.Now - _mineLoadedAt > TimeSpan.FromMinutes(15)))
		{
			// 先显示缓存；画廊资料只给新画廊读，评论按热度只查到时间的
			LoadMine("smart");
			_mineStale = false;
		}
	}

	private void Nav_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.Tag != null)
		{
			SelectPage(int.Parse(fe.Tag.ToString()));
		}
	}

	private void UpdateSide()
	{
		string login = _gs.LoginOk == true ? "已登录" : (_gs.LoginOk == false ? "未登录" : "未检测");
		SideStatus.Text = "e-hentai：" + login + (string.IsNullOrEmpty(_gs.LoginChecked) ? "" : "\n（" + _gs.LoginChecked + " 检测）");
		LoginState.Text = _gs.LoginOk == true
			? "已登录（" + _gs.LoginChecked + " 检测）。登录信息保存在独立的浏览器资料里，只需登录一次"
			: "用独立的浏览器窗口登录一次，之后上传都会沿用";
	}

	// ---- works list ----

	private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();

	/// <summary>程序版本号（来自 EHUploader.csproj 的 Version）。</summary>
	public static string AppVersion
	{
		get
		{
			Version v = typeof(MainWindow).Assembly.GetName().Version;
			return $"{v.Major}.{v.Minor}.{v.Build}";
		}
	}

	private async Task Scan()
	{
		if (_scanning || _uploading)
		{
			return;
		}
		if (string.IsNullOrEmpty(_gs.Root) || !Directory.Exists(_gs.Root))
		{
			Summary.Text = "请先在「设置」页选择漫画根目录（放回填好的作品的文件夹），再点扫描。";
			return;
		}
		_scanning = true;
		ScanButton.IsEnabled = false;
		ScanRing.Visibility = Visibility.Visible;
		Summary.Text = "正在扫描并查询原画廊信息…";
		_gs.RefilledAfter = RefillDate.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
		SaveGui();
		List<string> args = new List<string> { "scan", "--json" };
		if (RefillDate.SelectedDate.HasValue)
		{
			args.Add("--refilled-after");
			args.Add(RefillDate.SelectedDate.Value.ToString("yyyy-MM-dd"));
		}
		args.Add(_gs.Root);
		_backend.Python = _gs.Python;
		(Dictionary<string, object> ev, string log, int code) = await _backend.RunAsync(args, "scan",
			e => { if (Json.Str(e, "ev") == "scan_progress") Dispatcher.BeginInvoke((Action)(() => { if (_scanning) Summary.Text = Json.Str(e, "text"); })); });
		_scanning = false;
		ScanButton.IsEnabled = true;
		ScanRing.Visibility = Visibility.Collapsed;
		if (ev == null)
		{
			Summary.Text = "扫描失败：" + LastLine(log);
			AppendLog(log);
			return;
		}
		_works.Clear();
		double min = MinMonths.Value ?? 0;
		foreach (WorkItem w in Json.List(ev, "items").OfType<Dictionary<string, object>>().Select(WorkItem.FromJson).OrderByDescending(w => w.Months))
		{
			w.Evaluate(min, resetSelection: true);
			_works.Add(w);
			LoadThumb(w);
		}
		RefreshFilter();
		UpdateSummary();
		WarnNotices();
	}

	/// <summary>扫描后：原画廊评论里有禁止、勿扰、预定（或组内、翻译、AI）的作品，弹窗提示。</summary>
	private void WarnNotices()
	{
		if (Environment.GetCommandLineArgs().Contains("--snapshot"))
		{
			return;
		}
		// 同一条原评论只弹一次；之后只在作品卡片上标出来，原画廊有了新的限制评论才会再弹
		_gs.NoticesShown ??= new List<string>();
		HashSet<string> shown = new HashSet<string>(_gs.NoticesShown);
		string NoticeKey(WorkItem w, Dictionary<string, object> n) => w.Gid + ":" + Json.Str(n, "id");
		List<WorkItem> hit = _works.Where(w => !w.Done && !w.Excluded && w.PendingNotices.Any(n => !shown.Contains(NoticeKey(w, n))))
			.GroupBy(w => w.Gid).Select(g => g.First()).ToList();
		if (hit.Count == 0)
		{
			return;
		}
		_gs.NoticesShown.AddRange(hit.SelectMany(w => w.PendingNotices.Select(n => NoticeKey(w, n))).Where(k => !shown.Contains(k)).Distinct());
		SaveGui();
		IEnumerable<string> lines = hit.Take(12).Select(w =>
		{
			Dictionary<string, object> n = w.PendingNotices.OrderBy(x => Json.Str(x, "level") == "block" ? 0 : 1).First();
			string snippet = Json.Str(n, "snippet");
			return $"· {w.Title}\n   【{(Json.Str(n, "level") == "block" ? "拦截" : "待确认")}：{Json.Str(n, "keyword")}】{Json.Str(n, "author")}：{(snippet.Length > 60 ? snippet.Substring(0, 60) + "…" : snippet)}";
		});
		int block = hit.Count(w => w.HasBlockNotice);
		MessageBox.Show(this,
			$"有 {hit.Count} 部作品的原画廊评论里有限制或需要确认的内容（{block} 部是禁止 / 勿扰 / 预定之类），已暂时不能勾选上传：\n\n"
			+ string.Join("\n", lines) + (hit.Count > 12 ? $"\n……还有 {hit.Count - 12} 部" : "")
			+ "\n\n点作品卡片上的铅笔图标可以看完整的原评论，确认可以上传后勾选放行。",
			"原画廊评论提示", MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	private async void LoadThumb(WorkItem w)
	{
		System.Windows.Media.ImageSource img = await Thumbnails.LoadAsync(w.Cbz, 168, w.ThumbEntry);
		if (img != null)
		{
			await Dispatcher.BeginInvoke((Action)(() => w.Thumb = img));
		}
	}

	private void UpdateSummary()
	{
		if (_works.Count == 0)
		{
			Summary.Text = "这个日期之后没有回填的作品。";
			UpdateUploadButton();
			return;
		}
		double min = MinMonths.Value ?? 0;
		int eligible = _works.Count(w => w.Eligible);
		int excluded = _works.Count(w => w.Excluded);
		int problems = _works.Count(w => w.Problems.Count > 0 && !w.Done && !w.Excluded);
		int done = _works.Count(w => w.Done && !w.Excluded);
		int selected = _works.Count(w => w.Selected);
		Summary.Text = $"待上传 {_works.Count - excluded - done} 部 · 原作满 {min:0.#} 个月的 {eligible} 部 · 已勾选 {selected} 部"
			+ (problems > 0 ? $" · 需处理 {problems} 部" : "") + (done > 0 ? $" · 已上传 {done} 部" : "")
			+ (excluded > 0 ? $" · 已排除 {excluded} 部" : "");
		UpdateUploadButton();
	}

	private void UpdateUploadButton()
	{
		int n = _works.Count(w => w.Selected);
		UploadButton.Content = n > 0 ? $"开始上传（{n} 部）" : "开始上传";
		UploadButton.IsEnabled = n > 0 && !_uploading;
	}

	private void MinMonths_ValueChanged(object sender, RoutedEventArgs e)
	{
		if (!_ready)
		{
			return;
		}
		double min = MinMonths.Value ?? 0;
		foreach (WorkItem w in _works)
		{
			w.Evaluate(min, resetSelection: true);
		}
		_gs.MinMonths = min;
		SaveGui();
		RefreshFilter();
		UpdateSummary();
	}

	private bool PassesFilter(object o)
	{
		if (o is not WorkItem w)
		{
			return true;
		}
		// 永久排除的作品只在「只看已排除」里出现
		if (FilterBox.SelectedIndex == 5)
		{
			return w.Excluded;
		}
		if (w.Excluded)
		{
			return false;
		}
		// 已上传完成的作品同样默认隐藏，只在「只看已上传」里出现
		if (FilterBox.SelectedIndex == 4)
		{
			return w.Done;
		}
		if (w.Done)
		{
			return false;
		}
		return FilterBox.SelectedIndex switch
		{
			1 => w.Eligible,
			2 => w.Selected,
			3 => w.Problems.Count > 0,
			_ => true,
		};
	}

	private void Filter_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready)
		{
			return;
		}
		_gs.Filter = FilterBox.SelectedIndex;
		SaveGui();
		RefreshFilter();
	}

	private void View_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready)
		{
			return;
		}
		_gs.View = ViewBox.SelectedIndex;
		SaveGui();
		RefreshFilter();
	}

	/// <summary>Grid view: 2 columns, 3 when the list is wide enough.</summary>
	private int GridColumns => Math.Max(1, Math.Min(4, (int)((WorkList.ActualWidth - 16) / 400)));

	private int _shownColumns;

	/// <summary>Rebuilds what the list shows from the filter and the view mode (grid rows keep the list virtualized).</summary>
	private void RefreshFilter()
	{
		List<WorkItem> items = _works.Where(PassesFilter).ToList();
		if (_gs.View == 0)
		{
			int cols = GridColumns;
			_shownColumns = cols;
			List<WorkRow> rows = new List<WorkRow>();
			for (int i = 0; i < items.Count; i += cols)
			{
				rows.Add(new WorkRow { Columns = cols, Items = items.Skip(i).Take(cols).ToList() });
			}
			WorkList.ItemTemplate = (DataTemplate)FindResource("GridRow");
			WorkList.ItemsSource = rows;
		}
		else
		{
			_shownColumns = 0;
			WorkList.ItemTemplate = (DataTemplate)FindResource(_gs.View == 2 ? "LargeRow" : "CompactRow");
			WorkList.ItemsSource = items;
		}
	}

	private void WorkList_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (_ready && _gs.View == 0 && GridColumns != _shownColumns)
		{
			RefreshFilter();
		}
	}

	/// <summary>Edits the title / tags of one work (upload_meta.json), then re-scans just that work.</summary>
	private async void EditMeta_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement fe || fe.DataContext is not WorkItem w || _uploading)
		{
			return;
		}
		if (new EditMetaWindow(w) { Owner = this }.ShowDialog() != true)
		{
			return;
		}
		(Dictionary<string, object> ev, string log, _) = await _backend.RunAsync(new[] { "scan", "--json", w.Folder }, "scan");
		List<WorkItem> fresh = ev == null ? new List<WorkItem>()
			: Json.List(ev, "items").OfType<Dictionary<string, object>>().Select(WorkItem.FromJson).ToList();
		if (fresh.Count == 0)
		{
			MessageBox.Show(this, "已保存，但重新读取这部作品失败：" + LastLine(log), "EH 上传助手");
			return;
		}
		// 同一个文件夹可能拆成了多篇，整组替换
		int at = _works.IndexOf(w);
		List<WorkItem> old = _works.Where(x => x.Folder == w.Folder).ToList();
		foreach (WorkItem x in old)
		{
			_works.Remove(x);
		}
		at = Math.Max(0, Math.Min(at < 0 ? _works.Count : at, _works.Count));
		foreach (WorkItem f in fresh)
		{
			WorkItem prev = old.FirstOrDefault(x => x.Key == f.Key);
			f.Thumb = prev?.Thumb;
			f.Evaluate(MinMonths.Value ?? 0, resetSelection: true);
			_works.Insert(at++, f);
			if (f.Thumb == null)
			{
				LoadThumb(f);
			}
		}
		RefreshFilter();
		UpdateSummary();
	}

	/// <summary>点标题：在浏览器里打开画廊（作品列表/队列：已发布的画廊或原画廊；已上传页：画廊本身）。</summary>
	private void Title_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		e.Handled = true;   // 不触发卡片的勾选
		string url = (sender as FrameworkElement)?.DataContext switch
		{
			WorkItem w => w.LinkUrl,
			GalleryItem g => g.Url,
			_ => null,
		};
		if (!string.IsNullOrEmpty(url))
		{
			try
			{
				Process.Start(new ProcessStartInfo(ToEx(url)) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "打不开：" + ex.Message, "EH 上传助手");
			}
		}
	}

	/// <summary>右键菜单「永久排除 / 取消排除」：记在 excluded.json，排除的作品不再显示、不会上传。</summary>
	private void Exclude_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not WorkItem w)
		{
			return;
		}
		string path = Path.Combine(_dir, "excluded.json");
		Dictionary<string, object> data;
		try
		{
			data = File.Exists(path) ? Json.Parse(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
		}
		catch
		{
			data = new Dictionary<string, object>();
		}
		if (w.Excluded)
		{
			data.Remove(w.Key);
			data.Remove(w.Gid.ToString());
		}
		else
		{
			data[w.Key] = new Dictionary<string, object> { ["title"] = string.IsNullOrEmpty(w.Jp) ? w.En : w.Jp, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
		}
		File.WriteAllText(path, Json.Serialize(data), new UTF8Encoding(false));
		w.Excluded = !w.Excluded;
		w.Evaluate(MinMonths.Value ?? 0, resetSelection: !w.Excluded);
		FooterStatus.Text = w.Excluded ? $"已永久排除「{w.Title}」，以后不再显示和上传；筛选里选「只看已排除」可以找回" : $"已取消排除「{w.Title}」";
		RefreshFilter();
		UpdateSummary();
	}

	/// <summary>右键菜单「复制…」：Tag 指明复制哪一项。</summary>
	private void Copy_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem mi)
		{
			return;
		}
		string what = mi.Tag as string;
		string text = mi.DataContext switch
		{
			WorkItem w => what switch
			{
				"jp" => w.Jp,
				"en" => w.En,
				"author" => w.AuthorName,
				"circle" => w.CircleName,
				"link" => ToEx(w.LinkUrl),
				_ => null,
			},
			GalleryItem g => what switch
			{
				"jp" => g.Title,
				"en" => string.IsNullOrEmpty(g.En) ? g.Title : g.En,
				"link" => ToEx(g.Url),
				_ => null,
			},
			CommentItem c => what == "commenter" ? c.Author : c.Text,
			_ => null,
		};
		if (string.IsNullOrEmpty(text))
		{
			FooterStatus.Text = "这一项是空的，没有可复制的内容";
			return;
		}
		for (int i = 0; i < 5; i++)
		{
			try
			{
				Clipboard.SetText(text);
				FooterStatus.Text = "已复制：" + (text.Length > 40 ? text.Substring(0, 40) + "…" : text);
				return;
			}
			catch (System.Runtime.InteropServices.COMException)
			{
				System.Threading.Thread.Sleep(50);   // 剪贴板被别的程序占用，稍等重试
			}
		}
		FooterStatus.Text = "复制失败：剪贴板被其他程序占用";
	}

	/// <summary>Clicking the empty part of a card toggles its selection.</summary>
	private void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		// 点到标题里的文字片段（Run）时它不在可视树上，要先沿逻辑树找到所在的 TextBlock
		for (DependencyObject d = e.OriginalSource as DependencyObject; d != null && d != sender;
			d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
		{
			if (d is System.Windows.Controls.Primitives.ButtonBase)
			{
				return;
			}
		}
		if (sender is FrameworkElement fe && fe.DataContext is WorkItem w && w.CanSelect)
		{
			w.Selected = !w.Selected;
			UpdateSummary();
		}
	}

	private void SelectEligible_Click(object sender, RoutedEventArgs e)
	{
		foreach (WorkItem w in _works)
		{
			w.Selected = w.Eligible;
		}
		UpdateSummary();
	}

	private void SelectNone_Click(object sender, RoutedEventArgs e)
	{
		foreach (WorkItem w in _works)
		{
			w.Selected = false;
		}
		UpdateSummary();
	}

	private void ItemCheck_Click(object sender, RoutedEventArgs e) => UpdateSummary();

	/// <summary>上传前：给勾选的作品统一加标签，记在各自的 upload_meta.json（add_tags）里，上传时一起加上。</summary>
	private async void AddTagsToSelected_Click(object sender, RoutedEventArgs e)
	{
		List<WorkItem> chosen = _works.Where(w => w.Selected).ToList();
		if (chosen.Count == 0)
		{
			MessageBox.Show(this, "先在作品列表里勾选要加标签的作品。", "给勾选的加标签");
			return;
		}
		TagBatchWindow dlg = new TagBatchWindow("给勾选的加标签",
			$"给勾选的 {chosen.Count} 部作品加上下面的标签，上传时和原画廊的标签一起加上；已经有的不会重复。记在每部作品自己的 upload_meta.json 里，打开编辑窗口能看到。",
			"加到勾选的作品", null, _gs.LastBatchTags) { Owner = this };
		if (dlg.ShowDialog() != true)
		{
			return;
		}
		_gs.LastBatchTags = string.Join(Environment.NewLine, dlg.Tags);
		SaveGui();
		foreach (string folder in chosen.Select(w => w.Folder).Distinct())
		{
			string path = Path.Combine(folder, EditMetaWindow.FileName);
			Dictionary<string, object> data = new Dictionary<string, object>();
			try
			{
				if (File.Exists(path))
				{
					data = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) ?? data;
				}
			}
			catch
			{
			}
			IEnumerable<string> old = data.TryGetValue("add_tags", out object v) && v is object[] arr ? arr.Select(x => x?.ToString()) : Enumerable.Empty<string>();
			data["add_tags"] = old.Concat(dlg.Tags).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
			File.WriteAllText(path, Json.Serialize(data), new UTF8Encoding(false));
		}
		FooterStatus.Text = $"已给 {chosen.Count} 部作品加上标签：{string.Join("、", dlg.Tags)}，正在重新扫描…";
		await Scan();
	}

	/// <summary>画廊链接一律在 exhentai 打开（已登录，看得到全部画廊和评论）。</summary>
	private static string ToEx(string url) => System.Text.RegularExpressions.Regex.Replace(url ?? "", @"^https?://(www\.)?e-hentai\.org/(?=[gs]/)", "https://exhentai.org/");
	private void OpenPath_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.Tag is string path && path.Length > 0)
		{
			try
			{
				Process.Start(new ProcessStartInfo(ToEx(path)) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "打不开：" + ex.Message, "EH 上传助手");
			}
		}
	}

	// ---- upload ----

	private async void Upload_Click(object sender, RoutedEventArgs e)
	{
		List<WorkItem> picked = _works.Where(w => w.Selected).ToList();
		if (picked.Count == 0 || _uploading)
		{
			return;
		}
		double min = MinMonths.Value ?? 0;
		int early = picked.Count(w => !w.Eligible);
		string msg = $"将上传并公开发布 {picked.Count} 部作品，发布后会自动补标签。"
			+ (early > 0 ? $"\n\n其中 {early} 部原作还不满 {min:0.#} 个月（你手动勾选的）。" : "")
			+ (string.IsNullOrWhiteSpace(CommentBox.Text) ? "\n\n简介目前为空，这次上传不会填写简介。" : "")
			+ (string.IsNullOrWhiteSpace(ShareBox.Text) || ShareModeBox.SelectedIndex == 2 ? ""
				: ShareModeBox.SelectedIndex == 1 ? "\n\n每部发布后都会发表经验分享评论。" : "\n\n经验分享评论只在第一部发布后发表一次。")
			+ "\n\n确定开始吗？";
		if (MessageBox.Show(this, msg, "开始上传", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		// 设置页里没点保存的修改也一并生效
		if (!await SaveSettings())
		{
			MessageBox.Show(this, "保存设置失败：" + FooterStatus.Text, "EH 上传助手");
			return;
		}
		StartUpload(picked);
	}

	private void StartUpload(List<WorkItem> picked)
	{
		_queue.Clear();
		foreach (WorkItem w in picked)
		{
			w.QueueReset();
			_queue.Add(w);
		}
		_ok = _failed = 0;
		_current = null;
		_fatal = null;
		_finishedEvent = false;
		_log.Clear();
		LogBox.Text = "";
		ResultBar.IsOpen = false;
		CurrentCard.Visibility = Visibility.Visible;
		CurrentTitle.Text = "准备中…";
		CurrentStage.Text = "正在启动浏览器并检查登录状态";
		CurrentDetail.Text = "";
		CurrentBar.IsIndeterminate = true;
		CurrentPercent.Text = "";
		_uploading = true;
		UpdateOverall();
		UpdateUploadButton();
		ScanButton.IsEnabled = false;
		SelectPage(1);

		List<string> args = new List<string> { "upload", "--json" };
		args.AddRange(picked.Select(w => w.Folder).Distinct());
		// 一本拆成多篇时，只上传勾选的那几篇
		args.Add("--keys");
		args.AddRange(picked.Select(w => w.Key));
		_backend.Python = _gs.Python;
		try
		{
			_backend.Start(args,
				ev => Dispatcher.BeginInvoke((Action)(() => OnEvent(ev))),
				line => Dispatcher.BeginInvoke((Action)(() => AppendLog(line))),
				code => Dispatcher.BeginInvoke((Action)(() => OnUploadExit(code))));
		}
		catch (Exception ex)
		{
			_fatal = "无法启动 Python：" + ex.Message;
			OnUploadExit(-1);
		}
	}

	private WorkItem Find(Dictionary<string, object> ev) => _queue.FirstOrDefault(w => w.Key == Json.Str(ev, "gid"));

	private void OnEvent(Dictionary<string, object> ev)
	{
		WorkItem w = Find(ev);
		switch (Json.Str(ev, "ev"))
		{
			case "item_start":
				_countdown?.Stop();
				if (w == null) break;
				_current = w;
				w.QueueRunning("开始上传…");
				w.QueueBusy = true;
				CurrentTitle.Text = $"({Json.Long(ev, "index")}/{Json.Long(ev, "total")}) {w.Title}";
				CurrentStage.Text = "开始上传…";
				CurrentDetail.Text = "";
				SetCurrentPercent(null);
				break;
			case "stage":
				if (w == null) break;
				string stage = Json.Str(ev, "stage");
				double? p = Json.Has(ev, "percent") ? Json.Num(ev, "percent") : (double?)null;
				w.QueueRunning(stage);
				w.QueueBusy = p == null;
				if (p != null) w.QueuePercent = p.Value;
				if (Json.Has(ev, "url")) w.Url = Json.Str(ev, "url");
				CurrentStage.Text = stage;
				CurrentDetail.Text = "";
				SetCurrentPercent(p);
				break;
			case "progress":
				if (w == null) break;
				double pct = Json.Num(ev, "percent");
				w.QueueBusy = false;
				w.QueuePercent = pct;
				CurrentDetail.Text = Json.Str(ev, "text");
				SetCurrentPercent(pct);
				break;
			case "item_done":
				if (w == null) break;
				_ok++;
				w.Url = Json.Str(ev, "url");
				bool wasUpdate = w.HasUpdate && !Json.Bool(ev, "skipped");
				w.State = "done";
				w.UpdatePages = new List<int>();
				int missing = Json.List(ev, "missing_tags").Length;
				w.QueueSucceeded(Json.Bool(ev, "skipped") ? "之前已经上传过"
					: (wasUpdate ? "成功：新版本已发布" : "成功：已发布") + (missing > 0 ? $"，有 {missing} 个标签没生效（见上传记录）" : "，标签已添加"));
				break;
			case "item_failed":
				if (w == null) break;
				_failed++;
				w.QueueFailed("失败：" + Json.Str(ev, "error"));
				break;
			case "fatal":
				_fatal = Json.Str(ev, "error");
				if (_fatal.Contains("登录"))
				{
					SetLogin(false);
				}
				break;
			case "waiting":
				StartCountdown((int)Json.Num(ev, "seconds"), _queue.FirstOrDefault(q => q.Key == Json.Str(ev, "next_gid")));
				break;
			case "finished":
				_finishedEvent = true;
				break;
		}
		if (Json.Str(ev, "ev") == "item_start" && _gs.LoginOk != true)
		{
			SetLogin(true);
		}
		UpdateOverall();
	}

	private DispatcherTimer _countdown;

	/// <summary>Shows the pause between two galleries as a countdown.</summary>
	private void StartCountdown(int seconds, WorkItem next)
	{
		_countdown?.Stop();
		DateTime end = DateTime.Now.AddSeconds(seconds);
		void Tick()
		{
			int left = Math.Max(0, (int)Math.Ceiling((end - DateTime.Now).TotalSeconds));
			CurrentStage.Text = $"休息一下，{left} 秒后上传下一部";
			CurrentDetail.Text = next != null ? "下一部：" + next.Title : "";
			if (next != null && next.QueueStatus.StartsWith("等待"))
			{
				next.QueueStatus = $"等待中（{left} 秒后开始）";
			}
			if (left == 0 || !_uploading)
			{
				_countdown?.Stop();
			}
		}
		_countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_countdown.Tick += (s, e) => Tick();
		SetCurrentPercent(null);
		Tick();
		_countdown.Start();
	}

	private void SetCurrentPercent(double? p)
	{
		CurrentBar.IsIndeterminate = p == null;
		CurrentBar.Value = p ?? 0;
		CurrentPercent.Text = p == null ? "" : $"{p:0}%";
	}

	private void UpdateOverall()
	{
		int total = _queue.Count;
		int finished = _queue.Count(IsFinished);
		double cur = _current != null && !IsFinished(_current) ? _current.QueuePercent / 100 : 0;
		double value = total == 0 ? 0 : Math.Min(100, (finished + cur) / total * 100);
		OverallBar.Value = value;
		OverallBar.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
		QueueEmpty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
		OverallPercent.Text = total == 0 ? "" : $"{value:0}%";
		OverallText.Text = total == 0 ? "还没有开始上传" : $"已处理 {finished}/{total} 部 · 成功 {_ok} · 失败 {_failed}";
		SideUpload.Text = _uploading ? $"上传中 {finished}/{total}" : "";
		Taskbar.ProgressState = _uploading ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;
		Taskbar.ProgressValue = value / 100;
	}

	private static bool IsFinished(WorkItem w) =>
		w.QueueStatus.StartsWith("成功") || w.QueueStatus.StartsWith("失败") || w.QueueStatus.StartsWith("之前");

	private void OnUploadExit(int code)
	{
		_uploading = false;
		_countdown?.Stop();
		_mineStale = _mineStale || _ok > 0;   // 有新画廊发布了，「已上传」页下次打开时重读列表
		foreach (WorkItem w in _queue.Where(w => !IsFinished(w)).ToList())
		{
			if (w.QueueStatus == "等待中")
			{
				w.QueueReset();
				w.QueueStatus = "未处理";
			}
			else
			{
				w.QueueFailed("已中断（再次上传会从断点继续）");
			}
		}
		UpdateOverall();
		CurrentBar.IsIndeterminate = false;
		ScanButton.IsEnabled = true;
		FooterProgress.Visibility = Visibility.Collapsed;
		SideUpload.Text = "";

		if (_fatal != null)
		{
			ShowResult(InfoBarSeverity.Error, "上传没有完成", _fatal);
		}
		else if (!_finishedEvent)
		{
			ShowResult(InfoBarSeverity.Warning, "上传已停止", $"成功 {_ok} 部。上传过程被中断（退出码 {code}），再次上传会从断点继续。");
		}
		else if (_failed == 0)
		{
			ShowResult(InfoBarSeverity.Success, "全部上传成功", $"{_ok} 部作品已经发布并添加了标签，可以在队列里点链接查看。");
		}
		else
		{
			ShowResult(InfoBarSeverity.Warning, "上传完成，有失败的作品", $"成功 {_ok} 部，失败 {_failed} 部。失败原因见队列和运行日志。");
		}
		CurrentStage.Text = "";
		CurrentDetail.Text = "";
		Taskbar.ProgressState = _failed > 0 || _fatal != null ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
		Flash();
		RefreshHistory();
		foreach (WorkItem w in _works)
		{
			w.Evaluate(MinMonths.Value ?? 0, resetSelection: false);
			if (w.Done)
			{
				w.Selected = false;
			}
		}
		RefreshFilter();
		UpdateSummary();
	}

	private void ShowResult(InfoBarSeverity severity, string title, string message)
	{
		ResultBar.Severity = severity;
		ResultBar.Title = title;
		ResultBar.Message = message;
		ResultBar.IsOpen = true;
		if (!IsVisible || WindowState == WindowState.Minimized)
		{
			_tray?.ShowBalloonTip(8000, title, message, severity == InfoBarSeverity.Success ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
		}
	}

	private GridLength _logHeight = new GridLength(200);

	/// <summary>收起 / 展开运行日志：收起时队列占满，展开时恢复上次拖动的高度。</summary>
	private void LogToggle_Click(object sender, RoutedEventArgs e)
	{
		bool collapse = LogBox.Visibility == Visibility.Visible;
		if (collapse)
		{
			_logHeight = LogRow.ActualHeight > 60 ? new GridLength(LogRow.ActualHeight) : _logHeight;
			LogRow.Height = GridLength.Auto;
		}
		else
		{
			LogRow.Height = _logHeight;
			LogBox.ScrollToEnd();
		}
		LogBox.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
		LogSplitter.IsEnabled = !collapse;
		LogToggle.Content = collapse ? "展开" : "收起";
		LogToggle.Icon = new SymbolIcon { Symbol = collapse ? SymbolRegular.ChevronUp24 : SymbolRegular.ChevronDown24 };
	}

	private void Stop_Click(object sender, RoutedEventArgs e)
	{
		if (!_uploading)
		{
			return;
		}
		if (MessageBox.Show(this, "停止当前上传？已经上传的部分会保留，下次上传会从断点继续。", "停止上传", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			_backend.Stop();
		}
	}

	// ---- 托盘 ----

	private System.Windows.Forms.NotifyIcon _tray;
	private bool _exiting;

	private void InitTray()
	{
		string exe = Process.GetCurrentProcess().MainModule.FileName;
		_tray = new System.Windows.Forms.NotifyIcon
		{
			Icon = new System.Drawing.Icon(System.Drawing.Icon.ExtractAssociatedIcon(exe), System.Windows.Forms.SystemInformation.SmallIconSize),
			Text = "EH 上传助手",
			Visible = true,
		};
		_tray.MouseClick += (s, e) =>
		{
			if (e.Button == System.Windows.Forms.MouseButtons.Left)
			{
				ShowFromTray();
			}
		};
		_tray.MouseUp += (s, e) =>
		{
			if (e.Button == System.Windows.Forms.MouseButtons.Right)
			{
				Dispatcher.BeginInvoke((Action)ShowTrayMenu);
			}
		};
		Closed += delegate { _tray.Visible = false; _tray.Dispose(); _trayMenu.Close(); Application.Current.Shutdown(); };   // 托盘菜单的隐藏窗口等不能让进程留着
	}

	private readonly TrayMenu _trayMenu = new TrayMenu();

	/// <summary>右键托盘图标：亚克力样式的菜单（和 Claude 自动审批一致）。</summary>
	private void ShowTrayMenu()
	{
		int total = _queue.Count, done = _queue.Count(IsFinished);
		string status = _uploading ? $"上传中 {done}/{total}" : (total > 0 ? $"上次上传：成功 {_ok}，失败 {_failed}" : "空闲");
		int newComments = _mine.Sum(g => g.NewCount);
		var entries = new List<TrayMenu.Entry>
		{
			new TrayMenu.Entry { Text = status, Enabled = false, Icon = _uploading ? SymbolRegular.ArrowUpload24 : SymbolRegular.CheckmarkCircle24 },
		};
		if (newComments > 0)
		{
			entries.Add(new TrayMenu.Entry { Text = $"{newComments} 条新评论", Icon = SymbolRegular.Comment24, Click = () => { ShowFromTray(); SelectPage(3); } });
		}
		entries.Add(new TrayMenu.Entry { Separator = true });
		entries.Add(new TrayMenu.Entry { Text = "显示窗口", Bold = true, Icon = SymbolRegular.Window24, Click = ShowFromTray });
		entries.Add(new TrayMenu.Entry { Text = "上传进度", Icon = SymbolRegular.ArrowUpload24, Click = () => { ShowFromTray(); SelectPage(1); } });
		entries.Add(new TrayMenu.Entry { Text = "打开日志文件夹", Icon = SymbolRegular.Folder24, Click = () => OpenLogs_Click(this, null) });
		entries.Add(new TrayMenu.Entry { Separator = true });
		entries.Add(new TrayMenu.Entry
		{
			Text = _updateStatus ?? (_update != null ? $"更新到 v{_update.Version}…" : "检查更新…"),
			Bold = _update != null && _updateStatus == null,
			Enabled = _updateStatus == null,
			Icon = SymbolRegular.ArrowSync24,
			Click = () => Dispatcher.BeginInvoke((Action)(async () => await CheckUpdates(manual: true)), DispatcherPriority.Background),
		});
		// 等右键菜单自己关掉以后再退出，不然菜单还开着时关主窗口，进程会留在后台退不掉
		entries.Add(new TrayMenu.Entry { Text = "退出", Icon = SymbolRegular.Power24, Click = () => Dispatcher.BeginInvoke((Action)ExitApp, DispatcherPriority.Background) });
		_trayMenu.Show(entries);
	}

	// ---- 检查更新 ----

	private UpdateInfo _update;        // 已发现的新版本
	private string _updateStatus;      // 正在检查/下载时显示在托盘菜单里
	private string _notifiedVersion;   // 已经弹过提示的版本，同一版本只提示一次
	private DispatcherTimer _updateTimer;

	/// <summary>启动 20 秒后检查一次，之后每 12 小时检查一次；发现新版本时托盘弹出提示。</summary>
	private void InitUpdateCheck()
	{
		_tray.BalloonTipClicked += (s, e) =>
		{
			if (_update != null)
			{
				Dispatcher.BeginInvoke((Action)(async () => await CheckUpdates(manual: true)));
			}
		};
		_updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
		_updateTimer.Tick += async (s, e) =>
		{
			_updateTimer.Interval = TimeSpan.FromHours(12);
			await CheckUpdates(manual: false);
		};
		_updateTimer.Start();
		Closed += delegate { _updateTimer.Stop(); };
	}

	/// <summary>manual=true：用户点了菜单，结果都要告诉用户，有新版本就问要不要更新；false：后台检查，只在发现新版本时弹一次提示。</summary>
	private async Task CheckUpdates(bool manual)
	{
		if (_updateStatus != null)
		{
			return;
		}
		// 后台也每次都重新查：期间又出了更新的版本时能提示最新的那个
		{
			_updateStatus = "正在检查更新…";
			try
			{
				_update = await Updater.CheckAsync();
			}
			catch (Exception ex)
			{
				_updateStatus = null;
				if (manual)
				{
					MessageBox.Show(this, "检查更新失败：" + ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				return;
			}
			_updateStatus = null;
		}
		if (_update == null)
		{
			if (manual)
			{
				MessageBox.Show(this, "已经是最新版本：v" + Updater.CurrentVersion, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			return;
		}
		if (!manual)
		{
			if (_notifiedVersion != _update.Version)
			{
				_notifiedVersion = _update.Version;
				_tray.ShowBalloonTip(8000, "EH 上传助手有新版本 v" + _update.Version, "点这里更新，或者右键托盘图标选“更新到 v" + _update.Version + "”。", System.Windows.Forms.ToolTipIcon.Info);
			}
			return;
		}
		await InstallUpdate(_update);
	}

	private async Task InstallUpdate(UpdateInfo update)
	{
		if (_uploading)
		{
			MessageBox.Show(this, "正在上传，等这次上传结束后再更新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		string notes = update.Notes.Length > 1200 ? update.Notes.Substring(0, 1200) + "…" : update.Notes;
		if (MessageBox.Show(this, $"发现新版本 v{update.Version}（当前 v{Updater.CurrentVersion}）。\n\n{notes}\n\n现在下载并更新吗？更新完会自动重新打开。",
				"检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		if (!Updater.CanWriteAppDir())
		{
			if (MessageBox.Show(this, "程序所在的文件夹不能写入（可能放在了 Program Files 里），没法自动更新。要打开下载页面手动更新吗？", "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
			{
				Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
			}
			return;
		}
		_updateStatus = "正在下载更新… 0%";
		try
		{
			var progress = new Progress<int>(p => _updateStatus = $"正在下载更新… {p}%");
			string zip = await Updater.DownloadAsync(update, progress);
			if (_uploading)
			{
				MessageBox.Show(this, "下载期间开始了上传，等上传结束后再点一次更新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			Updater.StartInstall(zip);
		}
		catch (Exception ex)
		{
			if (MessageBox.Show(this, "更新失败：" + ex.Message + "\n\n要打开下载页面手动更新吗？", "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
			{
				Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
			}
			return;
		}
		finally
		{
			_updateStatus = null;
		}
		_tray.Visible = false;
		ExitApp();
	}

	private void ShowFromTray()
	{
		Show();
		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}
		Activate();
	}

	private void HideToTray()
	{
		Hide();
		if (!_gs.TrayHintShown)
		{
			_tray.ShowBalloonTip(5000, "EH 上传助手还在运行", "已缩到托盘，上传会继续。单击托盘图标恢复窗口，右键可以退出。", System.Windows.Forms.ToolTipIcon.Info);
			_gs.TrayHintShown = true;
			SaveGui();
		}
	}

	private void ExitApp()
	{
		_exiting = true;
		ShowFromTray();
		Close();
	}

	private void CloseToTray_Click(object sender, RoutedEventArgs e)
	{
		_gs.CloseToTray = CloseToTray.IsChecked == true;
		SaveGui();
	}

	private void OnClosing(object sender, CancelEventArgs e)
	{
		if (!_exiting && _gs.CloseToTray)
		{
			e.Cancel = true;
			HideToTray();
			return;
		}
		if (_uploading)
		{
			if (MessageBox.Show(this, "正在上传，退出会中断上传（下次可以从断点继续）。确定退出吗？", "EH 上传助手", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				e.Cancel = true;
				_exiting = false;
				return;
			}
			_backend.Stop();
		}
		Backend.StopAll();   // 扫描、读评论等后台脚本一起结束，不留孤儿进程
	}

	private void AppendLog(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		_log.AppendLine(text.TrimEnd());
		if (_log.Length > 200_000)
		{
			_log.Remove(0, _log.Length - 150_000);
		}
		LogBox.Text = _log.ToString();
		LogBox.ScrollToEnd();
	}

	private static string LastLine(string log) =>
		log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Python 没有输出";

	[StructLayout(LayoutKind.Sequential)]
	private struct FLASHWINFO
	{
		public uint cbSize;
		public IntPtr hwnd;
		public uint dwFlags;
		public uint uCount;
		public uint dwTimeout;
	}

	[DllImport("user32.dll")]
	private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

	private void Flash()
	{
		if (IsActive)
		{
			return;
		}
		FLASHWINFO fi = new FLASHWINFO { hwnd = new WindowInteropHelper(this).Handle, dwFlags = 3 | 12, uCount = 5 };
		fi.cbSize = (uint)Marshal.SizeOf(fi);
		FlashWindowEx(ref fi);
	}

	// ---- 已上传：画廊和最新评论 ----

	private const int CommentsShown = 3;
	private readonly ObservableCollection<GalleryItem> _mine = new ObservableCollection<GalleryItem>();
	private readonly Backend _mineBackend;
	private readonly string _seenPath;
	private readonly Dictionary<string, long> _seen;
	private DateTime? _mineLoadedAt;
	private bool _mineStale;
	private static readonly TimeSpan MineInterval = TimeSpan.FromMinutes(15);
	private readonly DispatcherTimer _mineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
	private string _mineFetched = "";
	private string _mineChecked = "";
	private string _mineProgress = "";

	private Dictionary<string, long> LoadSeen()
	{
		try
		{
			if (File.Exists(_seenPath))
			{
				return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, long>>(File.ReadAllText(_seenPath)) ?? new Dictionary<string, long>();
			}
		}
		catch
		{
		}
		return new Dictionary<string, long>();
	}

	private void SaveSeen()
	{
		try
		{
			File.WriteAllText(_seenPath, Json.Serialize(_seen));
		}
		catch
		{
		}
	}

	/// <summary>refresh: cache = 只显示本地缓存；smart = 先显示缓存，评论按热度只查到时间的；all = 评论全部重读。</summary>
	private void LoadMine(string refresh)
	{
		if (_mineBackend.Running)
		{
			return;
		}
		_mineLoadedAt = DateTime.Now;
		MineBar.IsOpen = false;
		MineRing.Visibility = Visibility.Visible;
		MineRefresh.IsEnabled = false;
		_mineProgress = "正在读取「我的上传」…";
		UpdateMineSummary();
		_mineBackend.Python = _gs.Python;
		try
		{
			_mineBackend.Start(new[] { "mine", "--n", CommentsShown.ToString(), "--refresh", refresh },
				ev => Dispatcher.BeginInvoke((Action)(() => OnMineEvent(ev))),
				line => { },
				code => Dispatcher.BeginInvoke((Action)(() =>
				{
					MineRing.Visibility = Visibility.Collapsed;
					MineRefresh.IsEnabled = true;
					_mineProgress = "";
					UpdateMineSummary();
				})));
		}
		catch (Exception ex)
		{
			ShowMineError("无法启动 Python：" + ex.Message);
			MineRing.Visibility = Visibility.Collapsed;
			MineRefresh.IsEnabled = true;
		}
	}

	private void OnMineEvent(Dictionary<string, object> ev)
	{
		switch (Json.Str(ev, "ev"))
		{
			case "mine":
				Dictionary<long, GalleryItem> old = _mine.ToDictionary(g => g.Gid);
				_mine.Clear();
				foreach (Dictionary<string, object> d in Json.List(ev, "items").OfType<Dictionary<string, object>>())
				{
					GalleryItem g = GalleryItem.FromJson(d);
					if (old.TryGetValue(g.Gid, out GalleryItem prev))
					{
						g.Draft = prev.Draft;
						g.EditorOpen = prev.EditorOpen;
						g.KeepVisible = prev.KeepVisible;
					}
					g.SetComments(Json.List(d, "comments"), (int)Json.Long(d, "total"), Json.Str(d, "fetched"), _seen, CommentsShown);
					_mine.Add(g);
				}
				SaveSeen();
				_mineFetched = Json.Str(ev, "list_fetched");
				_mineProgress = "正在检查画廊列表和新评论…";
				break;
			case "gallery_comments":
				GalleryItem item = _mine.FirstOrDefault(g => g.Gid == Json.Long(ev, "gid"));
				int before = item?.NewCount ?? 0;
				item?.SetComments(Json.List(ev, "items"), (int)Json.Long(ev, "total"), Json.Str(ev, "fetched"), _seen, CommentsShown);
				if (item != null && item.NewCount > before)
				{
					Flash();   // 后台查到新评论时闪一下任务栏提醒
				}
				SaveSeen();
				break;
			case "comments_progress":
				long done = Json.Long(ev, "done"), total = Json.Long(ev, "total");
				_mineProgress = done < total ? $"正在检查评论 {done}/{total}…" : "";
				if (done >= total && Json.Has(ev, "skipped"))
				{
					_mineChecked = $"刚检查了 {Json.Long(ev, "checked")} 个画廊的评论，其余 {Json.Long(ev, "skipped")} 个还没到检查时间";
				}
				break;
			case "fatal":
				ShowMineError(Json.Str(ev, "error"));
				break;
		}
		ApplyMineFilter();
		UpdateMineSummary();
	}

	private void ShowMineError(string message)
	{
		MineBar.Title = "读取失败";
		MineBar.Message = message;
		MineBar.IsOpen = true;
	}

	private void UpdateMineSummary()
	{
		int withNew = _mine.Count(g => g.NewCount > 0);
		int newTotal = _mine.Sum(g => g.NewCount);
		string fetched = _mineChecked.Length > 0 ? " · " + _mineChecked + "（点「刷新评论」可全部重查）"
			: DateTime.TryParse(_mineFetched, out DateTime t) ? $" · 列表更新于 {t:MM-dd HH:mm}" : "";
		MineSummary.Text = (_mine.Count == 0 ? "" : $"共 {_mine.Count} 个画廊" + (withNew > 0 ? $" · {withNew} 个有新评论（{newTotal} 条）" : " · 没有新评论"))
			+ (_mineProgress.Length > 0 ? (_mine.Count > 0 ? " · " : "") + _mineProgress : fetched);
		MineNewBadge.Visibility = newTotal > 0 ? Visibility.Visible : Visibility.Collapsed;
		MineNewText.Text = newTotal.ToString();
	}

	private void ApplyMineFilter()
	{
		if (OnlyNew.IsChecked == true)
		{
			MineList.ItemsSource = _mine.Where(g => g.NewCount > 0 || g.KeepVisible || g.Sending || (g.EditorOpen && !string.IsNullOrWhiteSpace(g.Draft))).ToList();
		}
		else if (!ReferenceEquals(MineList.ItemsSource, _mine))
		{
			MineList.ItemsSource = _mine;
		}
	}

	private void OnlyNew_Click(object sender, RoutedEventArgs e)
	{
		_gs.OnlyNewComments = OnlyNew.IsChecked == true;
		SaveGui();
		ApplyMineFilter();
	}

	private void MineRefresh_Click(object sender, RoutedEventArgs e) => LoadMine("all");

	/// <summary>给已发布的画廊批量补标签：脚本先查每个画廊已有的标签，已有的跳过，只投缺的。</summary>
	private async void MineAddTags_Click(object sender, RoutedEventArgs e)
	{
		if (_uploading)
		{
			MessageBox.Show(this, "正在上传，等这批传完再补标签（两边要用同一个浏览器）。", "批量补标签");
			return;
		}
		if (_mine.Count == 0)
		{
			MessageBox.Show(this, "「已上传」列表还是空的，先刷新一次。", "批量补标签");
			return;
		}
		List<TagBatchWindow.Entry> entries = _mine.Select(g => new TagBatchWindow.Entry { Tag = g, Label = g.Title, Sub = g.Info, Thumb = g.Thumb }).ToList();
		TagBatchWindow dlg = new TagBatchWindow("批量补标签",
			"给已发布的画廊补标签。脚本会先查每个画廊已经有哪些标签，已有的跳过、只投缺的；全都有的画廊不会打开页面。画廊之间会停顿一下，不会刷太快。",
			"开始补标签", entries, _gs.LastBatchTags) { Owner = this };
		if (dlg.ShowDialog() != true)
		{
			return;
		}
		List<GalleryItem> chosen = dlg.Chosen.OfType<GalleryItem>().ToList();
		_gs.LastBatchTags = string.Join(Environment.NewLine, dlg.Tags);
		SaveGui();
		string tmp = Path.Combine(Path.GetTempPath(), "eh_addtags.json");
		File.WriteAllText(tmp, Json.Serialize(new Dictionary<string, object>
		{
			["tags"] = dlg.Tags.ToArray(),
			["galleries"] = chosen.Select(g => new object[] { g.Gid, g.Token }).ToArray(),
		}), new UTF8Encoding(false));
		MineAddTags.IsEnabled = false;
		FooterStatus.Text = $"正在给 {chosen.Count} 个画廊补标签…";
		(Dictionary<string, object> ev, string log, _) = await new Backend(_backend.Script) { Python = _gs.Python }
			.RunAsync(new[] { "addtags", "--file", tmp }, "addtags_done",
				p => { if (Json.Str(p, "ev") == "addtags_progress") Dispatcher.BeginInvoke((Action)(() => FooterStatus.Text = Json.Str(p, "text"))); });
		MineAddTags.IsEnabled = true;
		if (ev == null || Json.Str(ev, "ev") == "fatal")
		{
			FooterStatus.Text = "";
			MessageBox.Show(this, "补标签失败：" + (ev != null ? Json.Str(ev, "error") : LastLine(log)), "批量补标签");
			return;
		}
		object[] failed = Json.List(ev, "failed");
		FooterStatus.Text = $"补标签完成：处理 {Json.Long(ev, "done")} 个，跳过 {Json.Long(ev, "skipped")} 个已有的"
			+ (failed.Length > 0 ? $"，{failed.Length} 个有标签没生效（详情见日志）" : "");
	}

	/// <summary>已发布画廊的标题网站上是锁住的（只改标题、不改图片的新版本会被网站丢弃），只能走网站的改名投票：在浏览器里打开投票页。</summary>
	private void Retitle_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.DataContext is GalleryItem g && !string.IsNullOrEmpty(g.Token))
		{
			try
			{
				Process.Start(new ProcessStartInfo($"https://e-hentai.org/gallerypopups.php?gid={g.Gid}&t={g.Token}&act=rename") { UseShellExecute = true });
				FooterStatus.Text = "已打开改名投票页：在「新」里填新标题提交；需要别人的票数攒够才会改名";
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "打不开：" + ex.Message, "EH 上传助手");
			}
		}
	}

	private void MarkAllRead_Click(object sender, RoutedEventArgs e)
	{
		foreach (GalleryItem g in _mine)
		{
			g.MarkRead(_seen);
			g.KeepVisible = false;
			if (string.IsNullOrWhiteSpace(g.Draft) && !g.Sending)
			{
				g.EditorOpen = false;   // 没写内容的评论框一起收起，「只看有新评论」时全部消失
			}
		}
		SaveSeen();
		ApplyMineFilter();
		UpdateMineSummary();
	}

	private void MarkRead_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.DataContext is GalleryItem g)
		{
			g.MarkRead(_seen);
			g.KeepVisible = false;   // 回复过而留着的，点这一本的「标为已读」也一起收起
			if (string.IsNullOrWhiteSpace(g.Draft) && !g.Sending)
			{
				g.EditorOpen = false;
			}
			SaveSeen();
			ApplyMineFilter();
			UpdateMineSummary();
		}
	}

	private void ToggleEditor_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.DataContext is GalleryItem g)
		{
			g.EditorOpen = !g.EditorOpen;
			g.SendStatus = "";
		}
	}

	private async void SendComment_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement fe || fe.DataContext is not GalleryItem g)
		{
			return;
		}
		string text = (g.Draft ?? "").Trim();
		if (text.Length < 10)
		{
			g.SendStatus = "评论太短，e-hentai 要求至少 10 个字符";
			return;
		}
		if (MessageBox.Show(this, $"把这条评论发到「{g.Title}」？", "发送评论", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		g.Sending = true;
		g.SendStatus = "正在发送…";
		string tmp = Path.Combine(Path.GetTempPath(), $"eh_comment_{g.Gid}.txt");
		File.WriteAllText(tmp, text, new UTF8Encoding(false));
		(Dictionary<string, object> ev, string log, _) = await _mineBackend.RunAsync(
			new[] { "post-comment", g.Gid.ToString(), g.Token, "--file", tmp, "--n", CommentsShown.ToString() }, "comment_posted");
		g.Sending = false;
		try
		{
			File.Delete(tmp);
		}
		catch
		{
		}
		if (ev == null || Json.Str(ev, "ev") == "fatal")
		{
			g.SendStatus = "发送失败：" + (ev != null ? Json.Str(ev, "error") : LastLine(log));
			return;
		}
		// 发送期间后台刷新可能已经重建了列表，改当前列表里的那一项
		GalleryItem cur = _mine.FirstOrDefault(x => x.Gid == g.Gid) ?? g;
		cur.SetComments(Json.List(ev, "items"), (int)Json.Long(ev, "total"), DateTime.Now.ToString("s"), _seen, CommentsShown);
		cur.MarkRead(_seen);   // 自己刚发的不算新评论，回复完这个画廊的新评论也算看过了
		SaveSeen();
		if (Json.Bool(ev, "ok"))
		{
			foreach (GalleryItem x in new[] { g, cur }.Distinct())
			{
				x.Draft = "";
				x.EditorOpen = false;
				x.SendStatus = "";
				x.KeepVisible = true;   // 回复完留在列表里，点「全部标为已读」才消失
			}
		}
		else
		{
			cur.SendStatus = "已提交，但评论区里没找到这条评论，可能被网站拒绝（发得太频繁或内容不符合要求）";
		}
		ApplyMineFilter();
		UpdateMineSummary();
	}

	// ---- history ----

	private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();

	private void RefreshHistory()
	{
		string path = Path.Combine(_dir, "upload_state.json");
		List<HistoryRow> rows = new List<HistoryRow>();
		try
		{
			if (File.Exists(path) && Json.Parse(File.ReadAllText(path, Encoding.UTF8)) is Dictionary<string, object> state)
			{
				foreach (KeyValuePair<string, object> kv in state)
				{
					if (kv.Value is not Dictionary<string, object> st)
					{
						continue;
					}
					string status = Json.Str(st, "status");
					rows.Add(new HistoryRow
					{
						SourceGid = kv.Key,
						// 优先显示日文标题；旧记录没存日文，就到作品列表里按任务键找
						Title = Json.Str(st, "title_jp") is string jp && jp.Length > 0 ? jp
							: _works.FirstOrDefault(w => w.Key == kv.Key && !string.IsNullOrEmpty(w.Jp))?.Jp ?? Json.Str(st, "title"),
						Status = status switch { "done" => "完成", "tagged" => "已加标签，待发评论", "published" => "已发布，待补标签", "uploaded" => "已上传，待发布", "new" => "上传中断", _ => status },
						Finished = Json.Str(st, "finished").Replace("T", " "),
						MissingTags = string.Join(", ", Json.List(st, "missing_tags").Select(x => x?.ToString()))
							+ (Json.List(st, "tag_errors").Length > 0 ? "（网站提示：" + string.Join("；", Json.List(st, "tag_errors").Select(x => x?.ToString())) + "）" : ""),
						Url = Json.Str(st, "url"),
					});
				}
			}
		}
		catch (Exception ex)
		{
			HistorySummary.Text = "读取上传记录失败：" + ex.Message;
			return;
		}
		rows = rows.OrderByDescending(r => r.Finished).ToList();
		HistoryGrid.ItemsSource = rows;
		int done = rows.Count(r => r.Status == "完成");
		HistorySummary.Text = rows.Count == 0
			? "还没有上传记录。"
			: $"共 {rows.Count} 条记录，完成 {done} 条" + (rows.Count > done ? $"，未完成 {rows.Count - done} 条（在作品列表里再次上传会从断点继续）" : "") + "。";
	}

	private void OpenLogs_Click(object sender, RoutedEventArgs e)
	{
		string logs = Path.Combine(_dir, "logs");
		Directory.CreateDirectory(logs);
		Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true });
	}

	// ---- settings ----

	private async Task LoadConfig()
	{
		if (!File.Exists(_backend.Script))
		{
			return;
		}
		(Dictionary<string, object> ev, string log, _) = await _backend.RunAsync(new[] { "config" }, "config");
		if (ev == null)
		{
			FooterStatus.Text = "读取配置失败：" + LastLine(log);
			return;
		}
		CommentBox.Text = Json.Str(ev, "comment").Replace("\n", Environment.NewLine);
		ShareBox.Text = Json.Str(ev, "share_comment").Replace("\n", Environment.NewLine);
		BlockWords.Text = string.Join("，", Json.List(ev, "notice_keywords").Select(x => x?.ToString()));
		ReviewWords.Text = string.Join("，", Json.List(ev, "review_keywords").Select(x => x?.ToString()));
		object[] gap = Json.List(ev, "delay_between_galleries");
		GapMin.Value = gap.Length > 0 ? Convert.ToDouble(gap[0]) : 60;
		GapMax.Value = gap.Length > 1 ? Convert.ToDouble(gap[1]) : 120;
		string mode = Json.Str(ev, "share_comment_mode");
		ShareModeBox.SelectedIndex = mode == "all" ? 1 : (mode == "off" ? 2 : 0);
		ExtraTags.Text = string.Join(", ", Json.List(ev, "extra_tags").Select(x => x?.ToString()));
		BatchMb.Value = Json.Num(ev, "batch_max_mb");
		Headless.IsChecked = Json.Bool(ev, "headless");
		BrowserPath.Text = Json.Str(ev, "browser_path");
	}

	private async void Save_Click(object sender, RoutedEventArgs e) => await SaveSettings();

	private static string[] SplitWords(string text) =>
		text.Split(new[] { ',', '，', '、', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).Where(w => w.Length > 0).Distinct().ToArray();

	private async Task<bool> SaveSettings()
	{
		_gs.Root = RootPath.Text.Trim();
		_gs.Python = string.IsNullOrWhiteSpace(PythonPath.Text) ? "python" : PythonPath.Text.Trim();
		_backend.Python = _gs.Python;
		SaveGui();
		Dictionary<string, object> cfg = new Dictionary<string, object>
		{
			["comment"] = CommentBox.Text.Replace("\r\n", "\n").Trim(),
			["share_comment"] = ShareBox.Text.Replace("\r\n", "\n").Trim(),
			["notice_keywords"] = SplitWords(BlockWords.Text),
			["review_keywords"] = SplitWords(ReviewWords.Text),
			["share_comment_mode"] = ShareModeBox.SelectedIndex == 1 ? "all" : (ShareModeBox.SelectedIndex == 2 ? "off" : "first"),
			["delay_between_galleries"] = new[] { (int)Math.Min(GapMin.Value ?? 60, GapMax.Value ?? 120), (int)Math.Max(GapMin.Value ?? 60, GapMax.Value ?? 120) },
			["extra_tags"] = ExtraTags.Text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToArray(),
			["batch_max_mb"] = (int)(BatchMb.Value ?? 400),
			["headless"] = Headless.IsChecked == true,
			["browser_path"] = BrowserPath.Text.Trim(),
		};
		string tmp = Path.Combine(Path.GetTempPath(), "eh_upload_config.json");
		File.WriteAllText(tmp, Json.Serialize(cfg), new UTF8Encoding(false));
		(Dictionary<string, object> ev, string log, _) = await _backend.RunAsync(new[] { "config", "--set", tmp }, "config");
		FooterStatus.Text = ev != null ? "已保存 " + DateTime.Now.ToString("HH:mm:ss") : "保存失败：" + LastLine(log);
		return ev != null;
	}

	private async void Login_Click(object sender, RoutedEventArgs e)
	{
		LoginButton.IsEnabled = false;
		LoginState.Text = "已打开一个普通的浏览器窗口：请在里面完成网站验证并登录 e-hentai，登录成功后【关掉这个浏览器窗口】，这里会自动确认（最多等 15 分钟）…";
		_backend.Python = _gs.Python;
		(Dictionary<string, object> ev, string log, _) = await _backend.RunAsync(new[] { "login" }, "login");
		LoginButton.IsEnabled = true;
		if (ev == null)
		{
			LoginState.Text = "登录窗口出错：" + LastLine(log);
			return;
		}
		SetLogin(Json.Bool(ev, "ok"));
		if (_gs.LoginOk != true)
		{
			LoginState.Text = Json.Str(ev, "reason") is string r && r.Length > 0 ? r : "没有检测到登录，请重试。";
		}
		else if (_mine.Count == 0)
		{
			_mineLoadedAt = null;   // 登录后再进「已上传」会重新读取
		}
	}

	private void SetLogin(bool ok)
	{
		_gs.LoginOk = ok;
		_gs.LoginChecked = DateTime.Now.ToString("MM-dd HH:mm");
		SaveGui();
		UpdateSide();
	}

	private void BrowseRoot_Click(object sender, RoutedEventArgs e)
	{
		using System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = RootPath.Text };
		if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			RootPath.Text = dlg.SelectedPath;
		}
	}

	private void BrowseBrowser_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog { Filter = "浏览器程序|*.exe", FileName = BrowserPath.Text };
		if (dlg.ShowDialog(this) == true)
		{
			BrowserPath.Text = dlg.FileName;
		}
	}

	private void SaveGui()
	{
		try
		{
			_gs.Save(_settingsPath);
		}
		catch
		{
		}
	}

	// ---- appearance ----

	private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready)
		{
			return;
		}
		_gs.Theme = ThemeBox.SelectedIndex == 1 ? "light" : (ThemeBox.SelectedIndex == 2 ? "dark" : "system");
		_gs.Backdrop = BackdropBox.SelectedIndex == 1 ? "acrylic" : (BackdropBox.SelectedIndex == 2 ? "none" : "mica");
		ApplyAppearance();
		SaveGui();
	}

	private void ApplyAppearance()
	{
		WindowBackdropType backdrop = _gs.Backdrop == "acrylic" ? WindowBackdropType.Acrylic : (_gs.Backdrop == "none" ? WindowBackdropType.None : WindowBackdropType.Mica);
		if (_watching)
		{
			SystemThemeWatcher.UnWatch(this);
			_watching = false;
		}
		if (_gs.Theme == "light" || _gs.Theme == "dark")
		{
			ApplicationThemeManager.Apply(_gs.Theme == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light, backdrop, updateAccent: true);
		}
		else
		{
			ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
			SystemThemeWatcher.Watch(this, backdrop, updateAccents: true);
			_watching = true;
		}
		WindowBackdropType = backdrop;
		UpdateTint();
	}

	private void UpdateTint()
	{
		bool acrylic = _gs.Backdrop == "acrylic";
		bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
		Tint.Fill = new System.Windows.Media.SolidColorBrush(dark ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
		Tint.Opacity = acrylic ? _gs.TintPercent / 100.0 : 0.0;
		PageLayer.Background = acrylic ? System.Windows.Media.Brushes.Transparent : (System.Windows.Media.Brush)FindResource("LayerFillColorDefaultBrush");
		TintCard.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
		TintText.Text = _gs.TintPercent + "%";
	}

	private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
	{
		Dispatcher.BeginInvoke((Action)UpdateTint);
	}

	private void Tint_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready)
		{
			return;
		}
		_gs.TintPercent = (int)Math.Round(TintSlider.Value);
		UpdateTint();
		_tintSave.Stop();
		_tintSave.Start();
	}
}
