using System;
using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 跨平台 UI 抽象接口。封装所有 WinForms 依赖，使 EmueraConsole 能在无头模式下运行。
	/// </summary>
	internal interface IConsoleUI
	{
		bool Created { get; }
		int ClientWidth { get; }
		int ClientHeight { get; }
		string Text { get; set; }
		bool IsActive { get; }

		void Refresh();
		void Invoke(Action action);
		void Focus();
		void Close();
		void Reboot();
		void ShowConfigDialog();
		void UpdateLastInput();
		void ResetTextBoxPos();
		void ClearRichText();
		void ApplyTextBoxChanges();
		void SetTextBoxPos(int xOffset, int yOffset, int width);
		void ChangeTextBox(string str);

		bool TextBoxPosChanged { get; }
		bool TextBoxIgnoreScrollBarChanges { get; set; }

		EmuPoint GetMousePosition();
		EmuPoint GetCursorPosition();
		int GetCursorHeight();
		int GetScreenWorkingAreaHeight(EmuPoint point);
		void ExitApplication();
		void ProcessEvents();

		IScrollBar ScrollBar { get; }
		ITextBox TextBox { get; }
		IToolTip ToolTip { get; }
		IPictureBox MainPicBox { get; }
	}

	internal interface IScrollBar
	{
		int Value { get; set; }
		int Maximum { get; set; }
		bool Enabled { get; set; }
	}

	internal interface ITextBox
	{
		string Text { get; set; }
		EmuColor BackColor { get; set; }
	}

	internal interface IToolTip
	{
		void RemoveAll();
		void Show(string text, EmuPoint point);
		void Show(string text, EmuPoint point, int duration);
		int InitialDelay { get; set; }
		int AutoPopDelay { get; set; }
		bool OwnerDraw { get; set; }
		EmuColor ForeColor { get; set; }
		EmuColor BackColor { get; set; }
		string GetToolTip();

		event EventHandler<ToolTipDrawEventArgs> Draw;
		event EventHandler<ToolTipPopupEventArgs> Popup;
	}

	internal interface IPictureBox
	{
		int Width { get; }
		int Height { get; }
		EmuPoint PointToClient(EmuPoint point);
		EmuRectangle ClientRectangle { get; }
	}

	// 简化的事件参数，避免直接引用 System.Windows.Forms
	// I-14：移除 Graphics 和 ToolTipSize，Headless 模式下 Draw/Popup 事件从未触发
	internal class ToolTipDrawEventArgs : EventArgs
	{
		public string ToolTipText { get; set; } = null!;
		public EmuRectangle Bounds { get; set; }
	}

	internal class ToolTipPopupEventArgs : EventArgs
	{
	}
}
