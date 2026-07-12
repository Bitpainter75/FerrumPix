Imports System.Collections.ObjectModel
Imports FerrumPix.Services

Namespace Models

    Public Class VirtualNavigationNode
        Public Property Name As String
        Public Property Kind As String
        ''' Für gespeicherte Suchen: "Local" oder "Immich".
        Public Property Source As String = "Local"
        Public Property Query As String
        Public Property Id As String
        Public Property TextQuery As String
        Public Property RootFolder As String
        Public Property IncludeSubfolders As Boolean = True
        Public Property FavoriteMode As String = "Any"
        Public Property RatingMin As Integer = -1
        Public Property Ratings As New List(Of Integer)()
        Public Property Results As New List(Of String)()
        Public Property Conditions As New List(Of SearchCondition)()
        Public Property ConditionCombinator As String = "AND"
        Public Property IsRemovable As Boolean
        Public Property Children As ObservableCollection(Of VirtualNavigationNode)

        ''' <summary>Aufgeklappt im Navigationsbaum. Der TreeViewItem-Style der Galerie bindet IsExpanded
        ''' für JEDEN Baum - Ordner, Suchen und Immich. Ohne diese Eigenschaft lief die Bindung für die
        ''' beiden virtuellen Bäume ins Leere (Avalonia meldete das nur ins Log, sichtbar war nichts).</summary>
        Public Property IsExpanded As Boolean

        ''' True für einen konkreten Immich-Album-Knoten (umbenennbar, Upload-Ziel).
        Public ReadOnly Property IsImmichAlbumNode As Boolean
            Get
                Return String.Equals(Kind, "ImmichAlbum", StringComparison.Ordinal)
            End Get
        End Property

        ''' True für jeden Immich-Knoten (Album oder „Alle Fotos") - zeigt das Immich-Kontextmenü.
        Public ReadOnly Property IsImmichNode As Boolean
            Get
                Return String.Equals(Kind, "ImmichAlbum", StringComparison.Ordinal) OrElse String.Equals(Kind, "ImmichAll", StringComparison.Ordinal)
            End Get
        End Property

        Public Sub New(name As String, kind As String, Optional query As String = "")
            Me.Name = name
            Me.Kind = kind
            Me.Query = query
            Children = New ObservableCollection(Of VirtualNavigationNode)()
        End Sub
    End Class

End Namespace
