using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexTokenMonitor;

public partial class DetailWindow : Window
{
    public DetailWindow()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    public void SetData(SessionSummary data)
    {
        var rows = data.Rows.Concat(data.AgentRows).ToList();
        TaskTitle.Text = string.IsNullOrWhiteSpace(data.Title) ? "未命名任务" : string.Join(" ", data.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        TaskTitle.ToolTip = new ToolTip { Content = new TextBlock { Text = data.Title, MaxWidth = 210, TextWrapping = TextWrapping.Wrap } };
        TaskCounts.Text = $"{data.Rows.Select(r => r.TurnId).Distinct().Count()} 轮主任务 · {data.AgentSessionCount} 个子代理";
        InputValue.Text = TokenFormatter.Compact(rows.Sum(r => r.Uncached));
        CachedValue.Text = TokenFormatter.Compact(rows.Sum(r => r.Cached));
        OutputValue.Text = TokenFormatter.Compact(rows.Sum(r => r.Output));
        InputValue.ToolTip = $"未缓存输入：{rows.Sum(r => r.Uncached):N0}";
        CachedValue.ToolTip = $"缓存输入：{rows.Sum(r => r.Cached):N0}";
        OutputValue.ToolTip = $"输出：{rows.Sum(r => r.Output):N0}（包含推理）";
        var partial = data.HasUnknownPricing ? "≥ " : "";
        CreditsValue.Text = $"{partial}{data.GrandTotalCredits:N2}";
        ApiValue.Text = $"{partial}${data.GrandTotalUsd:N3}";
        CreditsValue.ToolTip = $"主任务：{(data.Rows.Any(r => !r.PricingKnown) ? "≥ " : "")}{data.TotalCredits:N2} cr\n子代理：{(data.AgentRows.Any(r => !r.PricingKnown) ? "≥ " : "")}{data.AgentTotalCredits:N2} cr";
        RequestedModelValue.Text = data.RequestedModel.Length == 0 ? "未记录" : data.RequestedModel;
        ReturnedModelValue.Text = data.ModelEvidence?.ReturnedModel ?? "未记录";
        RequestedModelValue.ToolTip = ReturnedModelValue.ToolTip = new ToolTip { Content = new TextBlock { Text = data.ModelCheckDetail, MaxWidth = 210, TextWrapping = TextWrapping.Wrap } };
        var hasContext = data.LastRequestInput.HasValue && data.ContextWindow is > 0;
        var percent = hasContext ? data.LastRequestInput!.Value * 100d / data.ContextWindow!.Value : 0;
        ContextValue.Text = hasContext ? $"{percent:0.#}% · 压缩 {data.CompactionCount} 次" : $"-- · 压缩 {data.CompactionCount} 次";
        ContextBar.Value = Math.Clamp(percent, 0, 100);
        ContextPanel.ToolTip = hasContext ? $"最近请求输入：{data.LastRequestInput:N0} / {data.ContextWindow:N0}\n不是实时上下文占用" : "暂无上下文记录";
        UpdatedText.Text = $"主模型校验：{data.ModelCheck}";
        UpdatedText.Foreground = (System.Windows.Media.Brush)FindResource(data.ModelCheck == "名称不同" ? "PurpleBrush" : data.ModelCheck == "名称一致" ? "TealBrush" : "MutedBrush");
        EstimateNote.Text = data.HasUnknownPricing ? "部分模型费率未知 · ≥ 为已知部分" : "全部模型与子代理合计 · 费用为估算";
        UpdatedText.ToolTip = new ToolTip { Content = new TextBlock { Text = data.ModelCheckDetail + $"\n更新 {data.LastWriteTime:HH:mm} · 共 {rows.Count} 条记录"
            + (data.MalformedLines > 0 ? $"\n跳过 {data.MalformedLines} 条损坏记录" : ""), MaxWidth = 210, TextWrapping = TextWrapping.Wrap } };
    }
}
