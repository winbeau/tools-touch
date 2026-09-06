using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ToolsTouch.Desktop.Controls;

// Presentation only: persisted values and command parameters keep their original identifiers.
public sealed class DisplayLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Draft" => "草稿", "Sent" => "已发送", "Sending" => "发送中", "Unknown" => "待核实",
        "Failed" => "失败", "Completed" => "已完成", "Running" => "进行中", "Queued" => "排队中",
        "Cancelled" => "已取消", "Partial" => "部分完成", "Discover" => "发现导师", "Analyze" => "研究分析",
        "Interested" => "有意申请", "Preparing" => "准备材料", "Submitted" => "已提交", "Interview" => "面试",
        "Offer" => "已录取", "Rejected" => "未录取", "Withdrawn" => "已撤回", "Missing" => "待补充",
        "Ready" => "已就绪", "NotApplicable" => "不适用", "Pending" => "待处理", "Dismissed" => "已忽略",
        "Eligible" => "符合资格", "Ineligible" => "不符合资格", "ProfessorAppointment" => "导师任职",
        "Department" => "学院", "Open" => "开放中", "Closed" => "已结束", "Upcoming" => "待开放",
        "SummerCamp" => "夏令营", "PreRecommendation" => "预推免", "Other" => "其他",
        "Text" => "文本", "Number" => "数值", "Date" => "日期", "Boolean" => "是 / 否",
        "SingleSelect" => "单选", "MultiSelect" => "多选", "Relation" => "关联", "Url" => "链接",
        "eq" => "等于", "contains" => "包含", "isEmpty" => "为空", "gte" => "不小于", "lte" => "不大于", "range" => "范围",
        "asc" => "升序", "desc" => "降序", _ => value ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
