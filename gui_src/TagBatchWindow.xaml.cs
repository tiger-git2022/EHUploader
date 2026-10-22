using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;

namespace EHUploader;

/// <summary>批量加标签：填标签；需要时再从列表里勾选要加的对象（已上传的画廊）。</summary>
public partial class TagBatchWindow : FluentWindow
{
	public sealed class Entry : Bindable
	{
		public object Tag { get; set; }
		public string Label { get; set; }
		public string Sub { get; set; }
		/// <summary>缩略图文件路径（没有就留空）。</summary>
		public string Thumb { get; set; }
		private bool _checked = true;
		public bool Checked { get => _checked; set => Set(ref _checked, value); }
	}

	private readonly List<Entry> _entries;

	public List<string> Tags => TagsBox.Text.Split(new[] { '\r', '\n', ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
		.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

	public List<object> Chosen => _entries?.Where(e => e.Checked).Select(e => e.Tag).ToList() ?? new List<object>();

	public TagBatchWindow(string title, string intro, string okText, List<Entry> entries = null, string initialTags = "female:mind control")
	{
		InitializeComponent();
		Title = title;
		Bar.Title = title;
		Intro.Text = intro;
		OkButton.Content = okText;
		// 预先填好（默认是上次用过的标签）并全选：直接点按钮就用它，打字就会整个替换掉
		TagsBox.Text = initialTags ?? "";
		Loaded += delegate { TagsBox.Focus(); TagsBox.SelectAll(); };
		_entries = entries;
		if (entries != null)
		{
			Items.ItemsSource = entries;
			ListPanel.Visibility = Visibility.Visible;
			ListLabel.Text = $"加到哪些画廊（共 {entries.Count} 个，已有这些标签的会自动跳过）";
		}
		else
		{
			SizeToContent = SizeToContent.Height;
		}
	}

	private void All_Click(object sender, RoutedEventArgs e) => _entries?.ForEach(x => x.Checked = true);

	private void None_Click(object sender, RoutedEventArgs e) => _entries?.ForEach(x => x.Checked = false);

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		if (Tags.Count == 0)
		{
			System.Windows.MessageBox.Show(this, "请至少填一个标签。", Title);
			return;
		}
		if (_entries != null && Chosen.Count == 0)
		{
			System.Windows.MessageBox.Show(this, "请至少勾选一个画廊。", Title);
			return;
		}
		DialogResult = true;
	}

	private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
