using System;

namespace MinorShift.Emuera.GameView
{
    internal sealed class TerminalCursor
    {
        private readonly bool _ansi;

        public TerminalCursor(bool ansiEnabled)
        {
            _ansi = ansiEnabled;
        }

        public void Save(out int left, out int top)
        {
            try { left = Console.CursorLeft; top = Console.CursorTop; }
            catch (Exception) { /* redirected console 不支持，用默认值 */ left = 0; top = 0; }
        }

        public void Set(int left, int top)
        {
            if (_ansi)
                TryWrite($"\x1b[{top + 1};{left + 1}H");
            else
                TrySetCursorPosition(left, top);
        }

        public void ClearLine()
        {
            if (_ansi)
            {
                TryWrite("\x1b[2K\r");
                return;
            }
            int top = Console.CursorTop;
            int width = TryGetWindowWidth();
            TrySetCursorPosition(0, top);
            TryWrite(new string(' ', width - 1));
            TrySetCursorPosition(0, top);
        }

        public void ClearScreen()
        {
            bool cleared = false;
            try { Console.Clear(); cleared = true; }
            catch (Exception) { /* Console.Clear 在 redirected 下失败，忽略 */ }

            if (!cleared && _ansi)
            {
                try { Console.Write("\x1b[2J\x1b[H"); cleared = true; }
                catch (Exception) { /* ANSI clear 失败，忽略 */ }
            }

            if (!cleared)
                Console.WriteLine(new string('=', Math.Max(TryGetWindowWidth() - 1, 20)));
        }

        public void SaveAnsi()
        {
            if (_ansi) TryWrite("\x1b7");
        }

        public void RestoreAnsi()
        {
            if (_ansi) TryWrite("\x1b8");
        }

        public static void TryWrite(string text)
        {
            try { Console.Write(text); } catch (Exception) { /* redirected console 写入失败，忽略 */ }
        }

        public static void TrySetCursorPosition(int left, int top)
        {
            try { Console.SetCursorPosition(left, top); } catch (Exception) { /* SetCursorPosition 越界或 redirected 失败，忽略 */ }
        }

        public static int TryGetWindowWidth()
        {
            try { return Console.WindowWidth; }
            catch (Exception) { /* redirected console，默认 80 */ return 80; }
        }

        public static int TryGetWindowHeight()
        {
            try { return Console.WindowHeight; }
            catch (Exception) { /* redirected console，默认 25 */ return 25; }
        }
    }
}
