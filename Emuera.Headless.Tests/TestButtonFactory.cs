using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;

namespace Emuera.Headless.Tests;

/// <summary>
/// 测试用按钮工厂（ADR-0010 C2/C3/C5）。
/// ConsoleButtonString 构造函数在 console==null 时跳过 Generation 赋值（Generation 保持 0），
/// 利用此行为创建不需要 EmueraConsole 的测试按钮——避免 IConsoleUI/Config 的重量级 stub。
/// </summary>
internal static class TestButtonFactory
{
    /// <summary>创建一个带文本的按钮，Generation=0。</summary>
    internal static ConsoleButtonString CreateButton(string text, long input = 1)
    {
        var nodes = new AConsoleDisplayNode[] { new TestTextNode(text) };
        return new ConsoleButtonString(null!, nodes, input);
    }

    /// <summary>创建一个字符串按钮（IsInteger=false），Generation=0。</summary>
    internal static ConsoleButtonString CreateStringButton(string text, string inputs)
    {
        var nodes = new AConsoleDisplayNode[] { new TestTextNode(text) };
        return new ConsoleButtonString(null!, nodes, inputs);
    }

    /// <summary>
    /// 创建一个 4 参构造的 int 按钮（IsInteger=true），Generation=0：
    /// Input 与 Inputs 可不同（HtmlManager HTML 按钮场景，如 Input=5 / Inputs="05"），
    /// 用于验证 StrButton 匹配对两种拼写都命中。
    /// </summary>
    internal static ConsoleButtonString CreateButtonWithInputs(string text, long input, string inputs)
    {
        var nodes = new AConsoleDisplayNode[] { new TestTextNode(text) };
        return new ConsoleButtonString(null!, nodes, input, inputs);
    }

    /// <summary>创建一个非按钮的 ConsoleButtonString（IsButton=false），用于测试 IsButton 过滤。</summary>
    internal static ConsoleButtonString CreateNonButton(string text)
    {
        var nodes = new AConsoleDisplayNode[] { new TestTextNode(text) };
        return new ConsoleButtonString(null!, nodes);
    }

    /// <summary>创建多个按钮数组。</summary>
    internal static ConsoleButtonString[] CreateButtons(params (string text, long input)[] specs)
    {
        var buttons = new ConsoleButtonString[specs.Length];
        for (int i = 0; i < specs.Length; i++)
            buttons[i] = CreateButton(specs[i].text, specs[i].input);
        return buttons;
    }

    /// <summary>
    /// 最小化 AConsoleDisplayNode 子类——仅存储 Text 供 ToString() 返回。
    /// 不依赖 Config/FontFactory，避免 ConsoleStyledString 的静态初始化需求。
    /// </summary>
    private sealed class TestTextNode : AConsoleDisplayNode
    {
        internal TestTextNode(string text)
        {
            Text = text;
            Width = text.Length;
            PointX = -1;
        }

        public override bool CanDivide => false;

        public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
        {
            // 测试专用——不需要实际绘制
        }

        public override void SetWidth(StringMeasure sm, float subPixel)
        {
            // Width 已在构造函数中设置
        }
    }
}
