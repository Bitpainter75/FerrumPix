Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Styling

Namespace Controls

    Public Class SliderValueUpDown
        Inherits NumericUpDown

        Protected Overrides ReadOnly Property StyleKeyOverride As Type
            Get
                Return GetType(NumericUpDown)
            End Get
        End Property

        Public Sub New()
            Increment = 1D
            FormatString = "F0"
            AddHandler LostFocus, Sub(s, e) RestoreTextFromValue()
        End Sub

        ''' <summary>Ein leeres Feld (Entf/Rücktaste) darf nicht in den Wert durchschlagen. NumericUpDown
        ''' setzt Value sonst auf Nothing, die Zwei-Wege-Bindung versucht damit eine nicht-nullbare
        ''' Zahl-Eigenschaft im ViewModel zu füllen, und der Konvertierungsfehler landet als Text der
        ''' Ausnahme im Feld - sichtbar wird davon im schmalen Kästchen das führende "System...".
        ''' Stattdessen bleibt der bisherige Wert stehen, das Feld darf beim Tippen aber leer aussehen;
        ''' beim Verlassen wird die Zahl wieder hingeschrieben.</summary>
        Protected Overrides Sub OnTextChanged(oldValue As String, newValue As String)
            If String.IsNullOrWhiteSpace(newValue) Then Return
            MyBase.OnTextChanged(oldValue, newValue)
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
                Case Key.Enter, Key.Return
                    RestoreTextFromValue()
                Case Key.Escape
                    RestoreTextFromValue()
                    e.Handled = True
            End Select
        End Sub

        ''' Holt die Anzeige zurück an den Wert - nach einem leeren oder unvollständigen Feld.
        Private Sub RestoreTextFromValue()
            If Not String.IsNullOrWhiteSpace(Text) Then Return
            Dim current = If(Value, 0D)
            Text = current.ToString(FormatString, System.Globalization.CultureInfo.CurrentCulture)
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
