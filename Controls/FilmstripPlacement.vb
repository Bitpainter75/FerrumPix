Imports System
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Controls.Templates
Imports Avalonia.Layout

Namespace Controls

    ''' <summary>
    ''' Stellt den Filmstreifen von Betrachter und Editor an die eingestellte Seite
    ''' (<see cref="Services.AppSettingsService.NormalizeFilmstripPosition"/>).
    '''
    ''' ES GIBT NUR EINE LISTE. Sie wandert zwischen zwei Plaetzen: unten liegt sie wie bisher in der
    ''' unteren Leiste ueber der Fusszeile, fuer oben, links und rechts in einem eigenen Rahmen ueber
    ''' der Bildflaeche. Zwei Listen mit je eigener Bindung, Auswahl, Vorschaubildern und Zug auf die
    ''' Leinwand waeren zweimal dieselbe Bedienung, die auseinanderlaufen kann. Der Name der Liste
    ''' bleibt im Namensraum der Ansicht, FindControl findet sie also an jedem der beiden Plaetze.
    '''
    ''' Der seitliche Rahmen liegt UEBER der Bildflaeche, nicht in einer eigenen Spalte des Gitters:
    ''' die Bildflaeche und das Videofenster werden dafuer um seine Groesse eingerueckt
    ''' (<see cref="ContentInset"/>). Eine zusaetzliche Spalte haette jede Spaltenangabe der Ansicht
    ''' verschoben, auch die im Code (Video-Lage, Vollbild).
    ''' </summary>
    Public Module FilmstripPlacement

        Public Const PositionBottom As String = "Bottom"
        Public Const PositionTop As String = "Top"
        Public Const PositionLeft As String = "Left"
        Public Const PositionRight As String = "Right"

        ''' <summary>Hoehe des Streifens oben: Liste 110, Polsterung 6 oben und unten, Trennlinie 1.</summary>
        Public Const TopExtent As Double = 123
        ''' <summary>Breite des Streifens links oder rechts: Kachel 148, der groesste Kachelabstand
        ''' (24), Platz fuer die Rollleiste und die Polsterung.</summary>
        Public Const SideExtent As Double = 196

        Public Function IsVertical(position As String) As Boolean
            Return position = PositionLeft OrElse position = PositionRight
        End Function

        ''' <summary>Um wie viel Bildflaeche und Videofenster einruecken, damit der Streifen sie nicht
        ''' verdeckt. Unten ist nichts einzuruecken, dort traegt ihn die untere Leiste.</summary>
        Public Function ContentInset(position As String, filmstripVisible As Boolean) As Thickness
            If Not filmstripVisible Then Return New Thickness(0)
            Select Case position
                Case PositionTop : Return New Thickness(0, TopExtent, 0, 0)
                Case PositionLeft : Return New Thickness(SideExtent, 0, 0, 0)
                Case PositionRight : Return New Thickness(0, 0, SideExtent, 0)
                Case Else : Return New Thickness(0)
            End Select
        End Function

        ''' <summary>Haengt die Liste an ihren Platz und richtet sie aus. <paramref name="bottomSlot"/>
        ''' ist das Gitter der unteren Leiste (Zeile 0 traegt die Liste), <paramref name="sideSlot"/>
        ''' der Rahmen ueber der Bildflaeche.</summary>
        Public Sub Apply(listBox As ListBox, bottomSlot As Panel, sideSlot As Border, position As String, filmstripVisible As Boolean)
            If listBox Is Nothing OrElse bottomSlot Is Nothing OrElse sideSlot Is Nothing Then Return
            Dim vertical = IsVertical(position)
            Dim atBottom = Not vertical AndAlso position <> PositionTop

            If atBottom Then
                If listBox.Parent IsNot bottomSlot Then
                    Detach(listBox)
                    Grid.SetRow(listBox, 0)
                    bottomSlot.Children.Insert(0, listBox)
                End If
            ElseIf listBox.Parent IsNot sideSlot Then
                Detach(listBox)
                sideSlot.Child = listBox
            End If
            sideSlot.IsVisible = Not atBottom AndAlso filmstripVisible

            ' Die Seite des Rahmens: eine Kante, die Trennlinie zur Bildflaeche hin.
            Select Case position
                Case PositionTop
                    sideSlot.HorizontalAlignment = HorizontalAlignment.Stretch
                    sideSlot.VerticalAlignment = VerticalAlignment.Top
                    sideSlot.Width = Double.NaN
                    sideSlot.Height = TopExtent
                    sideSlot.BorderThickness = New Thickness(0, 0, 0, 1)
                    sideSlot.Padding = New Thickness(24, 6, 24, 6)
                Case PositionLeft, PositionRight
                    sideSlot.HorizontalAlignment = If(position = PositionLeft, HorizontalAlignment.Left, HorizontalAlignment.Right)
                    sideSlot.VerticalAlignment = VerticalAlignment.Stretch
                    sideSlot.Width = SideExtent
                    sideSlot.Height = Double.NaN
                    sideSlot.BorderThickness = If(position = PositionLeft, New Thickness(0, 0, 1, 0), New Thickness(1, 0, 0, 0))
                    sideSlot.Padding = New Thickness(6, 8, 6, 8)
            End Select

            SetOrientation(listBox, vertical)
        End Sub

        ''' <summary>Haengt die Liste waagerecht in einen Rahmen ueber dem Vollbild. Zurueck an ihren
        ''' eingestellten Platz bringt sie das naechste <see cref="Apply"/>.</summary>
        Public Sub MoveIntoOverlay(listBox As ListBox, overlay As Border)
            If listBox Is Nothing OrElse overlay Is Nothing Then Return
            If listBox.Parent IsNot overlay Then
                Detach(listBox)
                overlay.Child = listBox
            End If
            SetOrientation(listBox, False)
        End Sub

        Private Sub Detach(listBox As ListBox)
            Dim frame = TryCast(listBox.Parent, Border)
            If frame IsNot Nothing Then
                frame.Child = Nothing
                Return
            End If
            TryCast(listBox.Parent, Panel)?.Children.Remove(listBox)
        End Sub

        Private Sub SetOrientation(listBox As ListBox, vertical As Boolean)
            ' Waagerecht steht die Hoehe fest und die Breite rollt, senkrecht umgekehrt. Die acht
            ' Punkte Abstand halten die Kacheln von der Rollleiste weg, die am Ende der Laufrichtung
            ' quer liegt.
            If vertical Then
                listBox.Height = Double.NaN
                ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled)
                ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto)
            Else
                listBox.Height = 110
                ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Auto)
                ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled)
            End If
            Dim orientation = If(vertical, Avalonia.Layout.Orientation.Vertical, Avalonia.Layout.Orientation.Horizontal)
            Dim built = TryCast(listBox.ItemsPanelRoot, VirtualizingStackPanel)
            If built Is Nothing OrElse built.Orientation <> orientation Then
                Dim panelMargin = If(vertical, New Thickness(0, 0, 8, 0), New Thickness(0, 0, 0, 8))
                listBox.ItemsPanel = New FuncTemplate(Of Panel)(
                    Function() New VirtualizingStackPanel With {.Orientation = orientation, .Margin = panelMargin})
            End If
        End Sub
    End Module

End Namespace
