using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端光标操作。ADR-0005：VT-only 后所有方法直接走 ANSI 路径，
    /// 原 <c>_ansi</c> 字段及非 ANSI 分支已删除。
    /// </summary>
    internal sealed class TerminalCursor
    {
        public void Save(out int left, out int top)
        {
            try { left = Console.CursorLeft; top = Console.CursorTop; }
            catch (Exception) { /* redirected console 不支持，用默认值 */ left = 0; top = 0; }
        }

        public void Set(int left, int top)
        {
            TryWrite($"\x1b[{top + 1};{left + 1}H");
        }

        public void ClearLine()
        {
            TryWrite("\x1b[2K\r");
        }

        /// <summary>
        /// 清屏。ADR-0005：VT-only 后仅保留 ANSI 路径（ESC[2J + ESC[H）。
        /// 原 Console.Clear() + === 分隔线 fallback 已删除。
        /// </summary>
        public void ClearScreen()
        {
            try { Console.Write("\x1b[2J\x1b[H"); }
            catch (Exception) { /* redirected console 写入失败，忽略 */ }
        }

        public void SaveAnsi()
        {
            TryWrite("\x1b7");
        }

        public void RestoreAnsi()
        {
            TryWrite("\x1b8");
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
