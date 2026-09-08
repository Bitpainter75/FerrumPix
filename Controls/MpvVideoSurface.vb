Imports System
Imports System.Threading
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Zeigt das Videobild an, das libmpv herausgibt, statt mpv in eine native Fläche
    ''' zeichnen zu lassen. Gebraucht wird das unter macOS: die Option <c>wid</c>, über die das
    ''' Video sonst in die Anwendung eingehängt wird, hat dort kein Ziel, und mpv macht sonst ein
    ''' eigenes Fenster auf. Wie das Bild entsteht, steht bei <see cref="MpvSoftwareRenderer"/>.
    '''
    ''' <para>Anders als bei der nativen Fläche ist das hier eine gewöhnliche Zeichenfläche der
    ''' Oberfläche: die Bedienleiste liegt sichtbar darüber, und Mausklicks kommen an.</para></summary>
    Public Class MpvVideoSurface
        Inherits Control
        Implements IMpvVideoTarget

        ''' <summary>Die Obergrenze der Zielfläche in Bildpunkten. Farbwandlung und Skalierung
        ''' laufen bei diesem Ausgabeweg auf dem Prozessor; ein bildschirmfüllendes Fenster auf
        ''' einem Bildschirm mit doppelter Punktdichte würde sonst ein Vielfaches dessen umrechnen
        ''' lassen, was ein Video an Auflösung mitbringt. Darüber vergrößert die Anzeige das Bild -
        ''' das kostet nichts und fällt bei Video nicht auf.</summary>
        Private Const MaxSurfaceWidth As Integer = 1920
        Private Const MaxSurfaceHeight As Integer = 1200

        Private _player As MpvPlayer
        Private _renderer As MpvSoftwareRenderer
        Private _invalidateQueued As Integer = 0

        Public Property Player As MpvPlayer Implements IMpvVideoTarget.Player
            Get
                Return _player
            End Get
            Set(value As MpvPlayer)
                If Object.ReferenceEquals(_player, value) Then Return

                If _renderer IsNot Nothing Then
                    RemoveHandler _renderer.FrameReady, AddressOf OnFrameReady
                    _renderer = Nothing
                End If

                _player = value
                _renderer = value?.Renderer
                If _renderer IsNot Nothing Then
                    AddHandler _renderer.FrameReady, AddressOf OnFrameReady
                    UpdateSurfaceSize()
                End If
                InvalidateVisual()
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is BoundsProperty Then UpdateSurfaceSize()
        End Sub

        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            ' Erst jetzt steht die Punktdichte des Bildschirms fest, auf dem das Fenster liegt.
            UpdateSurfaceSize()
        End Sub

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            Dim renderer = _renderer
            If renderer Is Nothing Then Return
            Dim frame = renderer.CurrentFrame
            If frame Is Nothing Then Return
            If Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then Return

            context.DrawImage(frame,
                              New Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height),
                              New Rect(0, 0, Bounds.Width, Bounds.Height))
        End Sub

        ''' <summary>Kommt aus dem Zeichenfaden des Renderers. Mehrere Bilder, die vor dem nächsten
        ''' Anzeigedurchlauf eintreffen, ergeben EINE Anforderung - sonst füllt sich die
        ''' Warteschlange des Anzeigefadens schneller, als er sie abarbeitet.</summary>
        Private Sub OnFrameReady()
            If Interlocked.Exchange(_invalidateQueued, 1) = 1 Then Return
            Dispatcher.UIThread.Post(Sub()
                                         Interlocked.Exchange(_invalidateQueued, 0)
                                         InvalidateVisual()
                                     End Sub, DispatcherPriority.Render)
        End Sub

        Private Sub UpdateSurfaceSize()
            Dim renderer = _renderer
            If renderer Is Nothing Then Return

            Dim scaling = 1.0
            Dim host = TopLevel.GetTopLevel(Me)
            If host IsNot Nothing AndAlso host.RenderScaling > 0 Then scaling = host.RenderScaling

            Dim width = CInt(Math.Round(Bounds.Width * scaling))
            Dim height = CInt(Math.Round(Bounds.Height * scaling))
            If width <= 0 OrElse height <= 0 Then Return

            Dim factor = 1.0
            If width > MaxSurfaceWidth Then factor = Math.Min(factor, MaxSurfaceWidth / CDbl(width))
            If height > MaxSurfaceHeight Then factor = Math.Min(factor, MaxSurfaceHeight / CDbl(height))
            If factor < 1.0 Then
                width = Math.Max(16, CInt(Math.Floor(width * factor)))
                height = Math.Max(16, CInt(Math.Floor(height * factor)))
            End If

            renderer.SetSurfaceSize(width, height)
        End Sub
    End Class

End Namespace
