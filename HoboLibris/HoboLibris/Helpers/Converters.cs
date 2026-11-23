using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HoboLibris.Helpers
{
    /// <summary>true -> Collapsed, false -> Visible.</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Collapsed;
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }

    /// <summary>null or empty -> Collapsed.</summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>0 -> Collapsed, anything else -> Visible.</summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Equal to the parameter -> true. Used for the radio-button navigation bound to an enum.</summary>
    public class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
    }

    /// <summary>A fraction 0..1 to a percentage for progress bars.</summary>
    public class FractionToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double d ? d * 100 : 0.0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>0 -> false, anything else -> true.</summary>
    public class CountToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n && n > 0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>All inputs true -> true.</summary>
    public class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>All inputs true -> Visible, else Collapsed.</summary>
    public class AllTrueToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// Book titles, authors and file names are data in their own language, so each cell reads in
    /// the direction of its own text: an English title stays whole ("12 Rules for Life") in an
    /// Arabic or Hebrew window, and a Hebrew title reads right to left in an English one. The
    /// direction comes from the first letter.
    /// </summary>
    public static class TextDirection
    {
        public static bool StartsRightToLeft(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return LanguageManager.IsRightToLeft;
            }
            foreach (var c in text)
            {
                if (char.IsLetter(c))
                {
                    return (c >= '֐' && c <= 'ࣿ') || (c >= 'יִ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿');
                }
            }
            return LanguageManager.IsRightToLeft;
        }
    }

    /// <summary>Text -> the FlowDirection of its first letter.</summary>
    public class TextFlowConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            TextDirection.StartsRightToLeft(value as string) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// Text -> the alignment that puts it at the start of the column as the window reads: its own
    /// start when it reads the same way as the window, its own end when it reads the other way.
    /// </summary>
    public class TextAlignConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            TextDirection.StartsRightToLeft(value as string) == LanguageManager.IsRightToLeft ? TextAlignment.Left : TextAlignment.Right;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Bold when true, normal otherwise.</summary>
    public class BoolToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? FontWeights.SemiBold : FontWeights.Normal;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
