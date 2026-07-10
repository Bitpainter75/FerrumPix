Imports Avalonia.Controls
Imports Avalonia.Platform
Imports LibVLCSharp.Shared

Namespace Controls

    ''' <summary>
    ''' Ausgabefläche für LibVLC: ein natives Kindfenster, dessen Handle dem MediaPlayer als
    ''' Zeichenziel übergeben wird. Ersetzt LibVLCSharp.Avalonia.VideoView, weil jenes Paket gegen
    ''' Avalonia 11 gebaut ist und unter Avalonia 12 mit MissingMethodException stirbt
    ''' (Visual.get_VisualRoot hat dort einen anderen Rückgabetyp) - siehe Avalonia12.md.
    '''
    ''' Vom Original übernommen ist nur das Wesentliche: Handle erzeugen, Handle an den Player
    ''' binden, beim Zerstören wieder lösen. Das Overlay-Fenster für eine Content-Property hat das
    ''' Original zusätzlich; FerrumPix nutzt sie nicht, die Bedienelemente liegen im Avalonia-Baum
    ''' neben dem VideoView (siehe ViewerView.axaml).
    ''' </summary>
    Public Class VideoView
        Inherits NativeControlHost

        Private _mediaPlayer As MediaPlayer
        Private _platformHandle As IPlatformHandle

        ''' Der Player, der in dieses Control zeichnet. Nothing löst die Bindung wieder.
        ''' Die Zuweisung darf erst erfolgen, wenn das Control eine reale Größe hat - sonst gibt es
        ''' kein Handle, an das gebunden werden könnte (siehe ViewerView.AttachVideoPlayer).
        Public Property MediaPlayer As MediaPlayer
            Get
                Return _mediaPlayer
            End Get
            Set(value As MediaPlayer)
                If Object.ReferenceEquals(_mediaPlayer, value) Then Return
                DetachPlayer()
                _mediaPlayer = value
                AttachPlayer()
            End Set
        End Property

        Protected Overrides Function CreateNativeControlCore(parent As IPlatformHandle) As IPlatformHandle
            _platformHandle = MyBase.CreateNativeControlCore(parent)
            ' Das Handle entsteht erst hier - ein vorher gesetzter Player wurde also noch nicht gebunden.
            AttachPlayer()
            Return _platformHandle
        End Function

        Protected Overrides Sub DestroyNativeControlCore(control As IPlatformHandle)
            DetachPlayer()
            _platformHandle = Nothing
            MyBase.DestroyNativeControlCore(control)
        End Sub

        Private Sub AttachPlayer()
            If _mediaPlayer Is Nothing OrElse _platformHandle Is Nothing Then Return

            If OperatingSystem.IsWindows() Then
                _mediaPlayer.Hwnd = _platformHandle.Handle
            ElseIf OperatingSystem.IsLinux() Then
                ' X11-Fenster-IDs sind 32 Bit; IntPtr ist hier 64 Bit breit.
                _mediaPlayer.XWindow = CUInt(_platformHandle.Handle.ToInt64() And &HFFFFFFFFL)
            ElseIf OperatingSystem.IsMacOS() Then
                _mediaPlayer.NsObject = _platformHandle.Handle
            End If
        End Sub

        ''' Ohne Lösen zeichnet LibVLC weiter in ein Fenster, das es nicht mehr gibt.
        Private Sub DetachPlayer()
            If _mediaPlayer Is Nothing Then Return

            If OperatingSystem.IsWindows() Then
                _mediaPlayer.Hwnd = IntPtr.Zero
            ElseIf OperatingSystem.IsLinux() Then
                _mediaPlayer.XWindow = 0UI
            ElseIf OperatingSystem.IsMacOS() Then
                _mediaPlayer.NsObject = IntPtr.Zero
            End If
        End Sub

    End Class

End Namespace
