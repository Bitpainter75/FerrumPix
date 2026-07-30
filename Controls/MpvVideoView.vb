Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.OpenGL
Imports Avalonia.OpenGL.Controls
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Controls

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
            _glReady = True
            SwitchPlayer(gl)
            RequestNextFrameRendering()
        End Sub

        Protected Overrides Sub OnOpenGlRender(gl As GlInterface, framebuffer As Integer)
            SwitchPlayer(gl)
            If _attachedPlayer Is Nothing Then Return

            Dim host = Avalonia.Controls.TopLevel.GetTopLevel(Me)
            Dim scaling = If(host Is Nothing, 1.0, host.RenderScaling)
            Dim width = Math.Max(1, CInt(Math.Ceiling(Bounds.Width * scaling)))
            Dim height = Math.Max(1, CInt(Math.Ceiling(Bounds.Height * scaling)))
            _attachedPlayer.RenderOpenGlFrame(framebuffer, width, height)
        End Sub

        Protected Overrides Sub OnOpenGlDeinit(gl As GlInterface)
            DetachPlayer()
            _glReady = False
        End Sub

        Private Sub SwitchPlayer(gl As GlInterface)
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
