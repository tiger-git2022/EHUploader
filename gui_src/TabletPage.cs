using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace EHUploader;

/// <summary>One work sent from the tablet (a row of tablet_bridge.py's list).</summary>
public sealed class TabletItem : INotifyPropertyChanged
{
	private ImageSource _cover;

	public event PropertyChangedEventHandler PropertyChanged;

	private static readonly Dictionary<string, string> States = new Dictionary<string, string>
	{
		["receiving"] = "接收中",
		["extract_queued"] = "等待解压",
		["extracting"] = "解压中",
		["ocr_queued"] = "等待识别原文",
		["ocr"] = "识别原文中",
		["waiting"] = "等你翻译",
		["render_queued"] = "等待导入翻译并渲染",
		["rendering"] = "渲染中",
		["pack_queued"] = "等待打包",
		["packing"] = "打包中",
		["ready"] = "已打包，等平板取回",
		["taken"] = "平板已取回",
		["stopped"] = "已停止",
		["error"] = "出错",
	};

	public long Gid { get; set; }
	public string Title { get; set; }
	public string Folder { get; set; }
	public string Dir { get; set; }
	/// <summary>解压出的原图文件夹（根目录\批次\编号），列表「文件位置」一列。</summary>
	public string Location { get; set; }
	public string State { get; set; }
	public string Message { get; set; }
	public int Pages { get; set; }
	public int Translated { get; set; }
	/// <summary>封面：解压出的第一页缩略图，后台读好后才填上。</summary>
	public ImageSource Cover
	{
		get => _cover;
		set
		{
			_cover = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
		}
	}
	/// <summary>排队或正在识别原文、渲染：状态旁显示「停止」按钮。</summary>
	public Visibility StopVisibility => State is "ocr_queued" or "ocr" or "render_queued" or "rendering" ? Visibility.Visible : Visibility.Collapsed;
	public string Progress => Pages > 0 ? $"{Translated}/{Pages}" : (Translated > 0 ? Translated.ToString() : "");
	public string StateText
	{
		get
		{
			string text = string.IsNullOrEmpty(Message) || State == "taken" ? (States.TryGetValue(State ?? "", out string s) ? s : State) : Message;
			// 列表没有「译图」列了：等翻译时已经译了几页，写在状态后面
			return State == "waiting" && Translated > 0 ? $"{text}（已译 {Progress}）" : text;
		}
	}

	public static TabletItem FromJson(Dictionary<string, object> d) => new TabletItem
	{
		Gid = Json.Long(d, "gid"),
		Title = Json.Str(d, "title") is string t && t.Length > 0 ? t : Json.Str(d, "dir"),
		Folder = Json.Str(d, "folder"),
		Dir = Json.Str(d, "dir"),
		State = Json.Str(d, "state"),
		Message = Json.Str(d, "message"),
		Pages = (int)Json.Num(d, "pages"),
		Translated = (int)(Json.Has(d, "translated_now") ? Json.Num(d, "translated_now") : Json.Num(d, "translated")),
	};
}

/// <summary>「平板收发」页：在后台运行 tablet_bridge.py，接收平板 EhViewer 发来的漫画，打包送回。</summary>
public partial class MainWindow
{
	private readonly ObservableCollection<TabletItem> _tablet = new ObservableCollection<TabletItem>();
	private readonly DispatcherTimer _tabletPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
	private static readonly HttpClient TabletHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
	private Backend _tabletBackend;
	private DateTime _tabletStarted;
	private string _tabletAddress = "";
	private string _tabletCode = "";
	private string _tabletError;
	private bool _tabletBalloon;
	private bool _tabletStopping;
	/// <summary>封面缩略图按编号缓存；读不到的记下时间，过一会儿再试（可能还没解压）。</summary>
	private static readonly Dictionary<long, ImageSource> TabletCovers = new Dictionary<long, ImageSource>();
	private static readonly Dictionary<long, DateTime> TabletCoverMissed = new Dictionary<long, DateTime>();
	private static readonly HashSet<long> TabletCoverLoading = new HashSet<long>();
	private static readonly string[] CoverExts = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

	private string TabletLocal => $"http://127.0.0.1:{_gs.TabletPort}/local/";

