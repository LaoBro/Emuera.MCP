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
		void ExitApplication();
		void ProcessEvents();

		IScrollBar ScrollBar { get; }
		ITextBox TextBox { get; }
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

	internal interface IPictureBox
	{
		int Width { get; }
		int Height { get; }
		EmuPoint PointToClient(EmuPoint point);
		EmuRectangle ClientRectangle { get; }
	}
}
