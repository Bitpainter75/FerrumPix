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

        ''' <summary>Zaehlt dieser Bestandteil mit? Das Auge daneben schaltet ihn ab, ohne ihn zu
        ''' entfernen - zum Nachsehen, was er beitraegt.</summary>
        Public ReadOnly Property IsComponentVisible As Boolean

        Public Sub New(index As Integer, component As MaskComponent, isActive As Boolean)
            Me.Index = index
            Me.IsActive = isActive
            ' Dasselbe Auge wie an einer Ebenenzeile, also auch derselbe Umschalter: gezeigt wird es
            ' ueber die Klasse layer-eye, abgeblendet wenn nicht gesetzt.
            Me.IsComponentVisible = component Is Nothing OrElse component.IsVisible
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
            ' HINZUFÜGEN bleibt ebenfalls leer: es ist der Normalfall, und ein Pluszeichen vor jeder
            ' Zeile sagt nichts, kostet aber die Spalte, um die alle Einträge eingerückt stünden.
            ' Sichtbar bleibt nur, was VOM Normalfall abweicht - Abziehen und Schneiden.
            ' Abziehen traegt den gewoehnlichen Bindestrich. Schneiden trug frueher das
            ' Multiplikationszeichen - ein Sonderzeichen im sichtbaren Text, und in einer schmalen
            ' Spalte war es ausserdem von einem Schliessen-Kreuz nicht zu unterscheiden. Ein kurzes
            ' Wort sagt dasselbe und traegt in der Spalte.
            ModeLabel = ""
            If index > 0 AndAlso component IsNot Nothing Then
                Select Case If(component.Mode, "").Trim().ToLowerInvariant()
                    Case "subtract" : ModeLabel = "-"
                    Case "intersect" : ModeLabel = LocalizationService.T("Schnitt")
                End Select
            End If
        End Sub
    End Class

End Namespace
