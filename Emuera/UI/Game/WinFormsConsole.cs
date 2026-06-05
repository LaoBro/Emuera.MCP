using System;
using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 对现有 MainWindow 的 IConsoleUI 适配器。将接口调用转发到 WinForms 控件。
	/// </summary>
	internal sealed class WinFormsConsole : IConsoleUI
	{
		private readonly MainWindow _window;
		private readonly WinFormsScrollBar _scrollBar;
		private readonly WinFormsTextBox _textBox;
		private readonly WinFormsToolTip _toolTip;
		private readonly WinFormsPictureBox _pictureBox;

		public WinFormsConsole(MainWindow window)
		{
			_window = window ?? throw new ArgumentNullException(nameof(window));
			_scrollBar = new WinFormsScrollBar(window.ScrollBar);
			_textBox = new WinFormsTextBox(window.TextBox);
			_toolTip = new WinFormsToolTip(window.ToolTip, window.MainPicBox);
			_pictureBox = new WinFormsPictureBox(window.MainPicBox);
		}

		public bool Created => _window.Created;
		public int ClientWidth => _window.MainPicBox.Width;
		public int ClientHeight => _window.MainPicBox.Height;
		public string Text
		{
			get => _window.Text;
			set => _window.Text = value;
		}
		public bool IsActive => Form.ActiveForm != null;

		public void Refresh() => _window.Refresh();
		public void Invoke(Action action)
		{
			if (_window.InvokeRequired)
				_window.Invoke(action);
			else
				action?.Invoke();
		}
		public void Focus() => _window.Focus();
		public void Close() => _window.Close();
		public void Reboot() => _window.Reboot();
		public void ShowConfigDialog() => _window.ShowConfigDialog();
		public void UpdateLastInput() => _window.update_lastinput();
		public void ResetTextBoxPos() => _window.ResetTextBoxPos();
		public void ClearRichText() => _window.clear_richText();
		public void ApplyTextBoxChanges() => _window.ApplyTextBoxChanges();
		public void SetTextBoxPos(int xOffset, int yOffset, int width) => _window.SetTextBoxPos(xOffset, yOffset, width);
		public void ChangeTextBox(string str) => _window.ChangeTextBox(str);

		public bool TextBoxPosChanged => _window.TextBoxPosChanged;
		public bool TextBoxIgnoreScrollBarChanges
		{
			get => _window.TextBoxIgnoreScrollBarChanges;
			set => _window.TextBoxIgnoreScrollBarChanges = value;
		}

		public Point GetMousePosition()
		{
			if (_window == null || !_window.Created)
				return Point.Empty;
			var pos = _window.MainPicBox.PointToClient(Cursor.Position);
			pos.Y -= ClientHeight;
			return pos;
		}

		public Point GetCursorPosition() => Cursor.Position;
		public int GetCursorHeight() => Cursor.Current?.Size.Height ?? 0;
		public int GetScreenWorkingAreaHeight(Point point) => Screen.FromPoint(point).WorkingArea.Height;

		public void ExitApplication() => Application.Exit();
		public void ProcessEvents() => Application.DoEvents();

		public IScrollBar ScrollBar => _scrollBar;
		public ITextBox TextBox => _textBox;
		public IToolTip ToolTip => _toolTip;
		public IPictureBox MainPicBox => _pictureBox;

		public MainWindow GetMainWindow() => _window;
	}

	internal sealed class WinFormsScrollBar : IScrollBar
	{
		private readonly VScrollBar _scrollBar;
		public WinFormsScrollBar(VScrollBar scrollBar)
		{
			_scrollBar = scrollBar ?? throw new ArgumentNullException(nameof(scrollBar));
		}
		public int Value
		{
			get => _scrollBar.Value;
			set => _scrollBar.Value = value;
		}
		public int Maximum
		{
			get => _scrollBar.Maximum;
			set => _scrollBar.Maximum = value;
		}
		public bool Enabled
		{
			get => _scrollBar.Enabled;
			set => _scrollBar.Enabled = value;
		}
	}

	internal sealed class WinFormsTextBox : ITextBox
	{
		private readonly RichTextBox _textBox;
		public WinFormsTextBox(RichTextBox textBox)
		{
			_textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
		}
		public string Text
		{
			get => _textBox.Text;
			set => _textBox.Text = value;
		}
		public Color BackColor
		{
			get => _textBox.BackColor;
			set => _textBox.BackColor = value;
		}
	}

	internal sealed class WinFormsToolTip : IToolTip
	{
		private readonly ToolTip _toolTip;
		private readonly Control _parent;

		public WinFormsToolTip(ToolTip toolTip, Control parent)
		{
			_toolTip = toolTip ?? throw new ArgumentNullException(nameof(toolTip));
			_parent = parent ?? throw new ArgumentNullException(nameof(parent));
			_toolTip.Draw += OnDraw;
			_toolTip.Popup += OnPopup;
		}

		public int InitialDelay
		{
			get => _toolTip.InitialDelay;
			set => _toolTip.InitialDelay = value;
		}
		public int AutoPopDelay
		{
			get => _toolTip.AutoPopDelay;
			set => _toolTip.AutoPopDelay = value;
		}
		public bool OwnerDraw
		{
			get => _toolTip.OwnerDraw;
			set => _toolTip.OwnerDraw = value;
		}
		public Color ForeColor
		{
			get => _toolTip.ForeColor;
			set => _toolTip.ForeColor = value;
		}
		public Color BackColor
		{
			get => _toolTip.BackColor;
			set => _toolTip.BackColor = value;
		}

		public event EventHandler<ToolTipDrawEventArgs> Draw;
		public event EventHandler<ToolTipPopupEventArgs> Popup;

		public void RemoveAll() => _toolTip.RemoveAll();
		public void Show(string text, Point point) => _toolTip.Show(text, _parent, point);
		public void Show(string text, Point point, int duration) => _toolTip.Show(text, _parent, point, duration);
		public string GetToolTip() => _toolTip.GetToolTip(_parent);

		private void OnDraw(object sender, DrawToolTipEventArgs e)
		{
			Draw?.Invoke(this, new ToolTipDrawEventArgs
			{
				Graphics = e.Graphics,
				ToolTipText = e.ToolTipText,
				Bounds = e.Bounds
			});
		}

		private void OnPopup(object sender, PopupEventArgs e)
		{
			var args = new ToolTipPopupEventArgs();
			Popup?.Invoke(this, args);
			e.ToolTipSize = args.ToolTipSize;
		}
	}

	internal sealed class WinFormsPictureBox : IPictureBox
	{
		private readonly PictureBox _picBox;
		public WinFormsPictureBox(PictureBox picBox)
		{
			_picBox = picBox ?? throw new ArgumentNullException(nameof(picBox));
		}
		public int Width => _picBox.Width;
		public int Height => _picBox.Height;
		public Point PointToClient(Point point) => _picBox.PointToClient(point);
		public Rectangle ClientRectangle => _picBox.ClientRectangle;
	}
}
