Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Styling

Namespace Controls

    Public Class SliderValueUpDown
        Inherits NumericUpDown

        ''' <summary>Übernimmt das Getippte ERST beim Verlassen des Feldes (oder mit der
        ''' Eingabetaste), statt bei jedem Anschlag.
        '''
        ''' Gedacht für gekoppelte Felderpaare wie Breite und Höhe bei „Seitenverhältnis
        ''' beibehalten": ohne das schlägt jeder einzelne Anschlag durch, und wer in ein 4000
        ''' breites Bild eine 1500 tippt, sieht die Höhe erst auf 1, dann auf 11, dann auf 112
        ''' springen - jeder Zwischenstand rundet die gekoppelte Kante neu, und beim Zurücktippen
        ''' bleibt sie verbogen stehen. Die Schrittknöpfe, Pfeiltasten und das Mausrad zählen
        ''' weiterhin sofort: das sind fertige Werte, keine halb getippten.</summary>
        Public Shared ReadOnly DeferTextCommitProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of SliderValueUpDown, Boolean)(NameOf(DeferTextCommit), False)

        Public Property DeferTextCommit As Boolean
            Get
                Return GetValue(DeferTextCommitProperty)
            End Get
            Set(value As Boolean)
                SetValue(DeferTextCommitProperty, value)
            End Set
        End Property

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
            ' Verzögerte Übernahme: solange die Tastatur in diesem Feld steht, bleibt der Wert, wie
            ' er ist. NumericUpDown holt ihn beim Fokusverlust und bei der Eingabetaste von selbst
            ' aus dem Text (CommitInput) - dann, und nur dann, zieht eine gekoppelte Kante nach.
            If DeferTextCommit AndAlso IsKeyboardFocusWithin AndAlso Not _restoringText Then Return
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
                    ' Bei verzögerter Übernahme steht im Feld etwas, das noch nicht gilt - ESC
                    ' verwirft es und holt den geltenden Wert zurück.
                    RestoreTextFromValue(force:=DeferTextCommit)
                    e.Handled = True
            End Select
        End Sub

        ''' Läuft gerade das Zurückholen? Dann muss der geschriebene Text auch bei verzögerter
        ''' Übernahme durchlaufen, sonst bliebe das sichtbare Feld auf dem verworfenen Getippten
        ''' stehen: die Anzeige hängt am inneren Textfeld, und das schreibt erst die Basisklasse.
        Private _restoringText As Boolean

        ''' Holt die Anzeige zurück an den Wert - nach einem leeren oder unvollständigen Feld.
        Private Sub RestoreTextFromValue(Optional force As Boolean = False)
            If Not force AndAlso Not String.IsNullOrWhiteSpace(Text) Then Return
            Dim current = If(Value, 0D)
            _restoringText = True
            Try
                Text = current.ToString(FormatString, System.Globalization.CultureInfo.CurrentCulture)
            Finally
                _restoringText = False
            End Try
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
