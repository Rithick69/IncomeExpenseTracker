using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.UI.Shared.Converters
{
    /// <summary>
    /// Converts bitwise ReviewFlags enum into a visual highlight for DataGrid rows.
    /// Returns BackgroundSecondary brush if ANY error flag is set (bitwise check).
    /// </summary>
    public class ReviewStatusConverter : IValueConverter
    {
        public static readonly ReviewStatusConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // Bitwise check: If value is a ReviewFlags enum and has any flags set (not None)
            if (value is ReviewFlags flags && flags != ReviewFlags.None)
            {
                // Resolve RowError as an application resource; DynamicResource markup
                // syntax cannot be parsed as a Color here.
                if (Application.Current != null && Application.Current.TryGetResource("RowError", null, out var resource))
                {
                    if (resource is IBrush brush)
                    {
                        return brush;
                    }

                    if (resource is Color color)
                    {
                        return new SolidColorBrush(color);
                    }
                }

                // Fallback: RowError is not defined.
                return new SolidColorBrush(Color.Parse("#3A2528"));
            }

            // No errors: return null for transparent/default background
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}