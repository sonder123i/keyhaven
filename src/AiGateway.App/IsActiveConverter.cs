using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiGateway.App;

// 「使用中」标记：行的 Id 与当前激活的 Id 一比。用转换器而不是给行加属性，
// 这样切上游之后标记自动跟着走，不必整张表刷新。
internal sealed class IsActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var rowId = values.Length > 0 ? values[0] as string : null;
        var activeId = values.Length > 1 ? values[1] as string : null;
        return rowId is not null && rowId == activeId ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}