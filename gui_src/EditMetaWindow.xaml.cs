using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace EHUploader;

/// <summary>Edits the title and tags of one work; saved as upload_meta.json in its folder.</summary>
public partial class EditMetaWindow : FluentWindow
{
	public const string FileName = "upload_meta.json";

	private readonly WorkItem _item;

	public EditMetaWindow(WorkItem item)
	{
		InitializeComponent();
		_item = item;
		HeadText.Text = item.Title + "\n" + item.Author;
		if (item.Partial)
		{
			string range = item.TranslatedPages.Count > 0 ? $"翻译的是原书第 {item.TranslatedPages.First()}～{item.TranslatedPages.Last()} 页。" : "";
			string how = item.Override
				? "下面是你之前保存过的标题；不对的话，在下方目录里点对应的那一篇会重新填好。"
				: item.AutoNamed.Count > 0
				? "标题已按原画廊评论里的目录自动填好（匹配到：" + string.Join("；", item.AutoNamed) + "），英文标题里的篇名已自动转成罗马音，觉得不对可以直接改。"
				+ (item.Split > 1 ? $"这一本翻译了 {item.Split} 篇，已拆成 {item.Split} 个上传任务，这里只改这一篇。" : "")
				: "请把标题里的 ◆篇名◆ 换成这一篇的标题（合集或杂志还要把 ◆作者◆ 换成这一篇的作者）。";
			PartialBar.Message = $"这本 cbz 只有 {item.Pages} 页，原画廊有 {item.OrigPages} 页，{range}{how}"
				+ "标签里不属于这一篇的属性要手动删掉；如果其实是完整作品，点左下角「按完整作品上传」。";
			PartialBar.IsOpen = true;
		}
		else if (item.NoSource)
		{
			// 原画廊在 e-hentai 上找不到：信息取自 hitomi 或按文件标题生成，点「保存」就算确认
			PartialBar.Title = "原画廊在 e-hentai 上找不到";
			PartialBar.Message = item.SourceUrl.Length > 0
				? "原画廊可能已被删除。下面的分类、标签和发布时间取自 hitomi 上的同一本（点卡片标题可以打开），确认没问题后点「保存」；简介里的 raw 链接会指向 hitomi。"
				: "原画廊可能已被删除，别的站上也没找到。下面的标题和标签是按文件里的标题生成的，请补上作品的属性标签（female:… 之类）后点「保存」；简介里不加 raw 链接。";
			PartialBar.IsOpen = true;
			WholeButton.Visibility = Visibility.Collapsed;
		}
		else
		{
			PartialBar.Visibility = Visibility.Collapsed;
			WholeButton.Visibility = Visibility.Collapsed;
		}
		if (item.Partial && item.TocOptions.Count > 0)
		{
			// 目录列成可点选的列表：点哪一篇，就填这一篇的标题、作者和标签
			TocList.ItemsSource = item.TocOptions.Select(o => new TocEntry(o)).ToList();
			TocPanel.Visibility = Visibility.Visible;
			TocPanel.IsExpanded = true;
		}
		else if (item.Toc.Count > 0)
		{
			TocBox.Text = string.Join(Environment.NewLine, item.Toc);
			TocBox.Visibility = Visibility.Visible;
			TocList.Visibility = Visibility.Collapsed;
			TocPanel.Header = "原画廊评论里的目录";
			TocPanel.Visibility = Visibility.Visible;
			TocPanel.IsExpanded = item.AutoNamed.Count == 0;
		}
		// 已自定义或自动命名成功时，Jp / En / TagList 就是当前生效的值；否则节选用带占位符的建议
		bool current = item.Override || !item.Partial || item.AutoNamed.Count > 0;
		JpBox.Text = current ? item.Jp : item.SuggestJp;
		EnBox.Text = current ? item.En : item.SuggestEn;
		TagsBox.Text = string.Join(Environment.NewLine, current ? item.TagList : item.SuggestTags);
		ResetButton.IsEnabled = item.Override;
		ShowNotices();
		ShowPicker();
		SelectCurrentTocEntry();
		_initial = (JpBox.Text, EnBox.Text, TagsBox.Text);
	}

