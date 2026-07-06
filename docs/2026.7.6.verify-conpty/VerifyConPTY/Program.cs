using System.Runtime.InteropServices;
using System.Text;

int passed = 0, failed = 0;

Report("=== Step 0: Environment ===");
Check(TestConsoleHandles(), ref passed, ref failed);

Report("=== Step 1: stdin handle & GetConsoleMode ===");
Check(TestStdinConsoleMode(), ref passed, ref failed);

Report("=== Step 2: SetConsoleMode(stdin) with VT_INPUT ===");
Check(TestSetStdinMode(), ref passed, ref failed);

Report("=== Step 3: WaitForSingleObject(stdin, 0) ===");
Check(TestWaitForSingleObject(), ref passed, ref failed);

Report("=== Step 4: Console.KeyAvailable + ReadKey(true) ===");
Check(TestConsoleKeyAvailable(), ref passed, ref failed);

Report("=== Step 5: stdout console mode & VT ===");
Check(TestStdoutVtMode(), ref passed, ref failed);

Report("=== Step 6: DA1 probe ===");
Check(TestDa1Probe(), ref passed, ref failed);

Report($"\n=== Summary: {passed} passed, {failed} failed ===");
return failed > 0 ? 1 : 0;

// ================================================================

static bool TestConsoleHandles()
{
    Console.WriteLine($"OS: {(OperatingSystem.IsWindows() ? "Windows" : "Non-Windows")}");
    Console.WriteLine($".NET: {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"Console.IsInputRedirected: {Console.IsInputRedirected}");
    Console.WriteLine($"Console.IsOutputRedirected: {Console.IsOutputRedirected}");

    IntPtr hStdin = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);
    IntPtr hStdout = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
    Console.WriteLine($"hStdin : 0x{hStdin:X8} (INVALID={hStdin == NativeMethods.INVALID_HANDLE_VALUE})");
    Console.WriteLine($"hStdout: 0x{hStdout:X8} (INVALID={hStdout == NativeMethods.INVALID_HANDLE_VALUE})");

    return hStdin != IntPtr.Zero && hStdin != NativeMethods.INVALID_HANDLE_VALUE
        && hStdout != IntPtr.Zero && hStdout != NativeMethods.INVALID_HANDLE_VALUE;
}

static bool TestStdinConsoleMode()
{
    IntPtr hStdin = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);
    if (!NativeMethods.GetConsoleMode(hStdin, out uint mode))
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"FAIL: GetConsoleMode(stdin) error={err}");
        return false;
    }

    Console.WriteLine($"GetConsoleMode(stdin) = 0x{mode:X8}");

    var flags = new (uint bit, string name)[]
    {
        (0x0001, "ENABLE_PROCESSED_INPUT"),
        (0x0002, "ENABLE_LINE_INPUT"),
        (0x0004, "ENABLE_ECHO_INPUT"),
        (0x0008, "ENABLE_WINDOW_INPUT"),
        (0x0010, "ENABLE_MOUSE_INPUT"),
        (0x0020, "ENABLE_INSERT_MODE"),
        (0x0040, "ENABLE_QUICK_EDIT_MODE"),
        (0x0080, "ENABLE_EXTENDED_FLAGS"),
        (0x0200, "ENABLE_VIRTUAL_TERMINAL_INPUT"),
    };
    foreach (var (bit, name) in flags)
        Console.WriteLine($"  {((mode & bit) != 0 ? "SET" : "   ")} {name}");

    return true;
}

static bool TestSetStdinMode()
{
    IntPtr hStdin = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);
    if (!NativeMethods.GetConsoleMode(hStdin, out uint originalMode))
    {
        Console.WriteLine("FAIL: GetConsoleMode before SetConsoleMode");
        return false;
    }

    // 1) Enable VIRTUAL_TERMINAL_INPUT
    uint vtMode = originalMode | NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT;
    if (!NativeMethods.SetConsoleMode(hStdin, vtMode))
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"FAIL: SetConsoleMode(VT_INPUT) error={err}");
        return false;
    }
    Console.WriteLine("SetConsoleMode(VT_INPUT) = OK");

    if (NativeMethods.GetConsoleMode(hStdin, out uint v))
        Console.WriteLine($"  Verify: mode=0x{v:X8}, VT_INPUT set={(v & 0x0200) != 0}");

    // 2) Clear cooked-mode flags to get raw bytestream
    uint rawMode = vtMode
                   & ~NativeMethods.ENABLE_PROCESSED_INPUT
                   & ~NativeMethods.ENABLE_LINE_INPUT
                   & ~NativeMethods.ENABLE_ECHO_INPUT;
    if (!NativeMethods.SetConsoleMode(hStdin, rawMode))
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"FAIL: SetConsoleMode(raw) error={err}");
        NativeMethods.SetConsoleMode(hStdin, originalMode);
        return false;
    }
    Console.WriteLine("SetConsoleMode(raw: ~PROCESSED ~LINE ~ECHO) = OK");

    if (NativeMethods.GetConsoleMode(hStdin, out uint rv))
        Console.WriteLine($"  Verify: mode=0x{rv:X8}");

    Console.WriteLine($"           PROCESSED_INPUT={(rv & 0x0001) != 0}");
    Console.WriteLine($"           LINE_INPUT     ={(rv & 0x0002) != 0}");
    Console.WriteLine($"           ECHO_INPUT     ={(rv & 0x0004) != 0}");
    Console.WriteLine($"           VT_INPUT       ={(rv & 0x0200) != 0}");

    NativeMethods.SetConsoleMode(hStdin, originalMode);
    return true;
}

