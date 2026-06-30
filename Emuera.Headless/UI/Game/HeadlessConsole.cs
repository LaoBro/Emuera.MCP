using System;
using System.Drawing;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 无头模式下的 IConsoleUI 空实现。所有操作均为空或返回合理默认值。
	/// </summary>
	internal sealed class HeadlessConsole : IConsoleUI
	{
		private readonly HeadlessScrollBar _scrollBar = new();
		private readonly HeadlessTextBox _textBox = new();
		private readonly HeadlessToolTip _toolTip = new();
		private readonly HeadlessPictureBox _pictureBox = new();

		public bool Created => true;
		public int ClientWidth => Config.WindowX;
		public int ClientHeight => Config.WindowY;
		public string Text { get; set; } = string.Empty;
		public bool IsActive => true;

		public void Refresh() { }
		public void Invoke(Action action) => action?.Invoke();
		public void Focus() { }
		public void Close() => throw new GameExitException();
		public void Reboot() { }
		public void ShowConfigDialog() { }
		public void UpdateLastInput() { }
		public void ResetTextBoxPos() { }
		public void ClearRichText() { }
		public void ApplyTextBoxChanges() { }
		public void SetTextBoxPos(int xOffset, int yOffset, int width) { }
		public void ChangeTextBox(string str) { }

		public bool TextBoxPosChanged => false;
		public bool TextBoxIgnoreScrollBarChanges { get; set; } = false;

		public Point GetMousePosition() => Point.Empty;
		public Point GetCursorPosition() => Point.Empty;
		public int GetCursorHeight() => 0;
		public int GetScreenWorkingAreaHeight(Point point) => 1080;
		public void ExitApplication() => throw new GameExitException();
		public void ProcessEvents() { }

		public IScrollBar ScrollBar => _scrollBar;
		public ITextBox TextBox => _textBox;
		public IToolTip ToolTip => _toolTip;
		public IPictureBox MainPicBox => _pictureBox;
	}

	internal sealed class HeadlessScrollBar : IScrollBar
	{
		public int Value { get; set; } = 0;
		public int Maximum { get; set; } = 0;
		public bool Enabled { get; set; } = false;
	}

	internal sealed class HeadlessTextBox : ITextBox
	{
		public string Text { get; set; } = string.Empty;
		public Color BackColor { get; set; } = Config.BackColor;
	}

	internal sealed class HeadlessToolTip : IToolTip
	{
		public int InitialDelay { get; set; } = 0;
		public int AutoPopDelay { get; set; } = 0;
		public bool OwnerDraw { get; set; } = false;
		public Color ForeColor { get; set; } = Color.Black;
		public Color BackColor { get; set; } = Color.White;

		public event EventHandler<ToolTipDrawEventArgs> Draw;
		public event EventHandler<ToolTipPopupEventArgs> Popup;

		public void RemoveAll() { }
		public void Show(string text, Point point) { }
		public void Show(string text, Point point, int duration) { }
		public string GetToolTip() => string.Empty;
	}

	internal sealed class HeadlessPictureBox : IPictureBox
	{
		public int Width => Config.WindowX;
		public int Height => Config.WindowY;
		public Point PointToClient(Point point) => point;
		public Rectangle ClientRectangle => new(0, 0, Width, Height);
	}
}
