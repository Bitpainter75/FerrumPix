Imports System
Imports System.Globalization
Imports Avalonia.Data.Converters
Imports FerrumPix.ViewModels

Namespace Converters

    Public Class AppModeToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing OrElse parameter Is Nothing Then Return False
            Dim mode = CType(value, AppMode)
            Dim parsed As AppMode
            If [Enum].TryParse(parameter.ToString(), parsed) Then
                Return mode = parsed
            End If
            Return False
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotImplementedException()
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
            Throw New NotImplementedException()
        End Function
    End Class

    Public Class StringEqualsConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing OrElse parameter Is Nothing Then Return False
            Return String.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotImplementedException()
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
            Throw New NotImplementedException()
        End Function
    End Class

    Public Class SliderFillWidthConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim parts = If(parameter?.ToString(), "").Split("|"c)
            If parts.Length <> 3 Then Return 0.0

            Dim minimum = Double.Parse(parts(0), CultureInfo.InvariantCulture)
            Dim maximum = Double.Parse(parts(1), CultureInfo.InvariantCulture)
            Dim width = Double.Parse(parts(2), CultureInfo.InvariantCulture)
            Dim current = If(TypeOf value Is Double, CDbl(value), minimum)
            Dim ratio = Math.Max(0, Math.Min(1, (current - minimum) / (maximum - minimum)))
            Return width * ratio
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotImplementedException()
        End Function
    End Class

    Public Class SliderThumbLeftConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim parts = If(parameter?.ToString(), "").Split("|"c)
            If parts.Length <> 4 Then Return 0.0

            Dim minimum = Double.Parse(parts(0), CultureInfo.InvariantCulture)
            Dim maximum = Double.Parse(parts(1), CultureInfo.InvariantCulture)
            Dim width = Double.Parse(parts(2), CultureInfo.InvariantCulture)
            Dim thumbSize = Double.Parse(parts(3), CultureInfo.InvariantCulture)
            Dim current = If(TypeOf value Is Double, CDbl(value), minimum)
            Dim ratio = Math.Max(0, Math.Min(1, (current - minimum) / (maximum - minimum)))
            Return width * ratio - thumbSize / 2.0
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotImplementedException()
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
            Throw New NotImplementedException()
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
            Throw New NotImplementedException()
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
            Throw New NotImplementedException()
        End Function
    End Class


End Namespace
