Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Layout

Namespace Controls

    ''' <summary>Die Angabenzeile unter einer Kachel: kurze Angaben nebeneinander, von denen
    ''' LIEBER EINE WEGFAELLT, als dass sie sich ueberlagern.
    '''
    ''' WARUM. Vorher standen Datum und Masse in einem waagrechten StackPanel und die Dateigroesse
    ''' rechts daneben. Ein waagrechtes StackPanel misst mit unendlicher Breite; seine Texte wurden
    ''' deshalb nie gekuerzt, sondern liefen unter die Dateigroesse. Aus "4928x3264" und "7,5 MB"
    ''' wurde "4928x32647,5 MB", gelesen als 160 Megapixel (Nutzerbefund, pixls.us).
    '''
    ''' DIE REGEL. Die Kinder stehen in ihrer Reihenfolge von links; ein Kind mit
    ''' HorizontalAlignment="Right" rueckt an den rechten Rand. Reicht die Breite nicht, faellt das
    ''' ERSTE noch sichtbare Kind weg, dann das naechste: die Reihenfolge ist also zugleich die
    ''' Rangfolge, das Wichtigste steht zuletzt. Bleibt nur eines und passt auch das nicht, bekommt
    ''' es die ganze Breite und kuerzt sich selbst (TextTrimming). Ein Kind mit IsVisible=False
    ''' zaehlt nicht mit.</summary>
    Public Class CaptionRowPanel
        Inherits Panel

        Public Shared ReadOnly SpacingProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of CaptionRowPanel, Double)(NameOf(Spacing), 8.0)

        Private _shown As Boolean() = Array.Empty(Of Boolean)()

        Shared Sub New()
            AffectsMeasure(Of CaptionRowPanel)(SpacingProperty)
        End Sub

        ''' Ein weggelassenes Kind liegt ausserhalb, und der Rand schneidet es ab. Nur auf Groesse 0
        ''' gesetzt, zoege ein TextBlock seinen Text trotzdem ueber die Nachbarn.
        Public Sub New()
            ClipToBounds = True
        End Sub

        Public Property Spacing As Double
            Get
                Return GetValue(SpacingProperty)
            End Get
            Set(value As Double)
                SetValue(SpacingProperty, value)
            End Set
        End Property

        Protected Overrides Function MeasureOverride(availableSize As Size) As Size
            Dim count = Children.Count
            _shown = New Boolean(count - 1) {}
            Dim infinite As New Size(Double.PositiveInfinity, Double.PositiveInfinity)
            Dim height = 0.0
            For i = 0 To count - 1
                Children(i).Measure(infinite)
                _shown(i) = Children(i).IsVisible
                height = Math.Max(height, Children(i).DesiredSize.Height)
            Next

            Dim total = availableSize.Width
            If Not Double.IsInfinity(total) Then
                ' Von vorn wegnehmen, bis der Rest passt - aber nie das letzte sichtbare Kind.
                While NeededWidth() > total AndAlso VisibleCount() > 1
                    _shown(Array.IndexOf(_shown, True)) = False
                End While
                If VisibleCount() = 1 AndAlso NeededWidth() > total Then
                    Children(Array.IndexOf(_shown, True)).Measure(New Size(total, Double.PositiveInfinity))
                End If
            End If
            Return New Size(If(Double.IsInfinity(total), NeededWidth(), Math.Min(total, NeededWidth())), height)
        End Function

        Protected Overrides Function ArrangeOverride(finalSize As Size) As Size
            Dim left = 0.0
            Dim right = finalSize.Width
            ' Rechts ausgerichtete Kinder von hinten her an den rechten Rand, die uebrigen von vorn.
            For i = Children.Count - 1 To 0 Step -1
                If i >= _shown.Length OrElse Not _shown(i) Then Continue For
                If Children(i).HorizontalAlignment <> HorizontalAlignment.Right Then Continue For
                Dim width = Math.Min(Children(i).DesiredSize.Width, Math.Max(0, right))
                Children(i).Arrange(New Rect(right - width, 0, width, finalSize.Height))
                right -= width + Spacing
            Next
            For i = 0 To Children.Count - 1
                If i >= _shown.Length OrElse Not _shown(i) Then
                    Children(i).Arrange(New Rect(-100000, 0, Children(i).DesiredSize.Width, finalSize.Height))
                    Continue For
                End If
                If Children(i).HorizontalAlignment = HorizontalAlignment.Right Then Continue For
                Dim width = Math.Min(Children(i).DesiredSize.Width, Math.Max(0, right - left))
                Children(i).Arrange(New Rect(left, 0, width, finalSize.Height))
                left += width + Spacing
            Next
            Return finalSize
        End Function

        Private Function NeededWidth() As Double
            Dim width = 0.0
            Dim visible = 0
            For i = 0 To _shown.Length - 1
                If Not _shown(i) Then Continue For
                width += Children(i).DesiredSize.Width
                visible += 1
            Next
            Return width + Math.Max(0, visible - 1) * Spacing
        End Function

        Private Function VisibleCount() As Integer
            Return _shown.Count(Function(s) s)
        End Function

    End Class

End Namespace
