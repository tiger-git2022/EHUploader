using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EHUploader;

/// <summary>
/// 托盘图标的右键菜单：一个无边框的 WPF-UI 小窗口，用 Windows 11 的亚克力背景（系统自己的菜单和弹出面板用的那种磨砂半透明）、
/// 圆角和 Fluent 图标，随深浅色主题。照搬 Claude 自动审批（claude-auto-pass）的 TrayMenu。
/// Windows 10 没有这种背景，用主题的纯色背景。失去焦点或按 Esc 时关闭。
/// </summary>
public sealed class TrayMenu
{
	public sealed class Entry
	{
		public string Text;

		public SymbolRegular Icon;

		public bool Enabled = true;

		public bool Bold;

		public Action Click;

		public bool Separator;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect { public int Left, Top, Right, Bottom; }

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

	private static readonly IntPtr TopMost = new IntPtr(-1);
	private const uint NoActivate = 0x0010;

	private FluentWindow _window;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OsVersionInfo
	{
		public int Size, Major, Minor, Build, Platform;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string CsdVersion;
	}

	[DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
	private static extern int RtlGetVersion(ref OsVersionInfo info);

	/// <summary>
	/// 亚克力系统背景要 Windows 11 22H2（22621）以上；更早的系统用纯色背景。
	/// 程序没有声明兼容 Win10/11 的清单，Environment.OSVersion 会报 6.2，所以直接问 RtlGetVersion 拿真实版本号。
	/// </summary>
	private static readonly bool AcrylicSupported = GetBuild() >= 22621;

	private static int GetBuild()
	{
		try
		{
			var info = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
			return RtlGetVersion(ref info) == 0 && info.Major >= 10 ? info.Build : 0;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public bool IsOpen => _window != null && _window.IsVisible;

	/// <summary>在鼠标位置打开菜单（像普通右键菜单一样）。</summary>
	public void Show(IEnumerable<Entry> entries) => Show(entries, System.Windows.Forms.Cursor.Position);

	/// <summary>在给定的屏幕坐标（物理像素）旁打开菜单。</summary>
	public void Show(IEnumerable<Entry> entries, System.Drawing.Point at)
	{
		Close();
		// 顶端对齐：窗口比内容高时，列表仍保持自然高度
		var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4), MinWidth = 240, VerticalAlignment = VerticalAlignment.Top };
		// Display 模式让文字对齐像素，和系统菜单一样清楚；默认的 Ideal 模式下小号中文发虚，亚克力背景上更明显（没有 ClearType）
		TextOptions.SetTextFormattingMode(list, TextFormattingMode.Display);
		TextOptions.SetTextHintingMode(list, TextHintingMode.Fixed);
		var w = new FluentWindow
		{
			ExtendsContentIntoTitleBar = true,
			WindowBackdropType = AcrylicSupported ? WindowBackdropType.Acrylic : WindowBackdropType.None,
			WindowCornerPreference = WindowCornerPreference.Round,
			WindowStartupLocation = WindowStartupLocation.Manual,
			ResizeMode = ResizeMode.NoResize,
			SizeToContent = SizeToContent.WidthAndHeight,
			ShowInTaskbar = false,
			ShowActivated = true,
			Topmost = true,
			MinWidth = 0,
			MinHeight = 0,
			Width = double.NaN,
			Height = double.NaN,
			Left = -32000,
			Top = -32000,
			Title = "EH 上传助手",
			// 菜单比屏幕工作区还高时（低分辨率加高缩放）可以滚动，最下面的“退出”不会跑到屏幕外
			Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
		};
		foreach (Entry e in entries)
		{
			list.Children.Add(e.Separator ? Separator() : Row(e, w));
		}

		bool closing = false;
		w.Deactivated += delegate { if (!closing) { closing = true; w.Close(); } };
		w.Closing += delegate { closing = true; };
		w.PreviewKeyDown += (s, k) => OnKey(w, list, k);
		w.Closed += delegate { if (_window == w) _window = null; };
		_window = w;
		w.Show();
		// WPF 按还有标题栏和边框的样子算窗口大小，但 WindowChrome 已经把整个窗口变成内容区，高缩放下最后一项下面会多出空白。
		// 所以直接按列表的自然大小（物理像素）设置窗口大小，之后 WPF 再改大小时也重新设一次。
		w.SizeToContent = SizeToContent.Manual;
		ApplyTheme(w);
		IntPtr handle = new WindowInteropHelper(w).Handle;
		int fits = 0;
		void Fit(bool first)
		{
			if (fits++ >= 6 || _window != w)
			{
				return;
			}
			w.UpdateLayout();
			DpiScale dpi = VisualTreeHelper.GetDpi(w);
			list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			Size size = list.DesiredSize;   // 自然大小，含列表的外边距
			int width = (int)Math.Ceiling(size.Width * dpi.DpiScaleX), height = (int)Math.Ceiling(size.Height * dpi.DpiScaleY);
			GetWindowRect(handle, out Rect r);
			// 第一次同时把窗口从屏幕外的起始位置移到点击处
			height = Math.Min(height, System.Windows.Forms.Screen.FromPoint(at).WorkingArea.Height);
			if (first || r.Right - r.Left != width || r.Bottom - r.Top != height)
			{
				Place(handle, at, width, height);
			}
		}
		Fit(true);
		w.SizeChanged += delegate { Fit(false); };
		w.DpiChanged += delegate { Fit(false); };
		SetForegroundWindow(handle);
		w.Activate();
	}

	/// <summary>键盘操作和系统菜单一样：方向键在命令间移动，回车 / 空格执行，Esc 关闭。</summary>
	private static void OnKey(Window w, Panel list, KeyEventArgs k)
	{
		if (k.Key == Key.Escape)
		{
			k.Handled = true;
			w.Close();
			return;
		}
		if (k.Key != Key.Down && k.Key != Key.Up && k.Key != Key.Home && k.Key != Key.End)
		{
			return;
		}
		k.Handled = true;
		var rows = new List<UIElement>();
		foreach (UIElement child in list.Children)
		{
			if (child.Focusable && child.IsEnabled)
			{
				rows.Add(child);
			}
		}
		if (rows.Count == 0)
		{
			return;
		}
		int current = rows.FindIndex(r => r.IsKeyboardFocused);
		int next = k.Key == Key.Home ? 0 : k.Key == Key.End ? rows.Count - 1
			: current < 0 ? (k.Key == Key.Down ? 0 : rows.Count - 1)
			: (current + (k.Key == Key.Down ? 1 : -1) + rows.Count) % rows.Count;
		Keyboard.Focus(rows[next]);
	}

	private static void ApplyTheme(FluentWindow w)
	{
		try
		{
			if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark)
			{
				WindowBackgroundManager.ApplyDarkThemeToWindow(w);
			}
			else
			{
				WindowBackgroundManager.RemoveDarkThemeFromWindow(w);
			}
		}
		catch (Exception)
		{
		}
	}

