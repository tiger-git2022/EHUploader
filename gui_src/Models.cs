using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace EHUploader;

public abstract class Bindable : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler PropertyChanged;

	protected void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		Notify(name);
		return true;
	}
}

/// <summary>One translated work: a refilled cbz plus the metadata of its original gallery.</summary>
public sealed class WorkItem : Bindable
{
	private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F)));
	private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)));
	private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
	private static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00)));
	private static readonly Brush Grey =Freeze(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)));

	public long Gid { get; set; }
	/// <summary>Upload task key: the gid, or "gid-p23" for one story split out of a book.</summary>
	public string Key { get; set; }
	public int Split { get; set; }
	public int PartPage { get; set; }
	public string ThumbEntry { get; set; }
	public string SourceUrl { get; set; }
	/// <summary>这一篇第一页的日文原图（节选才有），编辑窗口里看扉页用。</summary>
	public string FirstPage { get; set; }
	/// <summary>原画廊的全部作者（artist 标签），编辑窗口里选这一篇的作者用。</summary>
	public List<string> Candidates { get; set; } = new List<string>();
	/// <summary>作者标签 → 参考名（EhTagTranslation 标签库，多为日文原名）。</summary>
	public Dictionary<string, string> CandidateNames { get; set; } = new Dictionary<string, string>();
	public bool OcrGuessed { get; set; }
	/// <summary>原画廊在 e-hentai 上找不到（已删除），信息取自 hitomi 或按文件标题生成，要在编辑窗口确认。</summary>
	public bool NoSource { get; set; }
	/// <summary>点标题时打开的页面：已发布的画廊，没上传过就打开原画廊。</summary>
	public string LinkUrl => string.IsNullOrEmpty(Url) ? SourceUrl : Url;
	public string LinkTip => string.IsNullOrEmpty(Url) ? "点击在浏览器里打开原画廊" : "点击在浏览器里打开已发布的画廊";
	public string Folder { get; set; }
	/// <summary>翻译工作用的数字文件夹（如 漫画根目录\2026-08-25\222222）。</summary>
	public string WorkDir { get; set; }
	/// <summary>「2026-08-25 / 222222」：在哪个日期批次的哪个数字文件夹。</summary>
	public string FolderText => $"{Path.GetFileName(Path.GetDirectoryName(Folder ?? "") ?? "")} / {(string.IsNullOrEmpty(WorkDir) ? Key : Path.GetFileName(WorkDir))}";
	/// <summary>作者那一行后面带上所在文件夹。</summary>
	public string AuthorLine => $"{Author}  ·  📁 {FolderText}";
	public string Cbz { get; set; }
	public string En { get; set; }
	public string Jp { get; set; }
	public string Category { get; set; }
	public DateTime Refilled { get; set; }
	public DateTime? Posted { get; set; }
	public int Pages { get; set; }
	public double Mb { get; set; }
	public int Batches { get; set; }
	public int Tags { get; set; }
	public List<string> Problems { get; set; } = new List<string>();
	public string State { get; set; }

	private string _url = "";
	public string Url { get => _url; set { if (Set(ref _url, value ?? "")) { Notify(nameof(LinkVisibility)); Notify(nameof(LinkUrl)); Notify(nameof(LinkTip)); } } }

	public bool Partial { get; set; }
	public int OrigPages { get; set; }
	public bool Override { get; set; }
	public List<string> TagList { get; set; } = new List<string>();
	public string SuggestEn { get; set; }
	public string SuggestJp { get; set; }
	public List<string> SuggestTags { get; set; } = new List<string>();
	public string DefaultEn { get; set; }
	public string DefaultJp { get; set; }
	public List<string> DefaultTags { get; set; } = new List<string>();
	public List<string> Toc { get; set; } = new List<string>();
	/// <summary>目录里每一篇对应的建议标题和标签（page / author_en / author_jp / title / en / jp / tags）。</summary>
	public List<Dictionary<string, object>> TocOptions { get; set; } = new List<Dictionary<string, object>>();
	public List<string> AutoNamed { get; set; } = new List<string>();
	public List<int> TranslatedPages { get; set; } = new List<int>();
	/// <summary>原画廊评论里命中的限制说法（禁止、勿扰、预定 / 组内、翻译、AI）。</summary>
	public List<Dictionary<string, object>> CommentNotices { get; set; } = new List<Dictionary<string, object>>();
	public List<Dictionary<string, object>> PendingNotices => CommentNotices.Where(n => !Json.Bool(n, "confirmed")).ToList();
	public bool HasBlockNotice => PendingNotices.Any(n => Json.Str(n, "level") == "block");
	public string NoticeText => HasUpdate
		? $"已发布，第 {string.Join("、", UpdatePages.Take(15))}{(UpdatePages.Count > 15 ? " 等" : "")} 页改过（共 {UpdatePages.Count} 页），上传会发布新版本，评论保留"
		: Problems.Count == 0 && AutoNamed.Count > 0 && !Override ? "篇名取自原画廊评论目录：" + string.Join("；", AutoNamed) : "";
	public Visibility NoticeVisibility => NoticeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	/// <summary>Tooltip with the full problem / notice text; null hides the tooltip.</summary>
	public string DetailText => (ProblemText + (NoticeText.Length > 0 ? "\n" + NoticeText : "")).Trim() is string s && s.Length > 0 ? s : null;
	public List<string> Artists { get; set; } = new List<string>();
	public List<string> Groups { get; set; } = new List<string>();

	private static readonly Regex Lead = new Regex(@"^\s*(?:\((?<event>[^)]*)\)\s*)?\[(?<who>[^\]]+)\]\s*(?<rest>.*)$", RegexOptions.Compiled);
	private static readonly Regex CircleAuthor = new Regex(@"^(?<circle>.*?)\s*\((?<author>[^()]+)\)\s*$", RegexOptions.Compiled);

	/// <summary>The Japanese title without the leading "(event) [circle (author)]" and the "[中国翻訳]" marker.</summary>
	public string Title
	{
		get
		{
			string t = !string.IsNullOrEmpty(Jp) ? Jp : (!string.IsNullOrEmpty(En) ? En : Path.GetFileName(Folder));
			Match m = Lead.Match(t);
			if (m.Success && m.Groups["rest"].Value.Trim().Length > 0)
			{
				t = m.Groups["rest"].Value;
			}
			return t.Replace("[中国翻訳]", "").Replace("[Chinese]", "").Replace("  ", " ").Trim();
		}
	}

	/// <summary>Full upload titles, for the tooltip.</summary>
	public string FullTitle => string.IsNullOrEmpty(Jp) ? En : Jp + "\n" + En;

	public string AuthorName => ParseAuthor().author;
	public string CircleName => ParseAuthor().circle;

	/// <summary>"作者 X · 社团 Y" from the Japanese title, falling back to the artist / group tags.</summary>
	public string Author
	{
		get
		{
			(string author, string circle) = ParseAuthor();
			List<string> parts = new List<string>();
			if (author.Length > 0)
			{
				parts.Add("作者 " + author);
			}
			if (circle.Length > 0 && circle != author)
			{
				parts.Add("社团 " + circle);
			}
			return parts.Count > 0 ? string.Join(" · ", parts) : "作者未知";
		}
	}

	private (string author, string circle) ParseAuthor()
	{
		{
			string author = "", circle = "";
			Match m = Lead.Match(!string.IsNullOrEmpty(Jp) ? Jp : En ?? "");
			if (m.Success)
			{
				// 标题里的 [社团 (作者)] 优先；只有一个名字时，原画廊只有 group 标签说明它是社团
				string who = m.Groups["who"].Value.Trim();
				Match ca = CircleAuthor.Match(who);
				if (ca.Success && ca.Groups["circle"].Value.Length > 0)
				{
					circle = ca.Groups["circle"].Value.Trim();
					author = ca.Groups["author"].Value.Trim();
				}
				else if (Artists.Count == 0 && Groups.Count > 0)
				{
					circle = who;
				}
				else
				{
					author = who;
				}
			}
			else
			{
				author = string.Join("、", Artists);
				circle = string.Join("、", Groups);
			}
			return (author, circle);
		}
	}

	public string PagesText => Split > 1 && TranslatedPages.Count > 0
		? $"{Pages} 页（原书第 {TranslatedPages.First()}～{TranslatedPages.Last()} 页，拆分自同一本的 {Split} 篇）"
		: Partial ? $"{Pages} / {OrigPages} 页（节选）" : $"{Pages} 页";
	public string Info => $" · {Mb:0} MB · {Category} · 标签 {Tags} 个" + (Batches > 1 ? $" · 分 {Batches} 批上传" : "");
	public double Months => Posted.HasValue ? (DateTime.Now - Posted.Value).TotalDays / 30.4375 : -1;
	public string MonthsText => Months < 0 ? "—" : $"{Months:0.0} 个月";
	public string PostedText => Posted.HasValue ? "原作 " + Posted.Value.ToString("yyyy-MM-dd") : "原作时间未知";
	public string RefilledText => "回填 " + Refilled.ToString("MM-dd HH:mm");
	public string ProblemText => string.Join("；", Problems);
	public Visibility ProblemVisibility => Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	public Visibility LinkVisibility => string.IsNullOrEmpty(Url) ? Visibility.Collapsed : Visibility.Visible;
	/// <summary>已发布、之后本地又改过的页（按作品内页码）；有的话上传时会发布新版本。</summary>
	public List<int> UpdatePages { get; set; } = new List<int>();
	public bool HasUpdate => State == "done" && UpdatePages.Count > 0;
	public bool Done => State == "done" && !HasUpdate;

	private bool _excluded;
	/// <summary>永久排除、不再上传（excluded.json）。</summary>
	public bool Excluded { get => _excluded; set { if (Set(ref _excluded, value)) Notify(nameof(ExcludeMenuText)); } }
	public string ExcludeMenuText => Excluded ? "取消排除（恢复到作品列表）" : "永久排除（不再上传）";

	private ImageSource _thumb;
	public ImageSource Thumb { get => _thumb; set => Set(ref _thumb, value); }

	private bool _selected;
	public bool Selected { get => _selected; set => Set(ref _selected, value); }

	private bool _canSelect;
	public bool CanSelect { get => _canSelect; set => Set(ref _canSelect, value); }

	private bool _eligible;
	public bool Eligible { get => _eligible; private set => Set(ref _eligible, value); }

	private string _badge = "";
	public string Badge { get => _badge; set => Set(ref _badge, value); }

	private Brush _badgeBrush = Grey;
	public Brush BadgeBrush { get => _badgeBrush; set => Set(ref _badgeBrush, value); }

	private double _rowOpacity = 1;
	public double RowOpacity { get => _rowOpacity; set => Set(ref _rowOpacity, value); }

	/// <summary>Re-evaluates the item against the "original posted at least N months ago" rule.</summary>
	public void Evaluate(double minMonths, bool resetSelection)
	{
		Eligible = !Done && !Excluded && Problems.Count == 0 && (HasUpdate || Months >= minMonths);   // 更新不受「原作满几个月」限制
		if (Excluded)
		{
			Badge = "已排除";
			BadgeBrush = Grey;
			CanSelect = false;
			Selected = false;
			RowOpacity = 0.5;
			return;
		}
		if (Done)
		{
			Badge = "已上传";
			BadgeBrush = Blue;
			CanSelect = false;
			Selected = false;
			RowOpacity = 0.75;
			return;
		}
		if (Problems.Count > 0)
		{
			bool needsTitle = Partial && Problems.All(p => p.Contains("节选") || p.Contains("◆") || p.Contains("推测"));
			bool noticeOnly = PendingNotices.Count > 0 && Problems.All(p => p.StartsWith("原画廊评论"));
			needsTitle = needsTitle || (NoSource && Problems.All(p => p.StartsWith("原画廊在 e-hentai 上找不到")));
			Badge = noticeOnly ? (HasBlockNotice ? "评论有限制" : "评论待确认") : (needsTitle ? (OcrGuessed || NoSource ? "需确认" : "需填篇名") : "需处理");
			BadgeBrush = noticeOnly ? (HasBlockNotice ? Red : Orange) : (needsTitle ? Orange : Red);
			CanSelect = false;
			Selected = false;
			RowOpacity = 1;
			return;
		}
		CanSelect = true;
		if (HasUpdate)
		{
			Badge = "有更新";
			BadgeBrush = Orange;
			RowOpacity = 1;
		}
		else if (Eligible)
		{
			Badge = "符合";
			BadgeBrush = Green;
			RowOpacity = 1;
		}
		else
		{
			Badge = Months < 0 ? "时间未知" : $"还差 {Math.Ceiling((minMonths - Months) * 30.4375):0} 天";
			BadgeBrush = Grey;
			RowOpacity = 0.55;
		}
		if (resetSelection)
		{
			Selected = Eligible;
		}
	}

	// ---- upload queue ----

	private string _queueStatus = "等待中";
	public string QueueStatus { get => _queueStatus; set => Set(ref _queueStatus, value); }

	private double _queuePercent;
	public double QueuePercent { get => _queuePercent; set => Set(ref _queuePercent, value); }

	private bool _queueBusy;
	public bool QueueBusy { get => _queueBusy; set => Set(ref _queueBusy, value); }

	// 队列状态文字用亮一点的颜色，深色背景（窗口有模糊透明）上也看得清
	private static readonly Brush TextGrey = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));
	private static readonly Brush TextBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xA6, 0xFF)));
	private static readonly Brush TextGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x4C)));
	private static readonly Brush TextRed = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x4F)));

	private Brush _queueBrush = TextGrey;
	public Brush QueueBrush { get => _queueBrush; set => Set(ref _queueBrush, value); }

	public void QueueReset()
	{
		QueueStatus = "等待中";
		QueuePercent = 0;
		QueueBusy = false;
		QueueBrush = TextGrey;
	}

	public void QueueRunning(string text)
	{
		QueueStatus = text;
		QueueBrush = TextBlue;
	}

	public void QueueSucceeded(string text)
	{
		QueueStatus = text;
		QueuePercent = 100;
		QueueBusy = false;
		QueueBrush = TextGreen;
	}

	public void QueueFailed(string text)
	{
		QueueStatus = text;
		QueueBusy = false;
		QueueBrush = TextRed;
	}

	private static Brush Freeze(SolidColorBrush b)
	{
		b.Freeze();
		return b;
	}

	public static WorkItem FromJson(Dictionary<string, object> d)
	{
		WorkItem w = new WorkItem
		{
			Gid = Json.Long(d, "gid"),
			Key = Json.Str(d, "key"),
			Excluded = Json.Bool(d, "excluded"),
			Split = (int)Json.Long(d, "split"),
			PartPage = (int)Json.Long(d, "part_page"),
			ThumbEntry = Json.Str(d, "thumb_entry"),
			SourceUrl = Json.Str(d, "source_url"),
			FirstPage = Json.Str(d, "first_page"),
			Candidates = Json.List(d, "candidates").Select(x => x?.ToString() ?? "").ToList(),
			CandidateNames = (d.TryGetValue("candidate_names", out object cn) && cn is Dictionary<string, object> names
				? names.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") : new Dictionary<string, string>()),
			OcrGuessed = Json.Bool(d, "ocr_guessed"),
			NoSource = Json.Bool(d, "nosrc"),
			CommentNotices = Json.List(d, "notices").OfType<Dictionary<string, object>>().ToList(),
			Folder = Json.Str(d, "folder"),
			WorkDir = Json.Str(d, "work_dir"),
			Cbz = Json.Str(d, "cbz"),
			En = Json.Str(d, "en"),
			Jp = Json.Str(d, "jp"),
			Category = Json.Str(d, "category"),
			Refilled = Json.Date(d, "refilled") ?? DateTime.MinValue,
			Posted = Json.Date(d, "posted"),
			Pages = (int)Json.Long(d, "pages"),
			Mb = Json.Num(d, "mb"),
			Batches = (int)Json.Long(d, "batches"),
			Tags = (int)Json.Long(d, "tags"),
			Problems = Json.List(d, "problems").Select(x => x?.ToString() ?? "").ToList(),
			Partial = Json.Bool(d, "partial"),
			OrigPages = (int)Json.Long(d, "orig_pages"),
			Override = Json.Bool(d, "override"),
			TagList = Json.List(d, "tag_list").Select(x => x?.ToString() ?? "").ToList(),
			SuggestEn = Json.Str(d, "suggest_en"),
			SuggestJp = Json.Str(d, "suggest_jp"),
			SuggestTags = Json.List(d, "suggest_tags").Select(x => x?.ToString() ?? "").ToList(),
			DefaultEn = Json.Str(d, "default_en"),
			DefaultJp = Json.Str(d, "default_jp"),
			DefaultTags = Json.List(d, "default_tags").Select(x => x?.ToString() ?? "").ToList(),
			Toc = Json.List(d, "toc").Select(x => x?.ToString() ?? "").ToList(),
			TocOptions = Json.List(d, "toc_options").OfType<Dictionary<string, object>>().ToList(),
			AutoNamed = Json.List(d, "auto_named").Select(x => x?.ToString() ?? "").ToList(),
			TranslatedPages = Json.List(d, "translated_pages").Select(x => Convert.ToInt32(x)).ToList(),
			Artists = Json.List(d, "artists").Select(x => x?.ToString() ?? "").ToList(),
			Groups = Json.List(d, "groups").Select(x => x?.ToString() ?? "").ToList(),
			State = Json.Str(d, "status"),
			UpdatePages = d.TryGetValue("update", out object up) && up is Dictionary<string, object> upd
				? Json.List(upd, "pages").Select(x => Convert.ToInt32(x)).ToList() : new List<int>(),
		};
		w.Url = Json.Str(d, "url");
		return w;
	}
}

