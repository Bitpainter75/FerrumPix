Imports System
Imports System.Globalization
Imports Avalonia.Data
Imports Avalonia.Data.Converters
Imports Avalonia.Media
Imports FerrumPix.ViewModels

Namespace Converters

    ''' <summary>Wandelt eine Avalonia.Media.Color in einen SolidColorBrush - Avalonia konvertiert
    ''' Color nicht automatisch nach IBrush, daher schlug z.B. Background="{Binding}" auf einer
    ''' Farb-Sammlung (Zuletzt-verwendete-Farben) mit einer Bindungsfehlermeldung fehl.</summary>
    Public Class ColorToBrushConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Color Then Return New SolidColorBrush(CType(value, Color))
            If TypeOf value Is IBrush Then Return value
            Return BindingOperations.DoNothing
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class AppModeToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing OrElse parameter Is Nothing Then Return False
            If Not TypeOf value Is AppMode Then Return False
            Dim mode = CType(value, AppMode)
            Dim parsed As AppMode
            If [Enum].TryParse(parameter.ToString(), parsed) Then
                Return mode = parsed
            End If
            Return False
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class EditorToolEqualsConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing OrElse parameter Is Nothing Then Return False

            Dim currentTool As EditorTool
            Dim targetTool As EditorTool
            If Not [Enum].TryParse(value.ToString(), currentTool) Then Return False
            If Not [Enum].TryParse(parameter.ToString(), targetTool) Then Return False
            Return currentTool = targetTool
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class StringEqualsConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing OrElse parameter Is Nothing Then Return False
            Return String.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class ZoomLevelToTextConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Double Then
                Return $"{CInt(CDbl(value) * 100)}%"
            End If
            Return "100%"
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class SliderFillWidthConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim parts = If(parameter?.ToString(), "").Split("|"c)
            If parts.Length <> 3 Then Return 0.0

            Dim minimum, maximum, width As Double
            If Not Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, minimum) Then Return 0.0
            If Not Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, maximum) Then Return 0.0
            If Not Double.TryParse(parts(2), NumberStyles.Float, CultureInfo.InvariantCulture, width) Then Return 0.0
            If maximum <= minimum Then Return 0.0
            Dim current = If(TypeOf value Is Double, CDbl(value), minimum)
            Dim ratio = Math.Max(0, Math.Min(1, (current - minimum) / (maximum - minimum)))
            Return width * ratio
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class SliderThumbLeftConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim parts = If(parameter?.ToString(), "").Split("|"c)
            If parts.Length <> 4 Then Return 0.0

            Dim minimum, maximum, width, thumbSize As Double
            If Not Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, minimum) Then Return 0.0
            If Not Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, maximum) Then Return 0.0
            If Not Double.TryParse(parts(2), NumberStyles.Float, CultureInfo.InvariantCulture, width) Then Return 0.0
            If Not Double.TryParse(parts(3), NumberStyles.Float, CultureInfo.InvariantCulture, thumbSize) Then Return 0.0
            If maximum <= minimum Then Return 0.0
            Dim current = If(TypeOf value Is Double, CDbl(value), minimum)
            Dim ratio = Math.Max(0, Math.Min(1, (current - minimum) / (maximum - minimum)))
            Return width * ratio - thumbSize / 2.0
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class RatingThresholdConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim rating As Integer = 0
            Dim threshold As Integer = 1
            If value IsNot Nothing Then
                Integer.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, rating)
            End If
            If parameter IsNot Nothing Then
                Integer.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, threshold)
            End If
            Return rating >= threshold
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class RatingStarGlyphConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim rating As Integer = 0
            Dim threshold As Integer = 1
            If value IsNot Nothing Then
                Integer.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, rating)
            End If
            If parameter IsNot Nothing Then
                Integer.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, threshold)
            End If

            Return If(rating >= threshold, "★", "☆")
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class StringNotEmptyToBoolConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Return Not String.IsNullOrEmpty(TryCast(value, String))
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class

    Public Class BoolToWidthConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim parts = If(parameter?.ToString(), "").Split("|"c)
            If parts.Length <> 2 Then Return 440.0
            Dim trueValue As Double
            Dim falseValue As Double
            If Not Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, trueValue) Then trueValue = 760.0
            If Not Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, falseValue) Then falseValue = 440.0
            Return If(TypeOf value Is Boolean AndAlso CBool(value), trueValue, falseValue)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return BindingOperations.DoNothing
        End Function
    End Class


End Namespace
