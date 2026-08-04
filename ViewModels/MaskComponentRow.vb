Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Zeile der Bestandteilliste einer Maske. Reine Anzeige: der Zustand liegt in der
    ''' <see cref="ImageMask"/>, hier steht nur, was das Panel zeichnen muss. Die Liste wird bei jeder
    ''' Änderung neu gebaut - sie ist kurz, und ein Abgleich Zeile für Zeile wäre mehr Buchhaltung als
    ''' Gewinn.</summary>
    Public NotInheritable Class MaskComponentRow

        Public ReadOnly Property Index As Integer
        Public ReadOnly Property KindLabel As String
        Public ReadOnly Property ModeLabel As String
        Public ReadOnly Property IsActive As Boolean
        Public ReadOnly Property IconSource As String

        Public Sub New(index As Integer, component As MaskComponent, isActive As Boolean)
            Me.Index = index
            Me.IsActive = isActive
            Const base As String = "avares://FerrumPix/Assets/Icons/outline/"
            If component Is Nothing Then
                KindLabel = LocalizationService.T("Gemalt")
                IconSource = base & "brush.svg"
            ElseIf component.IsRadialGradient Then
                KindLabel = LocalizationService.T("Radialer Verlauf")
                IconSource = base & "circle.svg"
            ElseIf component.IsLinearGradient Then
                KindLabel = LocalizationService.T("Linearer Verlauf")
                IconSource = base & "line-shape.svg"
            Else
                KindLabel = LocalizationService.T("Gemalt")
                IconSource = base & "brush.svg"
            End If

            ' Der ERSTE Bestandteil setzt das Ergebnis - sein Modus stünde da und hieße nichts.
            If index = 0 OrElse component Is Nothing Then
                ModeLabel = ""
            Else
                Select Case If(component.Mode, "").Trim().ToLowerInvariant()
                    Case "subtract" : ModeLabel = "-"
                    Case "intersect" : ModeLabel = "×"
                    Case Else : ModeLabel = "+"
                End Select
            End If
        End Sub
    End Class

End Namespace
