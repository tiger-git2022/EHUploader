using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;

namespace EHUploader;

/// <summary>改「复制提示词」的模板：「恢复默认」把默认模板放回编辑框，点「保存」才生效。</summary>
public partial class PromptWindow : FluentWindow
{
	public string Template => TemplateBox.Text;

	public PromptWindow(string template)
	{
		InitializeComponent();
		TemplateBox.Text = template;
		Legend.Text = "复制时会换成这一本的内容：\n" + string.Join("\n", TabletPrompt.Placeholders.Select(p => p.Key + "　" + p.Desc));
	}

	private void Reset_Click(object sender, RoutedEventArgs e) => TemplateBox.Text = TabletPrompt.DefaultTemplate;

	private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

	private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
