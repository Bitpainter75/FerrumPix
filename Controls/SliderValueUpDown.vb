Imports Avalonia.Controls
Imports Avalonia.Input

Namespace Controls

    Public Class SliderValueUpDown
        Inherits NumericUpDown

        Public Sub New()
            Increment = 1D
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If e.Handled Then Return

            Select Case e.Key
                Case Key.Up, Key.PageUp
                    StepValue(Increment)
                    e.Handled = True
                Case Key.Down, Key.PageDown
                    StepValue(-Increment)
                    e.Handled = True
            End Select
        End Sub

        Private Sub StepValue(delta As Decimal)
            Dim current = If(Value, 0D)
            Dim nextValue = current + delta
            If nextValue < Minimum Then nextValue = Minimum
            If nextValue > Maximum Then nextValue = Maximum
            Value = nextValue
        End Sub
    End Class

End Namespace
