using System;
using System.Drawing;

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

		Point GetMousePosition();
		Point GetCursorPosition();
		int GetCursorHeight();
		int GetScreenWorkingAreaHeight(Point point);
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
		Color BackColor { get; set; }
	}

	internal interface IToolTip
	{
		void RemoveAll();
		void Show(string text, Point point);
		void Show(string text, Point point, int duration);
		int InitialDelay { get; set; }
		int AutoPopDelay { get; set; }
		bool OwnerDraw { get; set; }
		Color ForeColor { get; set; }
		Color BackColor { get; set; }
		string GetToolTip();

		event EventHandler<ToolTipDrawEventArgs> Draw;
		event EventHandler<ToolTipPopupEventArgs> Popup;
	}

	internal interface IPictureBox
	{
		int Width { get; }
		int Height { get; }
		Point PointToClient(Point point);
		Rectangle ClientRectangle { get; }
	}

	// 简化的事件参数，避免直接引用 System.Windows.Forms
	internal class ToolTipDrawEventArgs : EventArgs
	{
		public Graphics Graphics { get; set; }
		public string ToolTipText { get; set; }
		public Rectangle Bounds { get; set; }
	}

	internal class ToolTipPopupEventArgs : EventArgs
	{
		public Size ToolTipSize { get; set; }
	}
}