/// <summary>One comment under an uploaded gallery.</summary>
public sealed class CommentItem : Bindable
{
	private static readonly Brush Plus = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x4C));
	private static readonly Brush Minus = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x4F));

	public long Id { get; set; }
	public string Author { get; set; }
	public string PostedText { get; set; }
	public string Score { get; set; }
	public string Text { get; set; }
	public Brush ScoreBrush => Score.StartsWith("-") ? Minus : Plus;

	private bool _isNew;
	public bool IsNew { get => _isNew; set { if (Set(ref _isNew, value)) Notify(nameof(NewVisibility)); } }
	public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;

	public static CommentItem FromJson(Dictionary<string, object> d)
	{
		DateTime? t = Json.Date(d, "posted");
		long.TryParse(Json.Str(d, "id"), out long id);
		return new CommentItem
		{
			Id = id,
			Author = Json.Str(d, "author"),
			PostedText = t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm") : "",
			Score = Json.Str(d, "score"),
			Text = Json.Str(d, "text"),
		};
	}
}

/// <summary>A gallery from "My Uploads", with its latest comments.</summary>
public sealed class GalleryItem : Bindable
{
	public long Gid { get; set; }
	public string Token { get; set; }
	public string Url { get; set; }
	public string Title { get; set; }
	public string En { get; set; }
	public string Thumb { get; set; }
	public string Info { get; set; }
	/// <summary>网站上的原始英文 / 日文标题（修改标题时用）。</summary>
	public string RawEn { get; set; } = "";
	public string RawJp { get; set; } = "";
	public System.Collections.ObjectModel.ObservableCollection<CommentItem> Comments { get; } = new System.Collections.ObjectModel.ObservableCollection<CommentItem>();

