using System;
using System.Windows.Forms;

static class Dialog
{
	public enum Result
	{
		Yes,
		No
	}

	/// <summary>
	/// 检测当前是否处于无头模式（无 GUI 消息循环）。
	/// 通过 Program.IsHeadlessMode 判断，该值在 Main 中根据 --headless / --server 参数设置。
	/// </summary>
	private static bool IsHeadless => MinorShift.Emuera.Program.IsHeadlessMode;

	public static void Show(string text)
	{
		if (IsHeadless)
			Console.Error.WriteLine($"[dialog] {text}");
		else
			MessageBox.Show(text);
	}
	public static void Show(string title, string text)
	{
		if (IsHeadless)
			Console.Error.WriteLine($"[dialog:{title}] {text}");
		else
			MessageBox.Show(text, title);
	}
	public static bool ShowPrompt(string title, string text)
	{
		if (IsHeadless)
		{
			Console.Error.WriteLine($"[dialog:{title}] {text} (auto-select: No)");
			return false;
		}
		var result = MessageBox.Show(text, title, MessageBoxButtons.YesNo);
		return result switch
		{
			DialogResult.Yes => true,
			_ => false
		};
	}
}
