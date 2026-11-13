Imports System
Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.IO
Imports FerrumPix.Services

Namespace Models

    Public Class FolderNode
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Public Property Name As String
        Public Property FullPath As String
        Public Property Children As ObservableCollection(Of FolderNode)
        Public Property ImageCount As Integer

        ''' <summary>Steht dieser Knoten als ZUSAETZLICHE Wurzel im Baum - also weil er ein
        ''' ueberwachter Katalogordner oder ein Favorit ist, und nicht der persoenliche Ordner oder
        ''' die Wurzel des Dateisystems?
        '''
        ''' Nur zum Wiedererkennen beim Abgleich: diese Wurzeln werden ausgetauscht, wenn sich die
        ''' Einstellungen oder die Favoriten aendern, und alles andere im Baum bleibt dabei stehen -
        ''' samt aufgeklappter Ordner.</summary>
        Public Property IsExtraRoot As Boolean

        Private _isExpanded As Boolean
        Private _childrenLoaded As Boolean
        Private _isOffline As Boolean

        ''' <summary>Liegt der Ordner auf einem Laufwerk, das gerade nicht erreichbar ist? Dann
        ''' steht er abgesetzt im Baum (Symbol und Hinweis in der Akzentfarbe), seine Unterordner
        ''' kommen aus dem Katalog, und die Galerie zeigt ihn aus dem Katalog statt von der Platte
        ''' (siehe OfflineVolumes). Nachgefuehrt bei jedem Einlesen der Unterordner und beim
        ''' Oeffnen des Ordners in der Galerie.</summary>
        Public Property IsOffline As Boolean
            Get
                Return _isOffline
            End Get
            Set(value As Boolean)
                If _isOffline = value Then Return
                _isOffline = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsOffline)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(OfflineTip)))
            End Set
        End Property

        ''' <summary>Der Hinweis am Stecker hinter dem Namen. Hier und nicht im XAML: die Zeile
        ''' entsteht aus einer Vorlage, und deren feste Texte erreicht die Uebersetzung nicht
        ''' verlaesslich.</summary>
        Public ReadOnly Property OfflineTip As String
            Get
                Return If(_isOffline, LocalizationService.T("Laufwerk nicht erreichbar"), Nothing)
            End Get
        End Property

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
            SyncChildren()
        End Sub

        ''' <summary>Liest die Unterordner neu und gleicht sie mit den vorhandenen Knoten ab.
        ''' Ein Ordner, der geblieben ist, behaelt seinen Knoten und damit Aufklappzustand und
        ''' geladene Unterordner; nur Neues kommt hinzu, Verschwundenes geht. Ein Neuaufbau
        ''' klappte bei jedem Nachladen alles darunter zu.</summary>
        Public Sub ReloadChildren()
            _childrenLoaded = True
            SyncChildren()
        End Sub

        ''' <summary>"Aktualisieren" im Kontextmenue: dieser Ordner und alles, was darunter
        ''' aufgeklappt ist. Zugeklappte Unterordner, die schon einmal geladen waren, lesen beim
        ''' naechsten Aufklappen neu ein, statt hier jeden einzeln von der Platte zu holen; auf
        ''' einer Netzfreigabe waere das ein Lauf ueber alles, was je offen war.</summary>
        Public Sub RefreshSubtree()
            ReloadChildren()
            For Each child In Children.ToList()
                If child.IsExpanded Then
                    child.RefreshSubtree()
                Else
                    child._childrenLoaded = False
                End If
            Next
        End Sub

        Private Sub SyncChildren()
            If String.IsNullOrEmpty(FullPath) Then
                Children.Clear()
                Return
            End If
            ' ZUERST die Frage nach dem Laufwerk, DANN die Platte. Liegt der Ordner auf einem nicht
            ' erreichbaren Laufwerk, kennt nur der Katalog seine Unterordner, und ein Blick auf
            ' die Platte zeigte bestenfalls einen leeren Einhaengeort, schlimmstenfalls haengt er.
            IsOffline = OfflineVolumes.IsFolderUnreachable(FullPath)
            Dim wanted As List(Of String)
            If IsOffline Then
                Try
                    wanted = LibraryService.Instance.GetCatalogSubfolders(FullPath).
                        Where(Function(d) ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                        ToList()
                Catch ex As Exception
                    DiagnosticLogService.LogException("FolderNode.CatalogSubfolders", ex)
                    wanted = New List(Of String)()
                End Try
            Else
                Try
                    wanted = IO.Directory.GetDirectories(FullPath).
                        Where(Function(d) ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                        OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase).
                        ToList()
                Catch ex As UnauthorizedAccessException
                    wanted = New List(Of String)()
                Catch ex As IOException
                    wanted = New List(Of String)()
                End Try
            End If

            ' Platzhalter und verschwundene Ordner heraus, dann in der Reihenfolge der Platte
            ' verschieben oder neu einsetzen.
            Dim wantedSet = New HashSet(Of String)(wanted, PathIdentity.Comparer)
            For i = Children.Count - 1 To 0 Step -1
                Dim childPath = Children(i).FullPath
                If String.IsNullOrEmpty(childPath) OrElse Not wantedSet.Contains(childPath) Then Children.RemoveAt(i)
            Next
            For i = 0 To wanted.Count - 1
                Dim target = wanted(i)
                Dim existing = -1
                For j = i To Children.Count - 1
                    If String.Equals(Children(j).FullPath, target, PathIdentity.Comparison) Then
                        existing = j
                        Exit For
                    End If
                Next
                ' Ein Kind eines nicht erreichbaren Ordners ist es ebenso. Sonst wird je Kind gefragt:
                ' unter /mnt etwa ist jeder Eintrag ein eigener Einhaengeort.
                Dim childOffline = IsOffline OrElse OfflineVolumes.IsFolderUnreachable(target)
                If existing < 0 Then
                    Children.Insert(i, New FolderNode(target) With {.IsOffline = childOffline})
                Else
                    Children(existing).IsOffline = childOffline
                    If existing <> i Then Children.Move(existing, i)
                End If
            Next
        End Sub
    End Class

End Namespace