	private int _total;
	public int Total { get => _total; set { if (Set(ref _total, value)) Notify(nameof(CommentsHeader)); } }

	private string _fetched = "";
	public string Fetched { get => _fetched; set { if (Set(ref _fetched, value ?? "")) Notify(nameof(CommentsHeader)); } }

	public string CommentsHeader => Fetched.Length == 0 ? "评论还没读取" : (Total == 0 ? "还没有评论" : $"共 {Total} 条评论，显示最新 {Comments.Count} 条");

	private int _newCount;
	public int NewCount { get => _newCount; set { if (Set(ref _newCount, value)) { Notify(nameof(NewText)); Notify(nameof(NewVisibility)); Notify(nameof(ReadButtonVisibility)); } } }
	public string NewText => $"{NewCount} 条新评论";
	public Visibility NewVisibility => NewCount > 0 ? Visibility.Visible : Visibility.Collapsed;

	private bool _keepVisible;
	/// <summary>刚回复过：「只看有新评论」时也留在列表里，点这一本的「标为已读」或「全部标为已读」才消失。</summary>
	public bool KeepVisible { get => _keepVisible; set { if (Set(ref _keepVisible, value)) Notify(nameof(ReadButtonVisibility)); } }

	/// <summary>有新评论、或者回复过还留在列表里时，显示这一本的「标为已读」按钮。</summary>
	public Visibility ReadButtonVisibility => NewCount > 0 || KeepVisible ? Visibility.Visible : Visibility.Collapsed;

