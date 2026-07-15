Imports Avalonia.Controls
Imports Avalonia.Platform
Imports FerrumPix.Services

Namespace Controls

    Public Class MpvVideoView
        Inherits NativeControlHost

        Private _player As MpvPlayer
        Private _platformHandle As IPlatformHandle

        Public Property Player As MpvPlayer
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

        Protected Overrides Sub DestroyNativeControlCore(control As IPlatformHandle)
            _platformHandle = Nothing
            MyBase.DestroyNativeControlCore(control)
        End Sub

        Private Sub AttachPlayer()
            If _player Is Nothing OrElse _platformHandle Is Nothing Then Return
            _player.AttachWindow(_platformHandle.Handle)
        End Sub
    End Class

End Namespace
