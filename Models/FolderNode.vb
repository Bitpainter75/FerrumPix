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
        Private _isFolderMissing As Boolean

        ''' <summary>Steht der Ordner nur noch im Katalog, weil sein Laufwerk nicht erreichbar ist
        ''' oder weil es ihn nicht mehr gibt (<see cref="IsFolderMissing"/>)? Dann steht er
        ''' abgesetzt im Baum (Symbol und Hinweis in der Akzentfarbe), seine Unterordner kommen aus
        ''' dem Katalog, und die Galerie zeigt ihn aus dem Katalog statt von der Platte (siehe
        ''' FolderListing). Nachgefuehrt bei jedem Einlesen der Unterordner und beim Oeffnen des
        ''' Ordners in der Galerie.</summary>
        Public Property IsOffline As Boolean
            Get
                Return _isOffline
            End Get
            Set(value As Boolean)
                If _isOffline = value Then Return
                _isOffline = value
                RaiseStateChanged()
            End Set
        End Property

        ''' <summary>Gibt es den Ordner auf einem erreichbaren Laufwerk nicht mehr (ausserhalb der
        ''' Anwendung geloescht)? Nur zusammen mit <see cref="IsOffline"/>; entscheidet ueber
        ''' Symbol und Hinweis.</summary>
        Public ReadOnly Property IsFolderMissing As Boolean
            Get
                Return _isOffline AndAlso _isFolderMissing
            End Get
        End Property

        ''' <summary>Nicht erreichbar wegen des Laufwerks: dann steht der Stecker da.</summary>
        Public ReadOnly Property IsDriveOffline As Boolean
            Get
                Return _isOffline AndAlso Not _isFolderMissing
            End Get
        End Property

        ''' <summary>Der Zustand aus <see cref="FolderListing"/>, in einem Schritt gesetzt.</summary>
        Public Property State As FolderState
            Get
                If Not _isOffline Then Return FolderState.Present
                Return If(_isFolderMissing, FolderState.Missing, FolderState.Unreachable)
            End Get
            Set(value As FolderState)
                Dim offline = value = FolderState.Unreachable OrElse value = FolderState.Missing
                Dim missing = value = FolderState.Missing
                If _isOffline = offline AndAlso _isFolderMissing = missing Then Return
                _isOffline = offline
                _isFolderMissing = missing
                RaiseStateChanged()
            End Set
        End Property

        ''' <summary>Der Hinweis am Zeichen hinter dem Namen. Hier und nicht im XAML: die Zeile
        ''' entsteht aus einer Vorlage, und deren feste Texte erreicht die Uebersetzung nicht
        ''' verlaesslich.</summary>
        Public ReadOnly Property OfflineTip As String
            Get
                Return If(_isOffline, FolderListing.ReasonText(_isFolderMissing), Nothing)
            End Get
        End Property

        Private Sub RaiseStateChanged()
            For Each propertyName In {NameOf(IsOffline), NameOf(IsFolderMissing), NameOf(IsDriveOffline), NameOf(State), NameOf(OfflineTip)}
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
            Next
        End Sub

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
            ' ZUERST die Frage nach dem Laufwerk, DANN die Platte (FolderListing). Liegt der Ordner
            ' auf einem nicht erreichbaren Laufwerk oder gibt es ihn nicht mehr, kennt nur der
            ' Katalog seine Unterordner, und ein Blick auf die Platte zeigte bestenfalls einen
            ' leeren Einhaengeort, schlimmstenfalls haengt er.
            Dim ownState = FolderListing.StateOf(FullPath)
            State = ownState
            Dim wanted As List(Of FolderEntry)
            Try
                ' Die Kinder eines erreichbaren Ordners kommen von der Platte, dazu die, die nur
                ' noch der Katalog kennt (abgezogenes Laufwerk, geloeschter Ordner); jedes mit
                ' seinem eigenen Zustand, unter /mnt etwa ist jeder Eintrag ein eigener Einhaengeort.
                wanted = FolderListing.ListSubfolders(FullPath, ownState, ShowHiddenFolders)
            Catch ex As UnauthorizedAccessException
                wanted = New List(Of FolderEntry)()
            Catch ex As IOException
                wanted = New List(Of FolderEntry)()
            End Try

            ' Platzhalter und verschwundene Ordner heraus, dann in der Reihenfolge der Liste
            ' verschieben oder neu einsetzen.
            Dim wantedSet = New HashSet(Of String)(wanted.Select(Function(w) w.Path), PathIdentity.Comparer)
            For i = Children.Count - 1 To 0 Step -1
                Dim childPath = Children(i).FullPath
                If String.IsNullOrEmpty(childPath) OrElse Not wantedSet.Contains(childPath) Then Children.RemoveAt(i)
            Next
            For i = 0 To wanted.Count - 1
                Dim target = wanted(i)
                Dim existing = -1
                For j = i To Children.Count - 1
                    If String.Equals(Children(j).FullPath, target.Path, PathIdentity.Comparison) Then
                        existing = j
                        Exit For
                    End If
                Next
                If existing < 0 Then
                    Children.Insert(i, New FolderNode(target.Path) With {.State = target.State})
                Else
                    Children(existing).State = target.State
                    If existing <> i Then Children.Move(existing, i)
                End If
            Next
        End Sub
    End Class

End Namespace