	private bool _editorOpen;
	public bool EditorOpen { get => _editorOpen; set { if (Set(ref _editorOpen, value)) Notify(nameof(EditorVisibility)); } }
	public Visibility EditorVisibility => EditorOpen ? Visibility.Visible : Visibility.Collapsed;

	private string _draft = "";
	public string Draft { get => _draft; set => Set(ref _draft, value); }

	private bool _sending;
	public bool Sending { get => _sending; set { if (Set(ref _sending, value)) Notify(nameof(CanSend)); } }
	public bool CanSend => !Sending;

	private string _sendStatus = "";
	public string SendStatus { get => _sendStatus; set => Set(ref _sendStatus, value); }

	public static GalleryItem FromJson(Dictionary<string, object> d)
	{
		DateTime? posted = Json.Date(d, "posted");
		string jp = Json.Str(d, "title_jpn"), en = Json.Str(d, "title");
		string rating = Json.Str(d, "rating");
		return new GalleryItem
		{
			Gid = Json.Long(d, "gid"),
			Token = Json.Str(d, "token"),
			Url = Json.Str(d, "url"),
			Title = jp.Length > 0 ? jp : en,
			En = jp.Length > 0 ? en : "",
			RawEn = en,
			RawJp = jp,
			Thumb = Json.Str(d, "thumb_file").Length > 0 ? Json.Str(d, "thumb_file") : null,
			Info = (posted.HasValue ? "发布 " + posted.Value.ToString("yyyy-MM-dd") : "") + $" · {Json.Long(d, "pages")} 页"
				+ (rating.Length > 0 ? " · 评分 " + rating : "") + " · " + Json.Str(d, "category")
				+ (Json.Bool(d, "by_tool") ? " · 本工具上传" : ""),
		};
	}