	private void InitTablet()
	{
		TabletGrid.ItemsSource = _tablet;
		TabletEnabled.IsChecked = _gs.TabletEnabled;
		TabletOcr.IsChecked = _gs.TabletOcr;
		_tabletPoll.Tick += delegate
		{
			if (_page == 5 && IsVisible)
			{
				TabletRefresh();
			}
		};
		_tabletPoll.Start();
		Closed += delegate { StopTablet(); };
		if (_gs.TabletEnabled)
		{
			StartTablet();
		}
		UpdateTabletStatus();
	}

	private void StartTablet()
	{
		_tabletError = null;
		string script = Path.Combine(_dir, "tablet_bridge.py");
		if (!File.Exists(script))
		{
			_tabletError = "找不到 tablet_bridge.py，请把它和 EHUploader.exe 放在一起";
			UpdateTabletStatus();
			return;
		}
		if (string.IsNullOrWhiteSpace(_gs.Root) || !Directory.Exists(_gs.Root))
		{
			_tabletError = "先在「设置」里填好漫画根目录，收到的漫画放在它下面的批次文件夹里";
			UpdateTabletStatus();
			return;
		}
		List<string> args = new List<string> { "--root", _gs.Root, "--port", _gs.TabletPort.ToString() };
		if (_gs.TabletOcr)
		{
			args.Add("--ocr");
		}
		_tabletBackend = new Backend(script) { Python = _gs.Python };
		_tabletStarted = DateTime.Now;
		_tabletStopping = false;
		try
		{
			Backend b = _tabletBackend;
			b.Start(args, ev => Dispatcher.BeginInvoke((Action)(() => OnTabletEvent(ev))), line => { }, code => Dispatcher.BeginInvoke((Action)(() => OnTabletExit(b, code))));
		}
		catch (Exception ex)
		{
			_tabletBackend = null;
			_tabletError = "无法启动 Python：" + ex.Message;
		}
		UpdateTabletStatus();
	}

	private void StopTablet()
	{
		_tabletStopping = true;
		_tabletBackend?.Stop();
		_tabletBackend = null;
		_tabletAddress = "";
		_tabletCode = "";
	}

	private void OnTabletExit(Backend b, int code)
	{
		if (b != _tabletBackend || _tabletStopping)
		{
			return;
		}
		_tabletBackend = null;
		_tabletAddress = "";
		// 运行了一阵子才退出的（比如 Python 崩了）过几秒自动重开；一启动就退出的多半是配置问题，不反复重试
		if (_gs.TabletEnabled && DateTime.Now - _tabletStarted > TimeSpan.FromSeconds(30))
		{
			_tabletError = "收发服务意外退出，5 秒后重新打开…";
			DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
			t.Tick += delegate
			{
				t.Stop();
				if (_gs.TabletEnabled && _tabletBackend == null)
				{
					StartTablet();
				}
			};
			t.Start();
		}
		else if (_tabletError == null)
		{
			_tabletError = $"收发服务没能启动（退出码 {code}），详情见 logs 文件夹里的 tablet_日期.log";
		}
		UpdateTabletStatus();
	}

	private void OnTabletEvent(Dictionary<string, object> ev)
	{
		switch (Json.Str(ev, "ev"))
		{
			case "tablet_ready":
				string[] ips = Json.List(ev, "ips").Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
				_tabletAddress = (ips.Length > 0 ? string.Join(" / ", ips) : "本机") + ":" + Json.Str(ev, "port");
				_tabletCode = Json.Str(ev, "code");
				_tabletError = null;
				break;
			case "tablet_code":
				_tabletCode = Json.Str(ev, "code");
				break;
			case "tablet_items":
				SetTabletItems(Json.List(ev, "items").OfType<Dictionary<string, object>>());
				break;
			case "tablet_notice":
				string text = Json.Str(ev, "text");
				FooterStatus.Text = text;
				if (!IsVisible || WindowState == WindowState.Minimized || !IsActive)
				{
					_tabletBalloon = true;
					_tray?.ShowBalloonTip(6000, "平板收发", text, System.Windows.Forms.ToolTipIcon.Info);
				}
				break;
			case "fatal":
				_tabletError = Json.Str(ev, "text");
				break;
		}
		UpdateTabletStatus();
	}

