Imports Avalonia.Controls
Imports Avalonia.Platform
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Was der Viewer von einer Videofläche braucht, gleich welchen Ausgabeweg sie nutzt.
    ''' Es gibt zwei: <see cref="MpvVideoView"/> hängt mpv über die Option <c>wid</c> in eine native
    ''' Fläche (Windows, Linux), <see cref="MpvVideoSurface"/> holt das Bild bei mpv ab (macOS, wo
    ''' es <c>wid</c> nicht gibt).</summary>
    Public Interface IMpvVideoTarget
        Property Player As MpvPlayer
    End Interface

    Public Class MpvVideoView
        Inherits NativeControlHost
        Implements IMpvVideoTarget

        Private _player As MpvPlayer
        Private _platformHandle As IPlatformHandle

        Public Property Player As MpvPlayer Implements IMpvVideoTarget.Player
            Get
                Return _player
            End Get
            Set(value As MpvPlayer)
                If Object.ReferenceEquals(_player, value) Then Return
                If _player IsNot Nothing AndAlso value Is Nothing Then
                    _player.DetachWindow()
                End If
                _player = value
                AttachPlayer()
            End Set
        End Property

        Protected Overrides Function CreateNativeControlCore(parent As IPlatformHandle) As IPlatformHandle
            _platformHandle = MyBase.CreateNativeControlCore(parent)
            AttachPlayer()
            Return _platformHandle
        End Function

        ''' <summary>Avalonia raeumt das native Fenster ab, sobald die Flaeche unsichtbar wird -
        ''' beim Videoende und beim Verlassen des Betrachters. Der Spieler muss das erfahren:
        ''' sonst haelt er den Zeiger auf ein Fenster, das es nicht mehr gibt, laedt den naechsten
        ''' Film dorthin und zeigt nichts. Gemeldet wird nur, DASS es weg ist; mpv umzustellen
        ''' hiesse, es macht sein eigenes Fenster auf (siehe MpvPlayer.ForgetWindow).
        '''
        ''' Gemeldet wird der Zeiger des Fensters, das GERADE abgeraeumt wird, und nicht bloss
        ''' „irgendeins ist weg": Anbinden und Abraeumen gehen beide durch die Warteschlange des
        ''' Spielers, und das neue Fenster kann vor dem Abraeumen des alten ankommen.</summary>
        Protected Overrides Sub DestroyNativeControlCore(control As IPlatformHandle)
            _platformHandle = Nothing
            If control IsNot Nothing Then _player?.ForgetWindow(control.Handle)
            MyBase.DestroyNativeControlCore(control)
        End Sub

        Private Sub AttachPlayer()
            If _player Is Nothing OrElse _platformHandle Is Nothing Then Return
            _player.AttachWindow(_platformHandle.Handle)
        End Sub
    End Class

End Namespace