	/// <summary>Shows every unread comment, and at least the latest <paramref name="shown"/> ones; anything newer than the last read id is marked new.</summary>
	public void SetComments(object[] list, int total, string fetched, Dictionary<string, long> seen, int shown = 3)
	{
		List<CommentItem> all = list.OfType<Dictionary<string, object>>().Select(CommentItem.FromJson).ToList();
		Total = total;
		Fetched = fetched;
		string key = Gid.ToString();
		long max = all.Count > 0 ? all.Max(c => c.Id) : 0;
		if (!seen.ContainsKey(key) && fetched.Length > 0)
		{
			seen[key] = max;   // 第一次读到时现有评论都算已读
		}
		long last = seen.TryGetValue(key, out long v) ? v : long.MaxValue;
		Comments.Clear();
		for (int i = 0; i < all.Count; i++)
		{
			all[i].IsNew = all[i].Id > last;
			if (all[i].IsNew || i < shown)
			{
				Comments.Add(all[i]);
			}
		}
		NewCount = Comments.Count(c => c.IsNew);
		Notify(nameof(CommentsHeader));
	}

	public void MarkRead(Dictionary<string, long> seen)
	{
		if (Comments.Count > 0)
		{
			string key = Gid.ToString();
			seen[key] = Math.Max(seen.TryGetValue(key, out long v) ? v : 0, Comments.Max(c => c.Id));
		}
		foreach (CommentItem c in Comments)
		{
			c.IsNew = false;
		}
		NewCount = 0;
	}
}

