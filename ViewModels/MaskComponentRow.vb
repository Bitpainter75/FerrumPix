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

        ''' <summary>Lässt sich der Bestandteil in der Reihe bewegen? Die Reihenfolge ändert das Bild -
        ''' der erste setzt das Ergebnis, jeder weitere wird darauf verrechnet.</summary>
        Public ReadOnly Property CanMoveUp As Boolean
        Public ReadOnly Property CanMoveDown As Boolean

        ''' <summary>Trägt dieser Bestandteil überhaupt einen Modus? Der erste nicht - er setzt das
        ''' Ergebnis, und ein Modus stünde dort ohne Gegenüber.</summary>
        Public ReadOnly Property HasMode As Boolean

        Public Sub New(index As Integer, component As MaskComponent, isActive As Boolean, count As Integer)
            Me.Index = index
            Me.IsActive = isActive
            Me.CanMoveUp = index > 0
            Me.CanMoveDown = index < count - 1
            Me.HasMode = index > 0
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
            ' Ab dem zweiten steht das Zeichen IMMER, auch beim Normalfall Hinzufügen: es ist seit
            ' dem Umschalter kein blosses Etikett mehr, sondern der Knopf, mit dem man die
            ' Verknüpfung wechselt, und ein Knopf ohne Aufschrift ist nicht zu finden.
            ' Abziehen traegt den gewoehnlichen Bindestrich. Schneiden trug frueher das
            ' Multiplikationszeichen - ein Sonderzeichen im sichtbaren Text, und in einer schmalen
            ' Spalte war es ausserdem von einem Schliessen-Kreuz nicht zu unterscheiden. Ein kurzes
            ' Wort sagt dasselbe und traegt in der Spalte.
            ModeLabel = ""
            If index > 0 AndAlso component IsNot Nothing Then
                Select Case If(component.Mode, "").Trim().ToLowerInvariant()
                    Case "subtract" : ModeLabel = "-"
                    Case "intersect" : ModeLabel = LocalizationService.T("Schnitt")
                    Case Else : ModeLabel = "+"
                End Select
            End If
        End Sub
    End Class

End Namespace
