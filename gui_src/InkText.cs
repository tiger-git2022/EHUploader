using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EHUploader;

/// <summary>
/// 按字形的实际笔画范围排版的文字：尺寸就是笔画的外框，放进容器里居中时是视觉上的正中，
/// 不受字体行高、基线位置和屏幕缩放取整的影响（徽标里的数字用它，任何数字都不会偏）。
/// </summary>
public sealed class InkText : FrameworkElement
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InkText),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(InkText),
		new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(InkText),
		new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(InkText),
		new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(InkText),
		new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
	public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
	public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
	public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
	public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

	private Geometry _geometry = Geometry.Empty;

	public InkText()
	{
		UseLayoutRounding = false;   // 外框是小数尺寸，取整会把居中又弄偏
		SnapsToDevicePixels = false;
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		FormattedText ft = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
			new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground,
			VisualTreeHelper.GetDpi(this).PixelsPerDip);
		_geometry = ft.BuildGeometry(new Point(0, 0));
		Rect b = _geometry.Bounds;
		_shiftX = b.IsEmpty ? 0 : OpticalShift(_geometry, b);
		return b.IsEmpty ? new Size(0, 0) : new Size(b.Width, b.Height);
	}

	private double _shiftX;

	/// <summary>
	/// 水平方向按笔画的重心居中（视觉居中）：「1」左上有个小勾、竖笔偏右，按外框居中看起来会偏右；
	/// 按重心居中，竖笔就落在中间。其他数字的重心和外框中心几乎重合，基本不动。
	/// </summary>
	private static double OpticalShift(Geometry g, Rect b)
	{
		const double k = 8;   // 放大 8 倍栅格化后统计每一列的笔画量
		int w = Math.Max(1, (int)Math.Ceiling(b.Width * k)), h = Math.Max(1, (int)Math.Ceiling(b.Height * k));
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.PushTransform(new ScaleTransform(k, k));
			dc.PushTransform(new TranslateTransform(-b.X, -b.Y));
			dc.DrawGeometry(Brushes.Black, null, g);
		}
		System.Windows.Media.Imaging.RenderTargetBitmap rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
		rtb.Render(dv);
		byte[] px = new byte[w * h * 4];
		rtb.CopyPixels(px, w * 4, 0);
		double sum = 0, sx = 0;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				byte a = px[(y * w + x) * 4 + 3];
				sum += a;
				sx += a * (x + 0.5);
			}
		}
		if (sum <= 0)
		{
			return 0;
		}
		double shift = b.Width / 2 - sx / sum / k;
		return Math.Max(-b.Width / 4, Math.Min(b.Width / 4, shift));
	}

	protected override void OnRender(DrawingContext dc)
	{
		Rect b = _geometry.Bounds;
		if (b.IsEmpty)
		{
			return;
		}
		dc.PushTransform(new TranslateTransform(-b.X + _shiftX, -b.Y));
		dc.DrawGeometry(Foreground, null, _geometry);
		dc.Pop();
	}
}
