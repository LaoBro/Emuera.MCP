using System;
using MinorShift.Emuera.Primitives;
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
		private readonly HeadlessPictureBox _pictureBox = new();

		public bool Created => true;
		public int ClientWidth => Config.WindowX;
		public int ClientHeight => Config.WindowY;
		public string Text { get; set; } = string.Empty;
		public bool IsActive => true;

		public void Refresh() { }
		public void Invoke(Action action) => action?.Invoke();
		public void Focus() { }

		/// <summary>
		/// 脚本 <c>@QUIT/@EXIT</c> 系统命令的退出点。
		/// 无头模式没有可关闭的窗口，抛出 <see cref="GameExitException"/> 中止游戏循环，
		/// 由宿主（CLI <see cref="MinorShift.Emuera.HeadlessRunner"/> / Server <see cref="MinorShift.Emuera.Server.Session"/>）
		/// 在循环边界捕获后走正常清理。注意：调用方会收到该异常，需在宿主层 catch。
		/// </summary>
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

		public EmuPoint GetMousePosition() => EmuPoint.Empty;
		public EmuPoint GetCursorPosition() => EmuPoint.Empty;

		/// <summary>
		/// 脚本 <c>FORCE_QUIT</c> 系统命令（非重启路径退出的终止点）。
		/// 无头模式没有可关闭的窗口，抛出 <see cref="GameExitException"/> 中止游戏循环，
		/// 由宿主（CLI <see cref="MinorShift.Emuera.HeadlessRunner"/> / Server <see cref="MinorShift.Emuera.Server.Session"/>）
		/// 在循环边界捕获后走正常清理。注意：调用方会收到该异常，需在宿主层 catch。
		/// </summary>
		public void ExitApplication() => throw new GameExitException();
		public void ProcessEvents() { }

		public IScrollBar ScrollBar => _scrollBar;
		public ITextBox TextBox => _textBox;
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
		public EmuColor BackColor { get; set; } = Config.BackColor;
	}

	internal sealed class HeadlessPictureBox : IPictureBox
	{
		public int Width => Config.WindowX;
		public int Height => Config.WindowY;
		public EmuPoint PointToClient(EmuPoint point) => point;
		public EmuRectangle ClientRectangle => new(0, 0, Width, Height);
	}
}