	/// <summary>目录列表的一行。</summary>
	private sealed class TocEntry
	{
		public TocEntry(Dictionary<string, object> o)
		{
			Data = o;
			Page = (int)Json.Long(o, "page");
			string en = Json.Str(o, "author_en"), jp = Json.Str(o, "author_jp");
			Author = jp.Length > 0 && jp != en ? $"{jp} / {en}" : en;
			Title = Json.Str(o, "title");
		}

		public Dictionary<string, object> Data { get; }
		public int Page { get; }
		public bool Odd => Json.Bool(Data, "odd");
		/// <summary>页码和前后条目对不上（比如目录末尾的附录），可以手动选，但不参与自动匹配。</summary>
		public string PageText => Odd ? $"p.{Page} ?" : $"p.{Page}";
		public string Author { get; }
		public string Title { get; }
	}

	private bool _tocReady;

	/// <summary>先高亮翻译页码所在的那一篇（只是标出来，不改标题）；点了才填。</summary>
	private void SelectCurrentTocEntry()
	{
		if (TocList.ItemsSource is List<TocEntry> list && list.Count > 0 && _item.TranslatedPages.Count > 0)
		{
			int first = _item.TranslatedPages.First();
			TocEntry hit = list.Where(x => x.Page <= first).OrderByDescending(x => x.Page).FirstOrDefault();
			if (hit != null)
			{
				TocList.SelectedItem = hit;
				TocList.ScrollIntoView(hit);
			}
		}
		_tocReady = true;
	}