	private void SetTabletItems(IEnumerable<Dictionary<string, object>> rows)
	{
		HashSet<long> selected = new HashSet<long>(TabletGrid.SelectedItems.OfType<TabletItem>().Select(i => i.Gid));
		_tablet.Clear();
		foreach (Dictionary<string, object> d in rows)
		{
			TabletItem item = TabletItem.FromJson(d);
			item.Location = Path.Combine(_gs.Root ?? "", item.Folder ?? "", item.Gid.ToString());
			LoadTabletCover(item);
			_tablet.Add(item);
		}
		foreach (TabletItem it in _tablet.Where(i => selected.Contains(i.Gid)))
		{
			TabletGrid.SelectedItems.Add(it);
		}
		int waiting = _tablet.Count(i => i.State == "waiting");
		TabletBadge.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
		TabletBadgeText.Text = waiting.ToString();
	}

	/// <summary>封面用解压出的第一页（文件名按长度再按字典序，和页码顺序一致），在后台读成小图。</summary>
	private void LoadTabletCover(TabletItem item)
	{
		long gid = item.Gid;
		if (TabletCovers.TryGetValue(gid, out ImageSource cached))
		{
			item.Cover = cached;
			return;
		}
		// 还在收、在解压：第一页可能只写了一半，等解压完再读
		if (item.State is "receiving" or "extract_queued" or "extracting" || TabletCoverLoading.Contains(gid)
			|| (TabletCoverMissed.TryGetValue(gid, out DateTime missed) && DateTime.Now - missed < TimeSpan.FromSeconds(30)))
		{
			return;
		}
		string dir = item.Location;
		TabletCoverLoading.Add(gid);
		Task.Run(delegate
		{
			ImageSource image = null;
			try
			{
				if (Directory.Exists(dir))
				{
					foreach (string file in Directory.EnumerateFiles(dir)
						.Where(f => CoverExts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("."))
						.OrderBy(f => Path.GetFileName(f).Length).ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
						.Take(3))
					{
						try
						{
							BitmapImage bmp = new BitmapImage();
							bmp.BeginInit();
							bmp.CacheOption = BitmapCacheOption.OnLoad;
							bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
							bmp.DecodePixelWidth = 120;
							bmp.UriSource = new Uri(file);
							bmp.EndInit();
							bmp.Freeze();
							image = bmp;
							break;
						}
						catch (Exception)
						{
							// 这张读不了（比如系统没有 webp 解码器）：换下一张
						}
					}
				}
			}
			catch (Exception)
			{
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				TabletCoverLoading.Remove(gid);
				if (image == null)
				{
					TabletCoverMissed[gid] = DateTime.Now;
					return;
				}
				TabletCovers[gid] = image;
				TabletCoverMissed.Remove(gid);
				foreach (TabletItem it in _tablet.Where(i => i.Gid == gid))
				{
					it.Cover = image;
				}
			}));
		});
	}

	/// <summary>识别开关下面：manga-translator-ui 找到没有，打开了却用不了时说清楚。</summary>
	private void UpdateTabletOcrState()
	{
		if (TabletOcrState == null || _gs == null)
		{
			return;
		}
		string problem = MtuProblem();
		if (problem == null)
		{
			TabletOcrState.Text = "已找到 manga-translator-ui：" + MtuPath.Text.Trim();
		}
		else
		{
			TabletOcrState.Text = (_gs.TabletOcr ? "⚠ 现在不会识别：" : "") + problem + "。在「设置」里选好目录并保存";
		}
	}

	private void UpdateTabletStatus()
	{
		bool running = _tabletBackend != null && _tabletBackend.Running;
		TabletNewCode.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
		if (_tabletError != null)
		{
			TabletStatus.Text = "接收平板发来的漫画（没有在运行）";
			TabletDetail.Text = _tabletError;
		}
		else if (!_gs.TabletEnabled)
		{
			TabletStatus.Text = "接收平板发来的漫画";
			TabletDetail.Text = "打开后，平板上的 EhViewer 在下载列表里多选、点「发到电脑翻译」，漫画就会放进漫画根目录下你选的批次文件夹";
		}
		else if (string.IsNullOrEmpty(_tabletAddress))
		{
			TabletStatus.Text = "正在打开收发服务…";
			TabletDetail.Text = "第一次打开时 Windows 防火墙会询问是否允许 Python 访问网络，请勾选「专用网络」并允许";
		}
		else
		{
			TabletStatus.Text = $"正在等待平板 · {_tabletAddress} · 配对码 {_tabletCode}";
			TabletDetail.Text = (_tabletOnline.Length > 0 ? "在线：" + string.Join("、", _tabletOnline) + " · " : "")
				+ (_tabletPaired > 0 ? $"已配对 {_tabletPaired} 台平板" : "平板上第一次连接时输入这个配对码")
				+ " · 收到的漫画放进 " + _gs.Root;
		}
	}

	private string[] _tabletOnline = new string[0];
	private int _tabletPaired;

	private async void TabletRefresh()
	{
		if (_tabletBackend == null || !_tabletBackend.Running)
		{
			return;
		}
		try
		{
			string text = await TabletHttp.GetStringAsync(TabletLocal + "items");
			Dictionary<string, object> d = Json.Parse(text);
			_tabletOnline = Json.List(d, "online").Select(x => x?.ToString()).Distinct().ToArray();
			_tabletPaired = (int)Json.Num(d, "paired");
			_tabletCode = Json.Str(d, "code");
			SetTabletItems(Json.List(d, "items").OfType<Dictionary<string, object>>());
			UpdateTabletStatus();
		}
		catch
		{
			// 服务刚启动或正在重启，下次再刷新
		}
	}

	private async Task<bool> TabletPost(string action, object body)
	{
		try
		{
			using StringContent content = new StringContent(Json.Serialize(body ?? new Dictionary<string, object>()), Encoding.UTF8, "application/json");
			using HttpResponseMessage r = await TabletHttp.PostAsync(TabletLocal + action, content);
			return r.IsSuccessStatusCode;
		}
		catch (Exception ex)
		{
			FooterStatus.Text = "收发服务没有响应：" + ex.Message;
			return false;
		}
	}

	private List<TabletItem> SelectedTablet()
	{
		List<TabletItem> list = TabletGrid.SelectedItems.OfType<TabletItem>().ToList();
		if (list.Count == 0)
		{
			FooterStatus.Text = "先在列表里选中作品（可以按住 Ctrl 或 Shift 多选）";
		}
		return list;
	}

	private void TabletEnabled_Click(object sender, RoutedEventArgs e)
	{
		_gs.TabletEnabled = TabletEnabled.IsChecked == true;
		SaveGui();
		StopTablet();
		_tabletError = null;
		if (_gs.TabletEnabled)
		{
			StartTablet();
		}
		else
		{
			_tablet.Clear();
			TabletBadge.Visibility = Visibility.Collapsed;
		}
		UpdateTabletStatus();
	}

	private void TabletOcr_Click(object sender, RoutedEventArgs e)
	{
		_gs.TabletOcr = TabletOcr.IsChecked == true;
		SaveGui();
		UpdateTabletOcrState();
		if (_gs.TabletEnabled)
		{
			StopTablet();
			StartTablet();
		}
	}

	private async void TabletNewCode_Click(object sender, RoutedEventArgs e)
	{
		await TabletPost("newcode", null);
		TabletRefresh();
	}

	private async void TabletPack_Click(object sender, RoutedEventArgs e)
	{
		List<TabletItem> sel = SelectedTablet();
		if (sel.Count == 0)
		{
			return;
		}
		List<TabletItem> empty = sel.Where(i => i.Translated == 0).ToList();
		if (empty.Count == sel.Count)
		{
			MessageBox.Show(this, "选中的作品还没有译图（manga_translator_work\\result 是空的），翻译完再打包。", "平板收发");
			return;
		}
		if (await TabletPost("pack", new Dictionary<string, object> { ["gids"] = sel.Where(i => i.Translated > 0).Select(i => i.Gid).ToArray() }))
		{
			FooterStatus.Text = $"已开始打包 {sel.Count - empty.Count} 本" + (empty.Count > 0 ? $"，{empty.Count} 本还没有译图，跳过" : "");
		}
		TabletRefresh();
	}

	private async void TabletRender_Click(object sender, RoutedEventArgs e)
	{
		List<TabletItem> sel = SelectedTablet();
		if (sel.Count > 0 && await TabletPost("render", new Dictionary<string, object> { ["gids"] = sel.Select(i => i.Gid).ToArray() }))
		{
			FooterStatus.Text = $"已排队导入翻译并渲染 {sel.Count} 本，渲染完自动打包送回";
		}
		TabletRefresh();
	}

	private async void TabletOcrAgain_Click(object sender, RoutedEventArgs e)
	{
		List<TabletItem> sel = SelectedTablet();
		if (sel.Count > 0 && await TabletPost("ocr", new Dictionary<string, object> { ["gids"] = sel.Select(i => i.Gid).ToArray() }))
		{
			FooterStatus.Text = $"已排队重新识别 {sel.Count} 本";
		}
		TabletRefresh();
	}

	private string TabletPromptTemplate => string.IsNullOrWhiteSpace(_gs.TabletPrompt) ? TabletPrompt.DefaultTemplate : _gs.TabletPrompt;

	/// <summary>点的是哪一行：行里的按钮取自己那一行，右键菜单取选中的那一行。</summary>
	private TabletItem TabletRowOf(object sender) => (sender as FrameworkElement)?.DataContext as TabletItem ?? TabletGrid.SelectedItem as TabletItem;

	/// <summary>状态旁的「停止」：结束这一本正在跑的识别原文或渲染（或取消排队），下次再点从没做完的页接着做。</summary>
	private async void TabletRowStop_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item && await TabletPost("stop", new Dictionary<string, object> { ["gids"] = new[] { item.Gid } }))
		{
			FooterStatus.Text = $"正在停止「{item.Title}」…";
		}
	}

	/// <summary>列表每一行的「复制提示词」：按模板生成这一本的提示词复制到剪贴板。</summary>
	private void TabletRowPrompt_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item)
		{
			CopyTabletText(TabletPrompt.Fill(TabletPromptTemplate, _gs.Root, item), $"「{item.Title}」的提示词");
		}
	}

	private void TabletCopyGid_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item)
		{
			CopyTabletText(item.Gid.ToString(), "编号 " + item.Gid);
		}
	}

	private void TabletCopyTitle_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item)
		{
			CopyTabletText(item.Title ?? "", "标题");
		}
	}

	private void TabletCopyBoth_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item)
		{
			CopyTabletText($"{item.Gid} {item.Title}", "编号和标题");
		}
	}

	private void TabletCopyLocation_Click(object sender, RoutedEventArgs e)
	{
		if (TabletRowOf(sender) is TabletItem item)
		{
			CopyTabletText(item.Location ?? "", "文件位置");
		}
	}

	private void CopyTabletText(string text, string what)
	{
		try
		{
			Clipboard.SetText(text);
			FooterStatus.Text = "已复制" + what;
		}
		catch (Exception ex)
		{
			FooterStatus.Text = "复制失败（剪贴板被别的程序占用？）：" + ex.Message;
		}
	}

	private void TabletPromptTemplate_Click(object sender, RoutedEventArgs e)
	{
		PromptWindow dlg = new PromptWindow(TabletPromptTemplate) { Owner = this };
		if (dlg.ShowDialog() != true)
		{
			return;
		}
		string t = dlg.Template.Trim();
		_gs.TabletPrompt = t.Length == 0 || t == TabletPrompt.DefaultTemplate.Trim() ? null : t;
		SaveGui();
		FooterStatus.Text = "提示词模板已保存";
	}

	private async void TabletRemove_Click(object sender, RoutedEventArgs e)
	{
		List<TabletItem> sel = SelectedTablet();
		if (sel.Count > 0 && await TabletPost("remove", new Dictionary<string, object> { ["gids"] = sel.Select(i => i.Gid).ToArray() }))
		{
			FooterStatus.Text = $"已移出 {sel.Count} 本（文件都还在）";
		}
		TabletRefresh();
	}

	private void TabletOpenWork_Click(object sender, RoutedEventArgs e)
	{
		foreach (TabletItem it in SelectedTablet().Take(5))
		{
			OpenTabletPath(Path.Combine(_gs.Root, it.Folder, it.Gid.ToString()));
		}
	}

	private void TabletOpenItem_Click(object sender, RoutedEventArgs e)
	{
		foreach (TabletItem it in SelectedTablet().Take(5))
		{
			OpenTabletPath(Path.Combine(_gs.Root, it.Folder, it.Dir));
		}
	}

	private void TabletGrid_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (TabletGrid.SelectedItem is TabletItem it)
		{
			OpenTabletPath(Path.Combine(_gs.Root, it.Folder, it.Gid.ToString()));
		}
	}

	private void OpenTabletPath(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "打不开：" + ex.Message, "平板收发");
		}
	}
}
