Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.OpenGL
Imports Avalonia.OpenGL.Controls
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>
    ''' Hosts libmpv's OpenGL render API in Avalonia's control tree.
    ''' No native child window or platform window ID is created; Avalonia supplies
    ''' the current framebuffer to the render callback on every desktop platform.
    ''' </summary>
    Public Class MpvVideoView
        Inherits OpenGlControlBase

        Private _player As MpvPlayer
        Private _attachedPlayer As MpvPlayer
        Private _glReady As Boolean

        Public Property Player As MpvPlayer
            Get
                Return _player
            End Get
            Set(value As MpvPlayer)
                If Object.ReferenceEquals(_player, value) Then Return
                _player = value
                If _glReady Then RequestNextFrameRendering()
            End Set
        End Property

        Protected Overrides Sub OnOpenGlInit(gl As GlInterface)
            ' Avalonia has made its OpenGL context current before this callback.
            ' Attach the player here, rather than from the UI thread.
            _glReady = True
            SwitchPlayer(gl)
            RequestNextFrameRendering()
        End Sub

        Protected Overrides Sub OnOpenGlRender(gl As GlInterface, framebuffer As Integer)
            ' Both the framebuffer and libmpv's OpenGL render context are valid only
            ' while Avalonia is executing this callback with its context current.
            SwitchPlayer(gl)
            If _attachedPlayer Is Nothing Then Return

            Dim host = Avalonia.Controls.TopLevel.GetTopLevel(Me)
            Dim scaling = If(host Is Nothing, 1.0, host.RenderScaling)
            Dim width = Math.Max(1, CInt(Math.Ceiling(Bounds.Width * scaling)))
            Dim height = Math.Max(1, CInt(Math.Ceiling(Bounds.Height * scaling)))
            _attachedPlayer.RenderOpenGlFrame(framebuffer, width, height)
        End Sub

        Protected Overrides Sub OnOpenGlDeinit(gl As GlInterface)
            ' Release libmpv before Avalonia destroys this context. A render context
            ' belongs to one host context and must not be reused after deinitialization.
            DetachPlayer()
            _glReady = False
        End Sub

        Private Sub SwitchPlayer(gl As GlInterface)
            ' Navigation or disposal can replace the player while this control remains
            ' in the visual tree. Remove stale callbacks before attaching the new player.
            If _attachedPlayer IsNot Nothing AndAlso
               (Not Object.ReferenceEquals(_attachedPlayer, _player) OrElse _attachedPlayer.IsDisposeRequested) Then
                DetachPlayer()
            End If

            If _attachedPlayer Is Nothing AndAlso _player IsNot Nothing AndAlso Not _player.IsDisposeRequested Then
                Dim attached = _player.AttachOpenGl(
                    Function(name) gl.GetProcAddress(name),
                    Sub()
                        Dispatcher.UIThread.Post(
                            Sub()
                                If _glReady Then RequestNextFrameRendering()
                            End Sub,
                            DispatcherPriority.Render)
                    End Sub)
                If attached Then _attachedPlayer = _player
            End If
        End Sub

        Private Sub DetachPlayer()
            If _attachedPlayer Is Nothing Then Return
            _attachedPlayer.DetachOpenGl()
            _attachedPlayer = Nothing
        End Sub
    End Class

End Namespace
