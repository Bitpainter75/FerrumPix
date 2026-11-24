Imports System

Namespace ViewModels

    ''' <summary>Begleitet <see cref="ViewerViewModel.CenteredZoomRequested"/>, wenn der Zoom nicht
    ''' um die Mitte des Sichtfensters gehen soll, sondern eine bestimmte Stelle des Bildes in die
    ''' Mitte ruecken (Z bei sichtbaren Fokusfeldern). X und Y sind Anteile des gezeigten Bildes,
    ''' 0 bis 1, vor Drehen und Spiegeln des Betrachters.</summary>
    Public NotInheritable Class ZoomAnchorEventArgs
        Inherits EventArgs

        Public Sub New(x As Double, y As Double)
            Me.X = x
            Me.Y = y
        End Sub

        Public ReadOnly Property X As Double
        Public ReadOnly Property Y As Double
    End Class

End Namespace