	/// <summary>设置菜单大小并放在点击处的右上方，超出工作区时翻到另一边。</summary>
	private static void Place(IntPtr handle, System.Drawing.Point at, int width, int height)
	{
		System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(at).WorkingArea;
		height = Math.Min(height, area.Height);
		int x = at.X + width <= area.Right ? at.X : at.X - width;
		int y = at.Y - height >= area.Top ? at.Y - height : at.Y;
		x = Math.Max(area.Left, Math.Min(x, area.Right - width));
		y = Math.Max(area.Top, Math.Min(y, area.Bottom - height));
		SetWindowPos(handle, TopMost, x, y, width, height, NoActivate);
	}

	private static UIElement Separator()
	{
		var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
		line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
		return line;
	}

	// 鼠标悬停和键盘焦点用同一种高亮，按下时更深，和 WPF-UI 的菜单项一样
	private static readonly Lazy<ControlTemplate> RowTemplate = new Lazy<ControlTemplate>(() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
		"<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
		"<Border x:Name='Bd' Background='Transparent' CornerRadius='4' Margin='4,1,4,1' Padding='12,7,16,7'>" +
		"<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border>" +
		"<ControlTemplate.Triggers>" +
		"<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource SubtleFillColorSecondaryBrush}'/></Trigger>" +
		"<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource SubtleFillColorSecondaryBrush}'/></Trigger>" +
		"<Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource SubtleFillColorTertiaryBrush}'/></Trigger>" +
		"<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Background' Value='Transparent'/></Trigger>" +
		"</ControlTemplate.Triggers></ControlTemplate>"));

	private static UIElement Row(Entry e, Window owner)
	{
		var text = new System.Windows.Controls.TextBlock
		{
			Text = e.Text,
			VerticalAlignment = VerticalAlignment.Center,
			FontWeight = e.Bold ? FontWeights.SemiBold : FontWeights.Normal,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 360,
		};
		var icon = new SymbolIcon { Symbol = e.Icon, FontSize = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
		bool enabled = e.Enabled && e.Click != null;
		string foreground = e.Enabled ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush";
		text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foreground);
		icon.SetResourceReference(Control.ForegroundProperty, foreground);
		var content = new StackPanel { Orientation = Orientation.Horizontal };
		if (e.Icon != SymbolRegular.Empty)
		{
			content.Children.Add(icon);
		}
		content.Children.Add(text);
		// 用真正的按钮（改成菜单项的样子），这样命令能获得焦点、回车 / 空格执行，读屏软件也能读出名字
		var row = new System.Windows.Controls.Button
		{
			Content = content,
			// 空样式加 OverridesDefaultStyle，去掉 WPF-UI 按钮自带的边框和最小尺寸
			Style = new Style(typeof(System.Windows.Controls.Button)),
			OverridesDefaultStyle = true,
			Template = RowTemplate.Value,
			IsEnabled = enabled,
			FocusVisualStyle = null,
			HorizontalContentAlignment = HorizontalAlignment.Left,
		};
		System.Windows.Automation.AutomationProperties.SetName(row, e.Text);
		if (enabled)
		{
			Action click = e.Click;
			row.Click += delegate
			{
				owner.Close();
				click();
			};
		}
		return row;
	}

	public void Close()
	{
		Window w = _window;
		_window = null;
		try
		{
			w?.Close();
		}
		catch (InvalidOperationException)
		{
		}
	}
}
