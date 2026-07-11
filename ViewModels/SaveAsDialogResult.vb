Namespace ViewModels

    Public Class SaveAsDialogResult
        Public Property BaseName As String
        Public Property Format As String
        Public Property JpgQuality As Integer
        ''' Zielort: "Local" (Ordner) oder "Immich" (Upload als neues Asset).
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""

        Public ReadOnly Property Extension As String
            Get
                Select Case If(Format, "").Trim().ToUpperInvariant()
                    Case "PNG"
                        Return ".png"
                    Case "WEBP"
                        Return ".webp"
                    Case Else
                        Return ".jpg"
                End Select
            End Get
        End Property

        Public ReadOnly Property FileName As String
            Get
                Return BaseName & Extension
            End Get
        End Property
    End Class

End Namespace