/// <summary>One row of the grid view.</summary>
public sealed class WorkRow
{
	public int Columns { get; set; }
	public List<WorkItem> Items { get; set; }
}

/// <summary>A row of the upload history (upload_state.json).</summary>
public sealed class HistoryRow
{
	public string SourceGid { get; set; }
	public string Title { get; set; }
	public string Status { get; set; }
	public string Finished { get; set; }
	public string MissingTags { get; set; }
	public string Url { get; set; }
	public Visibility LinkVisibility => string.IsNullOrEmpty(Url) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Window and runtime settings of the EXE (eh_gui.json next to the exe).</summary>
public sealed class GuiSettings
{
	public string Theme { get; set; } = "system";
	public string Backdrop { get; set; } = "mica";
	public int TintPercent { get; set; } = 60;
	public string Python { get; set; } = "python";
	public string Root { get; set; } = "";
	public string RefilledAfter { get; set; } = "";
	public double MinMonths { get; set; } = 3;
	public int Filter { get; set; }
	public bool OnlyNewComments { get; set; }
	/// <summary>上次批量加标签用过的标签（一行一个），下次打开时预先填好。</summary>
	public string LastBatchTags { get; set; } = "female:mind control";
	public List<string> NoticesShown { get; set; } = new List<string>();   // 已经弹窗提示过的原画廊评论（gid:评论号），同一条只提示一次
	/// <summary>0 grid, 1 compact list, 2 large covers.</summary>
	public int View { get; set; }
	public bool CloseToTray { get; set; } = true;
	public bool TrayHintShown { get; set; }
	public bool? LoginOk { get; set; }
	public string LoginChecked { get; set; } = "";

