Namespace Models

    ''' <summary>Eine tatsächlich in der aktuellen Galerie vorhandene Dateiendung.</summary>
    Public Class FileTypeFilterOption
        Public Sub New(extension As String, isSelected As Boolean)
            Me.Extension = extension
            Me.IsSelected = isSelected
        End Sub

        Public ReadOnly Property Extension As String
        Public ReadOnly Property IsSelected As Boolean

        Public ReadOnly Property Label As String
            Get
                Return Extension.ToUpperInvariant()
            End Get
        End Property
    End Class
End Namespace
