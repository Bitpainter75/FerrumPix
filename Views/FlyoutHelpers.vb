Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.LogicalTree

Namespace Views

    ''' <summary>Die Auswahlfelder der App sind keine ComboBox, sondern ein Button mit Flyout und
    ''' darin je Eintrag ein weiterer Button (Klassen „dropdown-btn"/„dropdown-item"). Ein Klick auf
    ''' einen Eintrag setzt zwar den Wert, schließt das Flyout aber NICHT von selbst - anders als bei
    ''' einer echten ComboBox. In den Overlay-Dialogen blieb die Liste dadurch offen über dem Feld
    ''' stehen; der neue Wert war verdeckt, und der Dialog wirkte, als ließe sich nichts auswählen
    ''' („bei Speichern unter kann ich PDF nicht auswählen").
    ''' Deshalb ein gemeinsamer Helfer statt drei Einzellösungen.</summary>
    Friend Module FlyoutHelpers

        ''' <summary>Schließt das Flyout, in dem das geklickte Bedienelement liegt. Tut nichts, wenn
        ''' der Aufrufer gar nicht in einem Flyout sitzt - dann ist auch nichts zu schließen.</summary>
        Friend Sub CloseContainingFlyout(source As Object)
            Dim control = TryCast(source, Control)
            If control Is Nothing Then Return

            ' Der logische Baum bleibt über die Flyout-Grenze hinweg verbunden, der visuelle nicht:
            ' das Flyout rendert in einer eigenen Popup-Wurzel. Deshalb LOGISCH nach oben suchen.
            Dim popup = control.FindLogicalAncestorOfType(Of Popup)()
            If popup IsNot Nothing Then
                popup.IsOpen = False
                Return
            End If

            ' Rückfall für Flyouts, die als eigener Overlay-Host gehostet werden.
            Dim presenter = control.FindLogicalAncestorOfType(Of FlyoutPresenter)()
            Dim host = TryCast(presenter?.Parent, Popup)
            If host IsNot Nothing Then host.IsOpen = False
        End Sub

        ''' <summary>Sorgt dafuer, dass die Ausklappliste eines Button+Flyout-Auswahlfelds NIE
        ''' schmaler ist als der Knopf selbst (wie bei echten ComboBoxen
        ''' ueber den Theme-Stil). Eine $parent-Bindung traegt hier nicht zuverlaessig ueber die
        ''' Flyout-Grenze - deshalb wird die Breite beim OEFFNEN gesetzt, wenn der Knopf fertig
        ''' vermessen ist.</summary>
        Friend Sub MatchFlyoutWidthToButton(button As Button)
            Dim flyout = TryCast(button?.Flyout, Flyout)
            If flyout Is Nothing Then Return
            AddHandler flyout.Opening, Sub()
                                           Dim content = TryCast(flyout.Content, Control)
                                           If content IsNot Nothing AndAlso button.Bounds.Width > 0 Then
                                               content.MinWidth = button.Bounds.Width
                                           End If
                                       End Sub
        End Sub

    End Module

End Namespace