	public static GuiSettings Load(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				return new JavaScriptSerializer().Deserialize<GuiSettings>(File.ReadAllText(path)) ?? new GuiSettings();
			}
		}
		catch
		{
		}
		return new GuiSettings();
	}

	public void Save(string path)
	{
		File.WriteAllText(path, new JavaScriptSerializer().Serialize(this));
	}
}

public static class Json
{
	private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

	public static Dictionary<string, object> Parse(string text)
	{
		lock (Ser)
		{
			return Ser.DeserializeObject(text) as Dictionary<string, object>;
		}
	}

	public static string Serialize(object o)
	{
		lock (Ser)
		{
			return Ser.Serialize(o);
		}
	}

	public static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out object v) && v != null ? v.ToString() : "";

	public static double Num(Dictionary<string, object> d, string k) =>
		d != null && d.TryGetValue(k, out object v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

	public static long Long(Dictionary<string, object> d, string k) => (long)Num(d, k);

	public static bool Bool(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out object v) && v is bool b && b;

	public static bool Has(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out object v) && v != null;

	public static object[] List(Dictionary<string, object> d, string k) =>
		d != null && d.TryGetValue(k, out object v) && v is object[] a ? a : (d != null && d.TryGetValue(k, out v) && v is System.Collections.ArrayList al ? al.ToArray() : new object[0]);

	public static DateTime? Date(Dictionary<string, object> d, string k)
	{
		string s = Str(d, k);
		return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime t) ? t : null;
	}
}

/// <summary>"2.jpg" before "10.jpg".</summary>
public sealed class NaturalComparer : IComparer<string>
{
	public static readonly NaturalComparer Instance = new NaturalComparer();
	private static readonly Regex Parts = new Regex(@"\d+|\D+", RegexOptions.Compiled);

	public int Compare(string a, string b)
	{
		MatchCollection pa = Parts.Matches(a.ToLowerInvariant());
		MatchCollection pb = Parts.Matches(b.ToLowerInvariant());
		for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
		{
			string x = pa[i].Value, y = pb[i].Value;
			int c;
			if (char.IsDigit(x[0]) && char.IsDigit(y[0]))
			{
				c = x.TrimStart('0').Length.CompareTo(y.TrimStart('0').Length);
				if (c == 0)
				{
					c = string.CompareOrdinal(x.TrimStart('0'), y.TrimStart('0'));
				}
			}
			else
			{
				c = string.CompareOrdinal(x, y);
			}
			if (c != 0)
			{
				return c;
			}
		}
		return pa.Count.CompareTo(pb.Count);
	}
}