static bool TestWaitForSingleObject()
{
    IntPtr hStdin = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);

    uint result = NativeMethods.WaitForSingleObject(hStdin, 0);
    Console.WriteLine($"WaitForSingleObject(stdin, 0) = {result} (WAIT_OBJECT_0=0, WAIT_TIMEOUT=0x102)");
    bool signaled = result == 0;
    Console.WriteLine($"  Signaled (input pending): {signaled}");

    bool gneOk = NativeMethods.GetNumberOfConsoleInputEvents(hStdin, out uint eventCount);
    if (gneOk)
        Console.WriteLine($"GetNumberOfConsoleInputEvents = {eventCount}");
    else
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"GetNumberOfConsoleInputEvents FAILED: error={err} (expected on pipe handle)");
    }

    return true;
}

static bool TestConsoleKeyAvailable()
{
    Console.WriteLine($"Console.KeyAvailable: {Console.KeyAvailable}");

    Console.WriteLine();
    Console.WriteLine("=== WAITING_FOR_INPUT ===");
    Console.Out.Flush();

    DateTime deadline = DateTime.UtcNow.AddSeconds(8);
    DateTime lastKeyTime = DateTime.MinValue;
    bool gotKey = false;
    int keyCount = 0;

    while (DateTime.UtcNow < deadline)
    {
        if (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            gotKey = true;
            keyCount++;
            lastKeyTime = DateTime.UtcNow;
            char ch = key.KeyChar;
            string chDisplay = char.IsControl(ch) ? $"\\x{(int)ch:X2}" : ch.ToString();
            Console.WriteLine($"ReadKey: Key={key.Key}, KeyChar={chDisplay}, Mod={key.Modifiers}");
            Console.Out.Flush();
        }
        else
        {
            // If we got some keys but no more for 1s, end early
            if (gotKey && (DateTime.UtcNow - lastKeyTime).TotalSeconds >= 1.0)
                break;
            Thread.Sleep(50);
        }
    }

    Console.WriteLine($"Result: gotKey={gotKey}, keyCount={keyCount}");
    Console.Out.Flush();
    return gotKey;
}

static bool TestStdoutVtMode()
{
    IntPtr hStdout = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
    if (!NativeMethods.GetConsoleMode(hStdout, out uint mode))
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"FAIL: GetConsoleMode(stdout) error={err}");
        return false;
    }

    Console.WriteLine($"GetConsoleMode(stdout) = 0x{mode:X8}");

    const uint VT_PROCESSING = 0x0004;
    if ((mode & VT_PROCESSING) != 0)
    {
        Console.WriteLine("  ENABLE_VIRTUAL_TERMINAL_PROCESSING: ALREADY SET");
    }
    else
    {
        uint newMode = mode | VT_PROCESSING;
        if (NativeMethods.SetConsoleMode(hStdout, newMode))
        {
            Console.WriteLine("  SetConsoleMode(VT_PROCESSING) = OK");
            NativeMethods.GetConsoleMode(hStdout, out uint vm);
            Console.WriteLine($"  Verify: mode=0x{vm:X8}");
            NativeMethods.SetConsoleMode(hStdout, mode);
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  FAIL: SetConsoleMode(VT_PROCESSING) error={err}");
        }
    }

    Console.Write("VT test: \x1b[38;2;255;0;0mRED\x1b[0m \x1b[1mBOLD\x1b[0m\n");
    return true;
}

