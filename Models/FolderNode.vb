Imports System
Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.IO

Namespace Models

    Public Class FolderNode
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Public Property Name As String
        Public Property FullPath As String
        Public Property Children As ObservableCollection(Of FolderNode)
        Public Property ImageCount As Integer

        Private _isExpanded As Boolean
        Private _childrenLoaded As Boolean

        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Set(value As Boolean)
                If _isExpanded = value Then Return
                _isExpanded = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsExpanded)))
                If value Then EnsureChildrenLoaded()
            End Set
        End Property

        Public Sub New(folderPath As String)
            FullPath = folderPath
            Name = IO.Path.GetFileName(folderPath)
            If String.IsNullOrEmpty(Name) Then Name = folderPath
            Children = New ObservableCollection(Of FolderNode)()
            Children.Add(CreatePlaceholder())
        End Sub

        Public Sub New(displayName As String, folderPath As String)
            Me.Name = displayName
            FullPath = folderPath
            Children = New ObservableCollection(Of FolderNode)()
            Children.Add(CreatePlaceholder())
        End Sub

        Private Shared Function CreatePlaceholder() As FolderNode
            Dim ph As New FolderNode()
            ph.Name = "..."
            ph.Children = New ObservableCollection(Of FolderNode)()
            Return ph
        End Function

        Private Sub New()
            Children = New ObservableCollection(Of FolderNode)()
        End Sub

        Public Shared Property ShowHiddenFolders As Boolean = False

        Public Sub EnsureChildrenLoaded()
            If _childrenLoaded Then Return
            _childrenLoaded = True
            Children.Clear()
            If String.IsNullOrEmpty(FullPath) Then Return
            Try
                Dim dirs = IO.Directory.GetDirectories(FullPath).
                    OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
                For Each dirEntry In dirs
                    Dim folderName = IO.Path.GetFileName(dirEntry)
                    If Not ShowHiddenFolders AndAlso folderName.StartsWith(".") Then Continue For
                    Children.Add(New FolderNode(dirEntry))
                Next
            Catch ex As UnauthorizedAccessException
            Catch ex As IOException
            End Try
        End Sub

        Public Sub ReloadChildren()
            _childrenLoaded = False
            EnsureChildrenLoaded()
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(Children)))
        End Sub
    End Class

End Namespace
