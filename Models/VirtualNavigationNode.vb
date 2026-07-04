Imports System.Collections.ObjectModel
Imports FerrumPix.Services

Namespace Models

    Public Class VirtualNavigationNode
        Public Property Name As String
        Public Property Kind As String
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

        Public Sub New(name As String, kind As String, Optional query As String = "")
            Me.Name = name
            Me.Kind = kind
            Me.Query = query
            Children = New ObservableCollection(Of VirtualNavigationNode)()
        End Sub
    End Class

End Namespace