static bool TestDa1Probe()
{
    IntPtr hStdin = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);
    if (!NativeMethods.GetConsoleMode(hStdin, out uint originalMode))
    {
        Console.WriteLine("FAIL: GetConsoleMode(stdin) before DA1 probe");
        return false;
    }

    // Switch to raw VT input mode for reading DA1 response bytes
    uint probeMode = originalMode
                     | NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT
                     & ~NativeMethods.ENABLE_PROCESSED_INPUT
                     & ~NativeMethods.ENABLE_LINE_INPUT
                     & ~NativeMethods.ENABLE_ECHO_INPUT;
    if (!NativeMethods.SetConsoleMode(hStdin, probeMode))
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"FAIL: SetConsoleMode(raw) for DA1 probe, error={err}");
        NativeMethods.SetConsoleMode(hStdin, originalMode);
        return false;
    }

    // Flush any pre-existing input (terminal startup sequences)
    byte[] flushBuf = new byte[256];
    int flushed = 0;
    while (NativeMethods.WaitForSingleObject(hStdin, 0) == 0)
    {
        int n = ReadRawBytes(hStdin, flushBuf, 0, flushBuf.Length);
        if (n <= 0) break;
        flushed += n;
        if (flushed > 8192) break;
    }
    if (flushed > 0)
        Console.WriteLine($"  Flushed {flushed} bytes of pending input before DA1");

    // Send DA1 request
    Console.Write("\x1b[c");
    Console.Out.Flush();
    Console.WriteLine("  Sent DA1 (\\x1b[c)");

    // Poll for response
    byte[] buffer = new byte[256];
    int totalRead = 0;
    DateTime deadline = DateTime.UtcNow.AddMilliseconds(500);
    bool responded = false;

    while (DateTime.UtcNow < deadline)
    {
        uint wr = NativeMethods.WaitForSingleObject(hStdin, 50);
        if (wr == 0)
        {
            int n = ReadRawBytes(hStdin, buffer, totalRead, buffer.Length - totalRead);
            if (n > 0)
            {
                totalRead += n;
                if (ContainsDa1Response(buffer, totalRead))
                {
                    responded = true;
                    break;
                }
            }
        }
        else if (wr == 0xFFFFFFFF)
        {
            Console.WriteLine("  WaitForSingleObject failed during DA1 poll");
            break;
        }
    }

    Console.WriteLine($"  DA1 result: responded={responded}, totalRead={totalRead} bytes");
    if (totalRead > 0)
    {
        Console.Write("  Raw response hex: ");
        for (int i = 0; i < totalRead; i++)
            Console.Write($"{buffer[i]:X2} ");
        Console.WriteLine();

        string responseText = Encoding.UTF8.GetString(buffer, 0, totalRead);
        Console.WriteLine($"  Response text: {EscapeControl(responseText)}");
    }

    NativeMethods.SetConsoleMode(hStdin, originalMode);
    return responded;
}

// ================================================================

static int ReadRawBytes(IntPtr hFile, byte[] buffer, int offset, int count)
{
    IntPtr bufPtr = Marshal.AllocHGlobal(count);
    try
    {
        if (!NativeMethods.ReadFile(hFile, bufPtr, count, out int read, IntPtr.Zero))
            return 0;
        if (read > 0)
            Marshal.Copy(bufPtr, buffer, offset, read);
        return read;
    }
    finally
    {
        Marshal.FreeHGlobal(bufPtr);
    }
}

static bool ContainsDa1Response(byte[] buffer, int length)
{
    for (int i = 0; i < length - 1; i++)
    {
        if (buffer[i] == 0x1B && buffer[i + 1] == (byte)'[')
        {
            for (int j = i + 2; j < length; j++)
            {
                if (buffer[j] == (byte)'c')
                    return true;
                if (buffer[j] != ';' && buffer[j] != '?' && (buffer[j] < '0' || buffer[j] > '9'))
                    break;
            }
        }
    }
    return false;
}

static string EscapeControl(string text)
{
    var sb = new StringBuilder();
    foreach (char c in text)
    {
        if (c < 0x20 || c >= 0x7F)
            sb.Append($"\\x{(int)c:X2}");
        else
            sb.Append(c);
    }
    return sb.ToString();
}

static void Report(string label) => Console.WriteLine($"\n{label}");

static void Check(bool ok, ref int passed, ref int failed)
{
    Console.WriteLine(ok ? "  ==> PASS" : "  ==> FAIL");
    if (ok) passed++; else failed++;
}

// ================================================================

static class NativeMethods
{
    internal const int STD_INPUT_HANDLE = -10;
    internal const int STD_OUTPUT_HANDLE = -11;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    internal const uint ENABLE_PROCESSED_INPUT = 0x0001;
    internal const uint ENABLE_LINE_INPUT = 0x0002;
    internal const uint ENABLE_ECHO_INPUT = 0x0004;
    internal const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);
}
