Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports FerrumPix.Services

Namespace Models

    Public Class VirtualNavigationNode
        Implements INotifyPropertyChanged

        Private _isExpanded As Boolean

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
        ''' <summary>Gesetzt, wenn dieser Knoten im Favoriten-Tab steht: Schluessel des
        ''' FavoriteEntry (siehe FavoritesService). Leer bei allen Knoten der Herkunfts-Baeume -
        ''' daran unterscheidet das Kontextmenue "Als Favorit" von "Aus Favoriten entfernen".</summary>
        Public Property FavoriteKey As String = ""
        Public Property Children As ObservableCollection(Of VirtualNavigationNode)

        ''' <summary>Aufgeklappt im Navigationsbaum. Der TreeViewItem-Style der Galerie bindet IsExpanded
        ''' für JEDEN Baum - Ordner, Suchen und Immich. Ohne diese Eigenschaft lief die Bindung für die
        ''' beiden virtuellen Bäume ins Leere (Avalonia meldete das nur ins Log, sichtbar war nichts).</summary>
        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Set(value As Boolean)
                If _isExpanded = value Then Return
                _isExpanded = value
                RaisePropertyChanged()
            End Set
        End Property

        ''' <summary>Album löschbar: nur ein echter Album-Knoten, und nur wenn „Löschen in Immich erlauben"
        ''' eingeschaltet ist - derselbe Schalter, der auch das Löschen von Fotos freigibt. Die Album-Knoten
        ''' werden beim Umlegen der Einstellung neu gebaut (GalleryViewModel.RefreshImmichDeletePermission),
        ''' daher genügt hier ein einfacher Getter ohne Benachrichtigung.</summary>
        Public ReadOnly Property CanDeleteImmichAlbum As Boolean
            Get
                Return IsImmichAlbumNode AndAlso AppSettingsService.Load().ImmichAllowDelete
            End Get
        End Property

        ''' True für einen konkreten Immich-Album-Knoten (umbenennbar, Upload-Ziel).
        Public ReadOnly Property IsImmichAlbumNode As Boolean
            Get
                Return String.Equals(Kind, "ImmichAlbum", StringComparison.Ordinal)
            End Get
        End Property

        ''' Symbol des Knotens im Immich-Baum: Personen und Orte bekommen eigene Icons,
        ''' alles andere (Alle Fotos, Alben) behält die Wolke.
        Public ReadOnly Property ImmichIconSource As String
            Get
                Select Case Kind
                    Case "ImmichPeopleRoot" : Return "avares://FerrumPix/Assets/Icons/outline/users.svg"
                    Case "ImmichPerson" : Return "avares://FerrumPix/Assets/Icons/outline/user.svg"
                    Case "ImmichPlacesRoot", "ImmichPlace" : Return "avares://FerrumPix/Assets/Icons/outline/map-pin.svg"
                    Case Else : Return "avares://FerrumPix/Assets/Icons/outline/cloud.svg"
                End Select
            End Get
        End Property

        ''' <summary>Symbol im Favoriten-Tab: der Favorit zeigt das Symbol SEINES ZIELS, damit man
        ''' Ordner, Immich-Knoten und Suchen in der gemischten Liste auseinanderhaelt.</summary>
        Public ReadOnly Property FavoriteIconSource As String
            Get
                Select Case Kind
                    Case "FavoriteFolder" : Return "avares://FerrumPix/Assets/Icons/outline/folder.svg"
                    Case "SavedSearch" : Return "avares://FerrumPix/Assets/Icons/outline/search.svg"
                    Case "FavoriteMissing" : Return "avares://FerrumPix/Assets/Icons/outline/alert-triangle.svg"
                    Case Else : Return ImmichIconSource
                End Select
            End Get
        End Property

        ''' <summary>Favorit, der auf einen ORDNER zeigt - bekommt im Favoriten-Tab dasselbe
        ''' Kontextmenue wie der Ordnerbaum.</summary>
        Public ReadOnly Property IsFolderFavorite As Boolean
            Get
                Return String.Equals(Kind, "FavoriteFolder", StringComparison.Ordinal)
            End Get
        End Property

        ''' <summary>Gespeicherte Suche - bekommt Bearbeiten/Entfernen wie im Suchbaum.</summary>
        Public ReadOnly Property IsSavedSearchNode As Boolean
            Get
                Return String.Equals(Kind, "SavedSearch", StringComparison.Ordinal)
            End Get
        End Property

        ''' True, solange dieser Knoten in einem Favoriten-Tab steht (Kontextmenue zeigt dann
        ''' "Aus Favoriten entfernen" statt "Als Favorit").
        Public ReadOnly Property IsFavoriteNode As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(FavoriteKey)
            End Get
        End Property

        ''' <summary>Kann dieser Knoten Favorit werden? Alles Navigierbare - Ordner-Ersatzknoten,
        ''' gespeicherte Suchen und echte Immich-Ziele. NICHT: "Neue Suche" und die reinen
        ''' Klapp-Knoten (Personen/Orte-Wurzel).</summary>
        Public ReadOnly Property CanBecomeFavorite As Boolean
            Get
                If IsFavoriteNode Then Return False
                Select Case Kind
                    Case "SavedSearch", "ImmichAll", "ImmichAlbum", "ImmichPerson", "ImmichPlace" : Return True
                    Case Else : Return False
                End Select
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

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub RaisePropertyChanged(<CallerMemberName> Optional propertyName As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class

End Namespace
