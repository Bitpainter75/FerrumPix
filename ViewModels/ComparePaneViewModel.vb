Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Flaeche des Bildvergleichs im Betrachter: ihr Eintrag, ihr Bild, die Marken
    ''' aus dem Katalog, Fokusfelder und Fokus-Peaking.
    '''
    ''' Der Betrachter haelt vier davon und zeigt zwei oder vier (ViewerViewModel.ComparePanes).
    ''' Flaeche 0 ist das angeheftete Bezugsbild. Die Flaeche selbst entscheidet nichts: was in ihr
    ''' steht, setzt der Betrachter, und jeder Knopf auf ihr ruft ihn mit ihrem Index.
    '''
    ''' Der Eintrag ist der Pfad einer Datei oder der Schluessel einer zusaetzlichen Variante
    ''' (Services.VariantKey), wie ueberall im Betrachter.</summary>
    Public NotInheritable Class ComparePaneViewModel
        Inherits ReactiveObject

        Friend Sub New(owner As ViewerViewModel, index As Integer)
            Me.Owner = owner
            Me.Index = index
        End Sub

        ''' <summary>Der Betrachter, zu dem die Flaeche gehoert.</summary>
        Public ReadOnly Property Owner As ViewerViewModel

        ''' <summary>Die Stelle der Flaeche, 0 bis 3. 0 ist das Bezugsbild.</summary>
        Public ReadOnly Property Index As Integer

        Private _path As String = ""
        Private _fileName As String = ""

        ''' <summary>Der Eintrag der Flaeche, leer bei einer leeren Flaeche.</summary>
        Public ReadOnly Property Path As String
            Get
                Return _path
            End Get
        End Property

        ''' <summary>Der Name, wie er auf der Flaeche steht.</summary>
        Public ReadOnly Property FileName As String
            Get
                Return _fileName
            End Get
        End Property

        ''' <summary>Leer: die Flaeche gehoert zur Buehne, traegt aber kein Bild (das vierte Feld
        ''' bei drei Bildern). Sie ist gedimmt, hat keine Marken und bekommt keinen Fokus.</summary>
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return _path.Length = 0
            End Get
        End Property

        Public ReadOnly Property HasEntry As Boolean
            Get
                Return _path.Length > 0
            End Get
        End Property

        Friend Sub SetEntry(entry As String, displayName As String)
            entry = If(entry, "")
            displayName = If(displayName, "")
            If String.Equals(_path, entry, StringComparison.Ordinal) AndAlso
               String.Equals(_fileName, displayName, StringComparison.Ordinal) Then Return
            _path = entry
            _fileName = displayName
            Me.RaisePropertyChanged(NameOf(Path))
            Me.RaisePropertyChanged(NameOf(FileName))
            Me.RaisePropertyChanged(NameOf(IsEmpty))
            Me.RaisePropertyChanged(NameOf(HasEntry))
            RaiseLoadingState()
        End Sub

        Private _isVisible As Boolean

        ''' <summary>Gehoert die Flaeche zur Buehne? Bei zwei Feldern die ersten beiden.</summary>
        Public Property IsVisible As Boolean
            Get
                Return _isVisible
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isVisible, value)
            End Set
        End Property

        Private _isFocused As Boolean

        ''' <summary>Die aktive Flaeche: sie traegt den Akzentrahmen, Fusszeile und Info-Leiste
        ''' zeigen ihr Bild, und bei vier Feldern blaettert sie.</summary>
        Public Property IsFocused As Boolean
            Get
                Return _isFocused
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFocused, value)
            End Set
        End Property

        ''' <summary>Das angeheftete Bezugsbild. Es blaettert nicht.</summary>
        Public ReadOnly Property IsAnchor As Boolean
            Get
                Return Index = 0
            End Get
        End Property

        ' ── Bild ────────────────────────────────────────────────────────────────

        Private _image As Bitmap
        Private _loadedEntry As String = ""
        Private _rawDeveloped As Boolean
        Private _tileImage As Bitmap

        ''' <summary>Das gezeigte Bild. Nach einem Wechsel des Eintrags bleibt das vorige stehen,
        ''' bis das neue geladen ist (<see cref="IsLoading"/>): sonst fiele der Umfang der Flaeche
        ''' zusammen, und ein vergroesserter Ausschnitt spraenge bei jedem Blaettern an den Rand.</summary>
        Public ReadOnly Property Image As Bitmap
            Get
                Return _image
            End Get
        End Property

        ''' <summary>Der Eintrag, zu dem <see cref="Image"/> gehoert.</summary>
        Friend ReadOnly Property LoadedEntry As String
            Get
                Return _loadedEntry
            End Get
        End Property

        ''' <summary>Hat die RAW-Entwicklung das Bild geliefert (und nicht die eingebettete
        ''' Vorschau als Rueckfall)? Davon haengt ab, welche Geometrie die Fokusfelder durchlaufen.</summary>
        Friend ReadOnly Property IsRawDeveloped As Boolean
            Get
                Return _rawDeveloped
            End Get
        End Property

        ''' <summary>Steht das Bild des Eintrags schon da?</summary>
        Friend ReadOnly Property HasCurrentImage As Boolean
            Get
                Return _image IsNot Nothing AndAlso String.Equals(_loadedEntry, _path, StringComparison.Ordinal)
            End Get
        End Property

        ''' <summary>Wird das Bild des Eintrags noch geladen? Das vorige ist solange gedimmt, eine
        ''' Flaeche ohne Bild zeigt die Kachel aus dem Filmstreifen (<see cref="TileImage"/>).</summary>
        Public ReadOnly Property IsLoading As Boolean
            Get
                Return _path.Length > 0 AndAlso Not HasCurrentImage AndAlso
                       Not String.Equals(_failedEntry, _path, StringComparison.Ordinal)
            End Get
        End Property

        Private _failedEntry As String = ""

        ''' <summary>Das Bild des Eintrags liess sich nicht laden: die Ladeanzeige geht weg, die
        ''' Flaeche bleibt ohne Bild stehen.</summary>
        Friend Sub MarkLoadFailed()
            _failedEntry = _path
            RaiseLoadingState()
        End Sub

        ''' <summary>Die Kachel aus dem Filmstreifen, solange die Flaeche noch gar kein Bild hat.
        ''' Sie gehoert dem Filmstreifen und wird hier nie freigegeben. Nie die Vorschau der
        ''' Kamera: als Vergleichsbild ist die tabu.</summary>
        Public ReadOnly Property TileImage As Bitmap
            Get
                Return If(_image Is Nothing AndAlso _path.Length > 0, _tileImage, Nothing)
            End Get
        End Property

        Friend Sub SetTile(tile As Bitmap)
            If Object.ReferenceEquals(_tileImage, tile) Then Return
            _tileImage = tile
            Me.RaisePropertyChanged(NameOf(TileImage))
        End Sub

        ''' <summary>Das geladene Bild des Eintrags einsetzen. Das abgeloeste wird verzoegert
        ''' freigegeben, damit kein laufender Layoutdurchlauf mehr darauf zugreift.</summary>
        Friend Sub SetLoadedImage(bitmap As Bitmap, entry As String, rawDeveloped As Boolean)
            Dim previous = _image
            _image = bitmap
            _loadedEntry = If(bitmap Is Nothing, "", If(entry, ""))
            _rawDeveloped = bitmap IsNot Nothing AndAlso rawDeveloped
            Me.RaisePropertyChanged(NameOf(Image))
            Me.RaisePropertyChanged(NameOf(TileImage))
            RaiseLoadingState()
            If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, bitmap) Then ReleaseDeferred(previous)
        End Sub

        Private Sub RaiseLoadingState()
            Me.RaisePropertyChanged(NameOf(IsLoading))
            Me.RaisePropertyChanged(NameOf(TileImage))
        End Sub

        Private Shared Sub ReleaseDeferred(bitmap As Bitmap)
            If bitmap Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub() bitmap.Dispose(), DispatcherPriority.Background)
        End Sub

        ' ── Marken aus dem Katalog ──────────────────────────────────────────────

        Private _rating As Integer
        Private _isFavorite As Boolean
        Private _colorLabel As String = ""
        Private _pickState As Integer

        Public ReadOnly Property Rating As Integer
            Get
                Return _rating
            End Get
        End Property

        Public ReadOnly Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
        End Property

        Public ReadOnly Property ColorLabel As String
            Get
                Return _colorLabel
            End Get
        End Property

        Friend ReadOnly Property PickState As Integer
            Get
                Return _pickState
            End Get
        End Property

        Public ReadOnly Property IsPicked As Boolean
            Get
                Return _pickState > 0
            End Get
        End Property

        Public ReadOnly Property IsRejected As Boolean
            Get
                Return _pickState < 0
            End Get
        End Property

        Private Function HasColorLabel(hex As String) As Boolean
            Return String.Equals(_colorLabel, hex, StringComparison.OrdinalIgnoreCase)
        End Function

        Public ReadOnly Property IsColorLabelOrange As Boolean
            Get
                Return HasColorLabel("#F08A1A")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelRed As Boolean
            Get
                Return HasColorLabel("#E74C3C")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelPink As Boolean
            Get
                Return HasColorLabel("#F03B88")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelPurple As Boolean
            Get
                Return HasColorLabel("#8B5CF6")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelBlue As Boolean
            Get
                Return HasColorLabel("#3B82F6")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelCyan As Boolean
            Get
                Return HasColorLabel("#0891B2")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelTeal As Boolean
            Get
                Return HasColorLabel("#0F766E")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelGreen As Boolean
            Get
                Return HasColorLabel("#22C55E")
            End Get
        End Property

        Public ReadOnly Property IsColorLabelYellow As Boolean
            Get
                Return HasColorLabel("#FACC15")
            End Get
        End Property

        ''' <summary>Die Marken setzen, wie der Katalog sie gerade fuehrt. Eine leere Flaeche hat
        ''' keine.</summary>
        Friend Sub SetMarkers(rating As Integer, isFavorite As Boolean, colorLabel As String, pickState As Integer)
            _rating = rating
            _isFavorite = isFavorite
            _colorLabel = If(colorLabel, "")
            _pickState = pickState
            For Each name In {NameOf(Me.Rating), NameOf(Me.IsFavorite), NameOf(Me.ColorLabel),
                              NameOf(IsPicked), NameOf(IsRejected),
                              NameOf(IsColorLabelOrange), NameOf(IsColorLabelRed), NameOf(IsColorLabelPink),
                              NameOf(IsColorLabelPurple), NameOf(IsColorLabelBlue), NameOf(IsColorLabelCyan),
                              NameOf(IsColorLabelTeal), NameOf(IsColorLabelGreen), NameOf(IsColorLabelYellow)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ' ── Fokusfelder und Fokus-Peaking ───────────────────────────────────────

        ''' <summary>Die Fokusfelder der Flaeche in Anteilen ihres Bildes, leer bei ausgeschaltetem
        ''' Schalter und wenn die Aufnahme keine Lage hergibt.</summary>
        Public ReadOnly Property FocusFrames As New ObservableCollection(Of FocusFrame)()

        Private _focusPointStatus As FocusPointStatus = FocusPointStatus.Off

        ''' <summary>Was zu den Fokusfeldern dieser Flaeche zu sagen ist.</summary>
        Public Property FocusPointDisplayStatus As FocusPointStatus
            Get
                Return _focusPointStatus
            End Get
            Friend Set(value As FocusPointStatus)
                Me.RaiseAndSetIfChanged(_focusPointStatus, value)
            End Set
        End Property

        ''' <summary>Zaehler gegen ueberholte Lesungen der Fokusfelder und gegen ein ueberholtes
        ''' Peaking-Bild dieser Flaeche.</summary>
        Friend FocusToken As Integer
        Friend PeakingToken As Integer

        Friend Sub SetFocusFrames(frames As IEnumerable(Of FocusFrame), status As FocusPointStatus)
            FocusFrames.Clear()
            If frames IsNot Nothing Then
                For Each frame In frames
                    FocusFrames.Add(frame)
                Next
            End If
            FocusPointDisplayStatus = status
        End Sub

        Private _focusPeakingImage As Bitmap

        ''' <summary>Die Markierungsebene des Fokus-Peakings zum Bild der Flaeche, Nothing solange
        ''' aus oder in Arbeit.</summary>
        Public ReadOnly Property FocusPeakingImage As Bitmap
            Get
                Return _focusPeakingImage
            End Get
        End Property

        Friend Sub SetFocusPeakingImage(value As Bitmap)
            Dim previous = _focusPeakingImage
            If Object.ReferenceEquals(previous, value) Then Return
            _focusPeakingImage = value
            Me.RaisePropertyChanged(NameOf(FocusPeakingImage))
            ReleaseDeferred(previous)
        End Sub

        ' ── Inhalt verschieben und abraeumen ────────────────────────────────────

        ''' <summary>Den ganzen Inhalt mit einer anderen Flaeche tauschen, OHNE etwas freizugeben
        ''' oder neu zu laden: beim Tauschen und beim Nachruecken wandert ein Bild nur auf eine
        ''' andere Stelle. Ein zweiter Decode waere bei einer RAW Sekunden.</summary>
        Friend Sub SwapContentWith(other As ComparePaneViewModel)
            If other Is Nothing OrElse Object.ReferenceEquals(other, Me) Then Return
            Dim content = TakeContent()
            PutContent(other.TakeContent())
            other.PutContent(content)
        End Sub

        Private Structure PaneContent
            Public Path As String
            Public FileName As String
            Public Image As Bitmap
            Public LoadedEntry As String
            Public RawDeveloped As Boolean
            Public Tile As Bitmap
            Public Rating As Integer
            Public IsFavorite As Boolean
            Public ColorLabel As String
            Public PickState As Integer
            Public Frames As List(Of FocusFrame)
            Public Status As FocusPointStatus
            Public Peaking As Bitmap
        End Structure

        Private Function TakeContent() As PaneContent
            Return New PaneContent With {
                .Path = _path, .FileName = _fileName, .Image = _image, .LoadedEntry = _loadedEntry,
                .RawDeveloped = _rawDeveloped, .Tile = _tileImage,
                .Rating = _rating, .IsFavorite = _isFavorite, .ColorLabel = _colorLabel, .PickState = _pickState,
                .Frames = New List(Of FocusFrame)(FocusFrames), .Status = _focusPointStatus,
                .Peaking = _focusPeakingImage}
        End Function

        Private Sub PutContent(content As PaneContent)
            ' Laufende Lesungen und Rechnungen gehoerten zum alten Inhalt.
            FocusToken += 1
            PeakingToken += 1
            _path = content.Path
            _fileName = content.FileName
            _image = content.Image
            _loadedEntry = content.LoadedEntry
            _rawDeveloped = content.RawDeveloped
            _tileImage = content.Tile
            _focusPeakingImage = content.Peaking
            For Each name In {NameOf(Path), NameOf(FileName), NameOf(IsEmpty), NameOf(HasEntry),
                              NameOf(Image), NameOf(TileImage), NameOf(IsLoading), NameOf(FocusPeakingImage)}
                Me.RaisePropertyChanged(name)
            Next
            SetMarkers(content.Rating, content.IsFavorite, content.ColorLabel, content.PickState)
            SetFocusFrames(content.Frames, content.Status)
        End Sub

        ''' <summary>Die Flaeche leeren und ihr Bild freigeben.</summary>
        Friend Sub Clear()
            FocusToken += 1
            PeakingToken += 1
            SetEntry("", "")
            _tileImage = Nothing
            SetLoadedImage(Nothing, "", False)
            SetFocusPeakingImage(Nothing)
            SetMarkers(0, False, "", 0)
            SetFocusFrames(Nothing, FocusPointStatus.Off)
        End Sub

    End Class

End Namespace