	private void TocList_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		if (!_tocReady || TocList.SelectedItem is not TocEntry entry)
		{
			return;
		}
		JpBox.Text = Json.Str(entry.Data, "jp");
		EnBox.Text = Json.Str(entry.Data, "en");
		List<string> tags = Json.List(entry.Data, "tags").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
		TagsBox.Text = string.Join(Environment.NewLine, tags);
		// 作者下拉框跟着选到这一篇的作者（不再触发它自己的改标题逻辑）
		if (AuthorPick.Visibility == Visibility.Visible && AuthorPick.Items.Count > 0)
		{
			string artist = tags.FirstOrDefault(t => t.StartsWith("artist:"))?.Substring(7);
			_pickerReady = false;
			AuthorPick.SelectedIndex = artist != null ? _item.Candidates.IndexOf(artist) + 1 : 0;
			_pickerReady = true;
		}
		PartialBar.Message = $"已按目录填好第 {entry.Page} 页这一篇：{entry.Author}「{entry.Title}」。英文标题里的篇名是自动转的罗马音，觉得不对可以直接改；确认无误后点「保存」。";
	}

	private bool _whole;
	private bool _pickerReady;

	/// <summary>节选：显示这一篇的第一页原图，并列出原画廊的作者供选择。</summary>
	private void ShowPicker()
	{
		if (!_item.Partial)
		{
			return;
		}
		if (File.Exists(_item.FirstPage))
		{
			try
			{
				System.Windows.Media.Imaging.BitmapImage bmp = new System.Windows.Media.Imaging.BitmapImage();
				bmp.BeginInit();
				bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
				bmp.DecodePixelWidth = 360;
				bmp.UriSource = new Uri(_item.FirstPage);
				bmp.EndInit();
				bmp.Freeze();
				FirstPageImage.Source = bmp;
			}
			catch
			{
			}
		}
		if (_item.Candidates.Count >= 2)
		{
			AuthorPick.Items.Add("（不指定）");
			foreach (string c in _item.Candidates)
			{
				// 罗马音后面附上标签库里的参考名（多为日文原名），方便对照扉页
				string jp = _item.CandidateNames.TryGetValue(c, out string n) ? n : "";
				AuthorPick.Items.Add(jp.Length > 0 ? $"{TitleCase(c)}（{jp}）" : TitleCase(c));
			}
			// 标签里已经只剩一位候选作者时，默认选中他
			List<string> current = TagsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Where(t => t.StartsWith("artist:")).Select(t => t.Substring(7).Trim()).ToList();
			int at = current.Count == 1 ? _item.Candidates.IndexOf(current[0]) : -1;
			AuthorPick.SelectedIndex = at + 1;
		}
		else
		{
			AuthorLabel.Visibility = AuthorPick.Visibility = AuthorHint.Visibility = Visibility.Collapsed;
		}
		PickPanel.Visibility = Visibility.Visible;
		_pickerReady = true;
	}

	private static string TitleCase(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

	private void FirstPage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (File.Exists(_item.FirstPage))
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_item.FirstPage) { UseShellExecute = true });
		}
	}

	/// <summary>选了作者：英文标题开头的 [作者] 和 artist 标签换成这一位；日文标题里的作者名只在还是占位符或罗马音时替换。</summary>
	private void AuthorPick_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		if (!_pickerReady || AuthorPick.SelectedIndex <= 0)
		{
			return;
		}
		string tag = _item.Candidates[AuthorPick.SelectedIndex - 1];
		string name = TitleCase(tag);
		string jpName = _item.CandidateNames.TryGetValue(tag, out string n) && n.Length > 0 ? n.Replace("推测：", "") : name;
		System.Text.RegularExpressions.Regex lead = new System.Text.RegularExpressions.Regex(@"^\s*((?:\([^)]*\)\s*)?)\[([^\]]*)\]");
		string SetLead(string title, string who, bool onlyIfPlaceholder)
		{
			System.Text.RegularExpressions.Match m = lead.Match(title);
			if (!m.Success)
			{
				return $"[{who}] {title}".Trim();
			}
			string old = m.Groups[2].Value;
			bool replaceable = old.Contains("◆") || System.Text.RegularExpressions.Regex.IsMatch(old, @"^[\x00-\x7f]*$");
			return onlyIfPlaceholder && !replaceable ? title : m.Groups[1].Value + $"[{who}]" + title.Substring(m.Length);
		}
		EnBox.Text = SetLead(EnBox.Text, name, false);
		// 日文标题：作者还是占位符或罗马音时，换成标签库的参考名（纯汉字的名字可能是简体，请对照扉页）
		JpBox.Text = SetLead(JpBox.Text, jpName, true);
		List<string> tags = TagsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(t => t.Trim()).Where(t => t.Length > 0 && !t.StartsWith("artist:")).ToList();
		tags.Insert(0, "artist:" + tag);
		TagsBox.Text = string.Join(Environment.NewLine, tags);
	}

	private (string jp, string en, string tags) _initial;

	/// <summary>原画廊评论里命中关键词的评论：完整列出，勾选确认后才能上传。</summary>
	private void ShowNotices()
	{
		List<Dictionary<string, object>> all = _item.CommentNotices;
		if (all.Count == 0)
		{
			return;
		}
		List<Dictionary<string, object>> pending = _item.PendingNotices;
		bool block = pending.Any(n => Json.Str(n, "level") == "block");
		NoticeBar.Severity = pending.Count == 0 ? InfoBarSeverity.Success : (block ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
		NoticeBar.Title = pending.Count == 0 ? "原画廊评论已确认" : (block ? "原画廊评论里有限制说法" : "原画廊评论里有需要确认的内容");
		NoticeBar.Message = pending.Count == 0
			? "下面这些评论之前已经确认过，可以上传。以后出现新的同类评论还会再提示。"
			: $"有 {pending.Count} 条评论提到{string.Join("、", pending.Select(n => "「" + Json.Str(n, "keyword") + "」").Distinct())}。请看下面的原评论判断能不能上传，确认后勾选下方的选项再保存。";
		NoticeBar.IsOpen = true;
		NoticeBar.Visibility = Visibility.Visible;
		NoticeBox.Text = string.Join(Environment.NewLine + Environment.NewLine, all.Select(n =>
			$"【{(Json.Str(n, "level") == "block" ? "拦截" : "待确认")}：{Json.Str(n, "keyword")}】{Json.Str(n, "author")}  {Json.Str(n, "posted")}"
			+ (Json.Bool(n, "confirmed") ? "（已确认）" : "") + Environment.NewLine + Json.Str(n, "text").Trim()));
		ConfirmNotices.IsChecked = pending.Count == 0;
		ConfirmNotices.IsEnabled = pending.Count > 0;
		NoticePanel.Visibility = Visibility.Visible;
	}

	private string MetaPath => Path.Combine(_item.Folder, FileName);

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		string jp = JpBox.Text.Trim(), en = EnBox.Text.Trim();
		if (en.Length == 0)
		{
			MessageBox.Show(this, "英文 / 罗马音标题不能为空，它是 e-hentai 的主标题。", "编辑上传信息");
			return;
		}
		if (en.Contains("◆") || jp.Contains("◆"))
		{
			if (MessageBox.Show(this, "标题里还有 ◆ 占位符没替换，这部作品会被拦下不能上传。仍然保存吗？", "编辑上传信息",
				MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
			{
				return;
			}
		}
		List<string> tags = TagsBox.Text.Split(new[] { '\r', '\n', ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
		Dictionary<string, object> mine = new Dictionary<string, object> { ["en"] = en, ["jp"] = jp, ["tags"] = tags };
		Dictionary<string, object> data = ReadMeta();
		if (ConfirmNotices.IsChecked == true && _item.PendingNotices.Count > 0)
		{
			// 记下确认过的评论 id；以后出现新的同类评论还会再提示
			IEnumerable<string> old = data.TryGetValue("confirmed_comments", out object c) && c is object[] arr ? arr.Select(x => x?.ToString()) : Enumerable.Empty<string>();
			data["confirmed_comments"] = old.Concat(_item.CommentNotices.Select(n => Json.Str(n, "id"))).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
		}
		if (_whole && JpBox.Text == _item.DefaultJp && EnBox.Text == _item.DefaultEn)
		{
			// 按完整作品上传：只记一个标记，标题和标签继续自动生成（以后改了生成规则也会跟着更新）
			data["whole"] = true;
			data.Remove("en");
			data.Remove("jp");
			data.Remove("tags");
			data.Remove("parts");
			WriteMeta(data);
			DialogResult = true;
			return;
		}
		if ((JpBox.Text, EnBox.Text, TagsBox.Text) == _initial && !_item.Partial && !_item.NoSource)
		{
			// 只确认了评论、没改标题和标签：不写入自定义标题，保持自动生成
			WriteMeta(data);
			DialogResult = true;
			return;
		}
		if (_item.PartPage > 0)
		{
			// 拆分出的单篇：存在 parts["起始页"] 下，不影响同一本的其他篇
			Dictionary<string, object> parts = data.TryGetValue("parts", out object p) && p is Dictionary<string, object> pd ? pd : new Dictionary<string, object>();
			parts[_item.PartPage.ToString()] = mine;
			data["parts"] = parts;
		}
		else
		{
			foreach (KeyValuePair<string, object> kv in mine)
			{
				data[kv.Key] = kv.Value;
			}
		}
		WriteMeta(data);
		DialogResult = true;
	}

	private Dictionary<string, object> ReadMeta()
	{
		try
		{
			return File.Exists(MetaPath) ? Json.Parse(File.ReadAllText(MetaPath, Encoding.UTF8)) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
		}
		catch
		{
			return new Dictionary<string, object>();
		}
	}

	private void WriteMeta(Dictionary<string, object> data)
	{
		if (data.Count == 0)
		{
			File.Delete(MetaPath);
			return;
		}
		File.WriteAllText(MetaPath, Json.Serialize(data), new UTF8Encoding(false));
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(this, "删除这部作品的自定义标题和标签，改回从原画廊自动生成的信息？", "编辑上传信息",
			MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			Dictionary<string, object> data = ReadMeta();
			if (_item.PartPage > 0)
			{
				if (data.TryGetValue("parts", out object p) && p is Dictionary<string, object> parts)
				{
					parts.Remove(_item.PartPage.ToString());
					if (parts.Count == 0)
					{
						data.Remove("parts");
					}
				}
			}
			else
			{
				data.Remove("en");
				data.Remove("jp");
				data.Remove("tags");
				data.Remove("whole");
			}
			WriteMeta(data);
			DialogResult = true;
		}
	}

	private void Whole_Click(object sender, RoutedEventArgs e)
	{
		_whole = true;
		JpBox.Text = _item.DefaultJp;
		EnBox.Text = _item.DefaultEn;
		TagsBox.Text = string.Join(Environment.NewLine, _item.DefaultTags);
		PartialBar.Message = "已改为按完整作品上传：标题和标签用原画廊的（自动生成），点「保存」生效。";
	}

	private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
