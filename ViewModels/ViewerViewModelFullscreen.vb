Imports System
Imports System.ComponentModel
Imports Avalonia.Media.Imaging
Imports ReactiveUI

Namespace ViewModels

    ' Das Bild der Vollbildflaeche.
    '
    ' Die Vollbildflaeche zeigt EIN Bild. In der Einzelansicht ist es das gezeigte (CurrentImage).
    ' Im Vergleich ist CurrentImage die ausgeblendete Einzelansicht: dort blaettert ein Feld, und
    ' die Vollbildflaeche bliebe auf dem alten Bild stehen, waehrend Infoleiste und Filmstreifen
    ' weiterlaufen. Im Vergleich zeigt das Vollbild deshalb das Bild des AKTIVEN Felds, und es
    ' blaettert genau dieses Feld (BrowsePaneIndex): was sich aendert, ist zu sehen. Beim Verlassen
    ' steht der Vergleich unveraendert da, das aktive Feld mit dem zuletzt gesehenen Bild.
    Partial Public Class ViewerViewModel

        ''' <summary>Das Bild, das die Vollbildflaeche zeigt: im Vergleich das des aktiven Felds,
        ''' sonst das gezeigte Bild. Laedt das Feld gerade ein anderes, bleibt wie auf der Flaeche
        ''' das vorige stehen, bis das neue da ist (<see cref="IsFullscreenImageLoading"/>).</summary>
        Public ReadOnly Property FullscreenImage As Bitmap
            Get
                If _isCompareMode Then Return _comparePanes(_focusedComparePane).Image
                Return CurrentImage
            End Get
        End Property

        ''' <summary>Im Vergleich: das aktive Feld laedt noch, im Vollbild steht solange das vorige
        ''' Bild gedimmt. Die Einzelansicht hat dafuer ihre Kachel (ShowTransitionImage).</summary>
        Public ReadOnly Property IsFullscreenImageLoading As Boolean
            Get
                Return _isCompareMode AndAlso _comparePanes(_focusedComparePane).IsLoading
            End Get
        End Property

        ''' <summary>Im Vollbild aus dem Vergleich mit aktivem Bezugsbild: das Feld, das beim ersten
        ''' Blaetterschritt aktiv wird und blaettert. Bei zwei Feldern das rechte, bei vier das
        ''' naechste belegte nach dem Bezug. -1 ausserhalb dieses Falls und wenn kein Feld belegt
        ''' ist. Das Bezugsbild selbst blaettert nie.</summary>
        Private Function FullscreenTakeoverPane() As Integer
            If Not _isCompareMode OrElse Not IsFullscreenMode OrElse _focusedComparePane <> 0 Then Return -1
            For i = 1 To _compareFieldCount - 1
                If _comparePanes(i).HasEntry Then Return i
            Next
            Return -1
        End Function

        ''' <summary>Meldet der Ansicht, dass im Vollbild ein anderes Bild steht. Gerufen, wo das
        ''' gezeigte Bild, der Vergleich oder das aktive Feld wechseln.</summary>
        Private Sub RaiseFullscreenImageChanged()
            Me.RaisePropertyChanged(NameOf(FullscreenImage))
            Me.RaisePropertyChanged(NameOf(IsFullscreenImageLoading))
        End Sub

        ''' <summary>Das Bild des aktiven Felds ist angekommen oder das Feld laedt ein neues: die
        ''' Vollbildflaeche zieht mit. Die anderen Felder gehen sie nichts an.</summary>
        Private Sub OnComparePaneChangedForFullscreen(sender As Object, e As PropertyChangedEventArgs)
            Dim pane = TryCast(sender, ComparePaneViewModel)
            If pane Is Nothing OrElse Not _isCompareMode OrElse pane.Index <> _focusedComparePane Then Return
            If e.PropertyName = NameOf(ComparePaneViewModel.Image) OrElse
               e.PropertyName = NameOf(ComparePaneViewModel.IsLoading) Then RaiseFullscreenImageChanged()
        End Sub

    End Class

End Namespace
