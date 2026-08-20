using System.Diagnostics;
using SafVerification.Services;

namespace SafVerification;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        var saf = SafService.Current;
        if (saf == null)
        {
            StatusLabel.Text = "正在初始化…";
            StatusLabel.TextColor = Colors.White;
            DebugLabel.Text = "";
        }
        else
        {
            StatusLabel.TextColor = Colors.White;
            StatusLabel.Text = saf.LoadUri() != null
                ? "SafService OK — 已有已存 URI"
                : "SafService OK — 请选择目录";
            DebugLabel.Text = "";
            RefreshState();
        }
    }

    private void RefreshState()
    {
        if (SafService.Current == null) return;

        var uri = SafService.Current.LoadUri();
        if (string.IsNullOrEmpty(uri))
        {
            UriLabel.Text = "(无)";
            FileList.ItemsSource = null;
            ContentLabel.Text = "(未选择文件)";
            return;
        }

        var valid = SafService.Current.IsUriValid(uri);
        if (!valid)
        {
            StatusLabel.Text = "已存 URI 已失效，请重新选择";
            UriLabel.Text = uri;
            FileList.ItemsSource = null;
            ContentLabel.Text = "(URI 失效)";
            return;
        }

        StatusLabel.Text = "已授权目录";
        UriLabel.Text = uri;

        var sw = Stopwatch.StartNew();
        var files = SafService.Current.ListRootFiles();
        sw.Stop();
        FileList.ItemsSource = files;
        TimingLabel.Text = $"列举 {files.Count} 个条目，耗时 {sw.ElapsedMilliseconds} ms";
    }

    private async void OnPickDirectory(object? sender, EventArgs e)
    {
        if (SafService.Current == null)
        {
            await DisplayAlertAsync("错误", $"SafService 未初始化\n{SafService.InitError ?? ""}", "OK");
            return;
        }

        PickButton.IsEnabled = false;
        PickButton.Text = "处理中…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var uri = await SafService.Current.PickDirectoryAsync(cts.Token);

            if (uri != null)
                await DisplayAlertAsync("成功", $"已选择目录:\n{uri}", "OK");
            else
                await DisplayAlertAsync("取消", "用户取消了选择", "OK");
        }
        catch (OperationCanceledException)
        {
            await DisplayAlertAsync("超时", "目录选择超时（5分钟），请重试", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("错误", $"选择失败:\n{ex.Message}", "OK");
        }
        finally
        {
            PickButton.IsEnabled = true;
            PickButton.Text = "选择目录";
            RefreshState();
        }
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        if (SafService.Current == null)
        {
            StatusLabel.Text = "SafService 未初始化";
            return;
        }
        RefreshState();
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SafService.Current == null || e.CurrentSelection.Count == 0) return;

        var entry = e.CurrentSelection[0] as string;
        if (entry == null) return;

        var fileName = entry.EndsWith("/")
            ? entry[..^1]
            : entry.Contains(" (")
                ? entry[..entry.LastIndexOf(" (")]
                : entry;

        if (entry.EndsWith("/"))
        {
            ContentLabel.Text = $"(目录: {fileName})";
            return;
        }

        var sw = Stopwatch.StartNew();
        var content = SafService.Current.ReadFileContent(fileName);
        sw.Stop();

        if (content == null)
        {
            ContentLabel.Text = $"(无法读取: {fileName})";
            return;
        }

        const int maxChars = 5000;
        var preview = content.Length > maxChars
            ? content[..maxChars] + $"\n\n…(截断，共 {content.Length} 字符)"
            : content;

        ContentLabel.Text = preview;
        TimingLabel.Text = $"读取 {fileName} ({content.Length} 字符)，耗时 {sw.ElapsedMilliseconds} ms";
    }
}
