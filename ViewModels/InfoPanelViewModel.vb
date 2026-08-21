Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>
    ''' Der Zustand des Infopanels fuer EIN Bild oder fuer eine Auswahl.
    '''
    ''' Galerie, Betrachter und Editor haengen ALLE DREI hier: was die Leiste anzeigt, ist ueberall
    ''' dasselbe. Verschieden bleiben nur die LADEWEGE - der Betrachter wechselt dreistufig und holt
    ''' fuer Immich erst die Datei, der Editor rechnet sein Histogramm aus dem bearbeiteten Bild.
    ''' Wer seinen eigenen Weg mitbringt, stellt <see cref="OwnerLoadsDetails"/> und fuettert das
    ''' Panel ueber <see cref="SetOwnedPath"/>, <see cref="ApplyOwnedState"/> und
    ''' <see cref="ApplyOwnedTags"/>; die Galerie laedt selbst.
    '''
    ''' Geschrieben wird ueber den Katalog, genau wie im Betrachter. Das markierte Element bekommt
    ''' die Aenderung zusaetzlich direkt, sonst zeigte die Kachel daneben weiter den alten Stand.
    ''' </summary>
    Public Class InfoPanelViewModel
        Inherits ViewModelBase
        Implements IInfoSidebarPanel

        Private _item As ImageItem
        Private _path As String = ""
        Private _loadToken As Integer
        ' Bei Mehrfachauswahl stehen hier ALLE betroffenen Bilder. Bewertung, Herz, Etikett und
        ' Stichwoerter wirken dann auf jedes davon.
        Private _items As New List(Of ImageItem)()
        Private _isSummary As Boolean

        Private _exifInfo As ExifData = New ExifData()
        Private _histogramImage As Bitmap
        Private _rating As Integer
        Private _isFavorite As Boolean
        Private _colorLabel As String = ""
        Private _newTagText As String = ""
        Private _selectedTab As InfoSidebarTab = InfoSidebarTab.General
        Private _isVisible As Boolean
        Private _isOwnerViewActive As Boolean = True
        Private _placeholderText As String = ""

        ''' <summary>Wartezeit vor dem Histogramm. Lang genug, dass Durchblaettern und das Aufbauen
        ''' einer Mehrfachauswahl keinen einzigen Decode ausloesen, kurz genug, dass es beim
        ''' Stehenbleiben sofort da ist.</summary>
        Private Const HistogramDelayMs As Integer = 300

        Public Sub New()
            Tags = New ObservableCollection(Of String)()
            TagSuggestions = New ObservableCollection(Of String)()

            SetInfoTabCommand = ReactiveCommand.Create(Of String)(AddressOf SetInfoTab)
            SetRatingCommand = ReactiveCommand.Create(Of String)(
                Sub(value)
                    Dim number As Integer
                    If Integer.TryParse(value, number) Then Rating = If(_rating = number, 0, number)
                End Sub)
            ToggleFavoriteCommand = ReactiveCommand.Create(Sub() IsFavorite = Not IsFavorite)
            ' Dieselbe Farbe noch einmal nimmt das Etikett weg - wie im Kontextmenue der Galerie.
            SetColorLabelCommand = ReactiveCommand.Create(Of String)(
                Sub(hex) ColorLabel = If(String.Equals(_colorLabel, If(hex, ""), StringComparison.OrdinalIgnoreCase), "", If(hex, "")))
            AddTagCommand = ReactiveCommand.Create(AddressOf AddTag)
            RemoveTagCommand = ReactiveCommand.Create(Of String)(AddressOf RemoveTag)
            OpenTagSearchCommand = ReactiveCommand.Create(Of String)(Sub(tag) OpenTagSearch?.Invoke(tag))
        End Sub

        ''' <summary>Was beim Klick auf ein Stichwort geschehen soll. Die Galerie haengt sich hier
        ''' ein - das Panel selbst weiss nichts von Suchen.</summary>
        Public Property OpenTagSearch As Action(Of String)

        ''' <summary>Das Bild, dessen Daten das Panel zeigt. Nothing leert es.</summary>
        Public Sub ShowItem(item As ImageItem)
            Dim path = If(item Is Nothing, "", If(item.FilePath, ""))
            If Not _isSummary AndAlso String.Equals(path, _path, StringComparison.OrdinalIgnoreCase) AndAlso _item Is item Then Return

            ' Jeder Wechsel macht laufende Hintergrundarbeit ungueltig. Ohne diese Marke setzte ein
            ' spaet zurueckkommender Lauf die Daten des VORHERIGEN Bildes ins Panel - genau der
            ' Fehler, den der Betrachter beim schnellen Blaettern im Filmstreifen hatte.
            Dim token = ResetPanel()

            _isSummary = False
            _items.Clear()
            If item IsNot Nothing Then _items.Add(item)
            WatchItems()
            _item = item
            _path = path

            ' Erst MELDEN, wenn der Pfad steht: IsSingleImage liest ihn. Wurde vorher gemeldet,
            ' rechnete die Anzeige noch mit dem leeren Pfad und blieb ausgeblendet - nach einer
            ' Mehrfachauswahl war der ganze Bereich weg.
            RaiseStateChanged()

            ' Personen gehoeren HIERHER, in den Einzelbild-Pfad. Sie zuerst in ShowItems einzuhaengen
            ' war falsch: bei genau EINEM Bild leitet ShowItems sofort auf ShowItem um und der
            ' Sammelpfad laeuft nie - der Abschnitt blieb leer, obwohl die Bibliothek die Personen
            ' kannte. Und genau ein Bild ist der einzige Fall, in dem er ueberhaupt etwas zeigt.
            LoadPeople()
            LoadPlace()

            If String.IsNullOrEmpty(_path) Then Return

            ' Betrachter und Editor holen die Aufnahmedaten selbst (siehe OwnerLoadsDetails).
            If OwnerLoadsDetails Then Return

            ' Sofort ein Stand des RICHTIGEN Bildes, damit nie die Angaben des vorherigen stehen
            ' bleiben. Ein Immich-Asset steht in keinem lokalen Katalog - dort kommt das, was das
            ' Element selbst weiss, und der Rest nach dem Holen der Datei.
            ' Gilt fuer JEDE Serverquelle, nicht nur fuer Immich: ein Serverbild steht in keinem
            ' lokalen Katalog, ein Katalogblick liefert dort nichts.
            ExifInfo = If(item IsNot Nothing AndAlso item.IsRemoteAsset,
                          BuildFromItem(item),
                          ImageInfoService.BuildProvisionalFromCatalog(_path))
            Me.RaisePropertyChanged(NameOf(Name))
            LoadBaseData()
            LoadInBackground(token, _path, item)
        End Sub

        ''' <summary>Der Besitzer liefert Aufnahmedaten, Histogramm, Bewertung, Herz, Etikett und
        ''' Stichwoerter selbst; das Panel laedt dann nichts nach.
        '''
        ''' Betrachter und Editor stellen das ein, weil ihre LADEWEGE andere sind als der der
        ''' Galerie und es bleiben sollen: der Betrachter wechselt dreistufig (sofort ein
        ''' provisorischer Stand, dann die Aufnahmedaten, dann das Histogramm), holt fuer ein
        ''' Immich-Asset das Original in den Temp-Ordner und schreibt das Gelesene in den Katalog
        ''' zurueck; der Editor rechnet sein Histogramm aus dem BEARBEITETEN Bild und nicht aus der
        ''' Datei - sonst zeigte die Leiste die Tonwerte vor allen Reglern.
        '''
        ''' Gemeinsam bleibt alles, was die Leiste ANZEIGT: Reiter, Farbetikett, Personen, Ort und
        ''' der Zustand von Bewertung, Herz und Stichwoertern.</summary>
        Public Property OwnerLoadsDetails As Boolean

        ''' <summary>Die Wahl zwischen Histogramm, Waveform und RGB-Parade. Liegt hier, weil die
        ''' Leiste EIN Steuerelement fuer drei Ansichten ist - so lautet die Bindung ueberall
        ''' gleich (siehe <see cref="ScopeSelectionViewModel"/>).</summary>
        Public ReadOnly Property Scope As ScopeSelectionViewModel =
            New ScopeSelectionViewModel(Sub() OnScopeModeChanged())

        ''' <summary>Wie der BESITZER sein Analysebild neu rechnet. Betrachter und Editor tragen
        ''' das ein, weil ihre Quellen andere sind: der Editor nimmt das bearbeitete Bild, der
        ''' Betrachter seine Temp-Kopie. Nicht gesetzt heisst: die Galerie, und die laedt selbst
        ''' nach.</summary>
        Public Property ScopeRefresh As Action

        Private Sub OnScopeModeChanged()
            ' Das alte Bild sofort weg: es zeigt die vorherige Darstellung und waere bis zum
            ' Eintreffen des neuen eine Falschaussage. Das gilt auch fuer die Ansichten, die gerade
            ' nicht auf dem Schirm stehen - dort bleibt es dann leer, bis sie wieder dran sind.
            ScopeImage = Nothing
            ' Die Wahl gilt anwendungsweit, das Ereignis erreicht also alle drei Ansichten. Rechnen
            ' darf nur die, die jemand vor sich hat; die anderen holen es beim Wechsel nach.
            If Not IsPanelLive Then Return
            If OwnerLoadsDetails Then
                ScopeRefresh?.Invoke()
            Else
                Refresh()
            End If
        End Sub

        ''' <summary>Sagt dem Panel, welches Bild gerade gilt, ohne seine Ladewege anzustossen.
        '''
        ''' Der Betrachter wechselt den Pfad frueher, als er das Panel fuettert (erst kommt das Bild
        ''' auf die Buehne, dann laufen die Aufnahmedaten nach). Ohne diesen Weg haetten der
        ''' Histogramm-Block und die Reiterleiste in der Zwischenzeit noch am vorherigen Bild
        ''' gehangen - beim Wechsel von einem Bild auf ein VIDEO waere der Block sichtbar
        ''' geblieben.</summary>
        Public Sub SetOwnedPath(path As String)
            Dim wanted = If(path, "")
            If String.Equals(_path, wanted, StringComparison.OrdinalIgnoreCase) Then Return
            _path = wanted
            RaiseStateChanged()
        End Sub

        ''' <summary>Bewertung, Herz und Etikett setzen, OHNE sie zurueckzuschreiben. Fuer den
        ''' Besitzer beim Bildwechsel: er hat die Werte gerade erst gelesen, und der Weg ueber die
        ''' Eigenschaften loeste sofort ein Speichern desselben Wertes aus - beim Blaettern also je
        ''' Bild ein Schreibvorgang in Katalog oder Server.</summary>
        Public Sub ApplyOwnedState(rating As Integer, favorite As Boolean, colorLabel As String)
            ApplyRatingState(rating, favorite, colorLabel)
        End Sub

        ''' <summary>Die Stichwoerter anzeigen, ohne sie zu schreiben. Gegenstueck zu
        ''' <see cref="ApplyOwnedState"/> fuer den Besitzer-Betrieb.</summary>
        ''' NICHT "tags" als Parametername: VB unterscheidet keine Gross- und Kleinschreibung, der
        ''' Parameter verdeckte die Eigenschaft Tags und Tags.Clear() leerte die Eingabe.
        Public Sub ApplyOwnedTags(loadedTags As IEnumerable(Of String))
            Dim wanted = If(loadedTags, Enumerable.Empty(Of String)()).ToList()
            Tags.Clear()
            For Each tag In wanted
                Tags.Add(tag)
            Next
            RefreshTagSuggestions()
        End Sub

        ''' <summary>Was das Element selbst ueber sich weiss - Name, Masse, Groesse. Fuer ein
        ''' Immich-Asset ist das alles, was ohne Herunterladen zu haben ist.</summary>
        Private Shared Function BuildFromItem(item As ImageItem) As ExifData
            Dim name = If(item.ImmichOriginalFileName, "")
            If String.IsNullOrEmpty(name) Then name = If(item.FileName, "")
            Dim data As New ExifData With {
                .FileName = name,
                .FileType = IO.Path.GetExtension(name).TrimStart("."c).ToUpperInvariant()
            }
            If item.ImageWidth > 0 AndAlso item.ImageHeight > 0 Then
                data.ImageWidth = item.ImageWidth.ToString(CultureInfo.InvariantCulture)
                data.ImageHeight = item.ImageHeight.ToString(CultureInfo.InvariantCulture)
                data.AspectRatio = ImageInfoService.FormatAspectRatio(item.ImageWidth, item.ImageHeight)
                data.Megapixels = (item.ImageWidth * item.ImageHeight / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) & " MP"
            End If
            If item.FileSize > 0 Then data.FileSize = FormatSize(item.FileSize)
            Return data
        End Function

        ''' <summary>Mehrere Bilder auf einmal.
        '''
        ''' Gezeigt wird, was fuer ALLE gilt: eine kurze Uebersicht, dazu Bewertung, Herz und
        ''' Etikett nur dann gesetzt, wenn alle denselben Wert tragen, und von den Stichwoertern die
        ''' Schnittmenge. Eine Aenderung wirkt auf jedes markierte Bild.
        '''
        ''' Aufnahmedaten und Histogramm entfallen: sie beschreiben genau ein Bild, und eines von
        ''' zwoelf zu zeigen waere schlicht falsch.</summary>
        Public Sub ShowItems(items As IList(Of ImageItem))
            Dim images = If(items, New List(Of ImageItem)()).
                         Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder AndAlso Not i.IsParentFolderEntry).
                         ToList()
            If images.Count <= 1 Then
                ShowItem(images.FirstOrDefault())
                Return
            End If

            ResetPanel()
            _isSummary = True
            _items = images
            WatchItems()
            RaiseStateChanged()

            LoadSummaryBaseData()
            ' Der teure Teil - Stichwoerter und Uebersicht - nur bei offener Leiste. Zugeklappt
            ' sieht das niemand, und ShowItems laeuft bei JEDEM Auswahlwechsel der Galerie.
            ' Beim Aufklappen holt Refresh es nach.
            If IsPanelLive Then
                LoadSummaryTags()
                BuildSummary()
            End If
        End Sub

        ''' <summary>Alles zurueck auf Anfang und die laufende Hintergrundarbeit ungueltig machen.
        '''
        ''' Bei JEDEM Wechsel der Auswahl, nicht nur bei manchen: einzelne Reste stehen zu lassen
        ''' hat schon zweimal Angaben des vorherigen Standes gezeigt - erst das Histogramm, dann
        ''' eine allein stehende Reiter-Ueberschrift. Ein Zustand, der immer gleich beginnt, kann
        ''' das nicht.</summary>
        Private Function ResetPanel() As Integer
            Dim token = Interlocked.Increment(_loadToken)
            _isSummary = False
            _items = New List(Of ImageItem)()
            WatchItems()
            _item = Nothing
            _path = ""
            ' DER REITER BLEIBT STEHEN. Wer sich die EXIF-Daten ansieht und weiterblaettert, will
            ' die EXIF-Daten des naechsten Bildes sehen - und nicht bei jedem Klick wieder auf
            ' "Allgemein" landen. Zurueck faellt die Auswahl nur, wenn es den Reiter fuer das neue
            ' Bild gar nicht gibt (siehe RaisePeopleTabState).
            ScopeImage = Nothing
            ExifInfo = New ExifData()
            ' Auch der Ort - sonst steht bei einer Mehrfachauswahl der des zuletzt einzeln
            ' markierten Bildes weiter da, und der gilt dann fuer keines der markierten.
            _placeText = ""
            Tags.Clear()
            SummaryFacts.Clear()
            ApplyRatingState(0, False, "")
            Return token
        End Function

        ''' <summary>Alles melden, was von Auswahl und Betriebsart abhaengt.</summary>
        Private Sub RaiseStateChanged()
            For Each propertyName In {NameOf(IsSummary), NameOf(IsSingleImage), NameOf(HasScope), NameOf(HasInfoContent),
                                      NameOf(Name), NameOf(IsInfoTabGeneral), NameOf(IsInfoTabExif),
                                      NameOf(IsInfoTabIptc), NameOf(IsInfoTabXmp), NameOf(IsInfoTabIcc),
                                      NameOf(IsInfoTabPeople), NameOf(PlaceText), NameOf(HasPlace)}
                Me.RaisePropertyChanged(propertyName)
            Next
        End Sub

        ''' <summary>Zeigt das Panel mehrere Bilder auf einmal?</summary>
        Public ReadOnly Property IsSummary As Boolean
            Get
                Return _isSummary
            End Get
        End Property

        ''' <summary>Genau ein Bild - dann gibt es Aufnahmedaten und Histogramm.</summary>
        Public ReadOnly Property IsSingleImage As Boolean
            Get
                Return Not _isSummary AndAlso Not String.IsNullOrEmpty(_path)
            End Get
        End Property

        ''' <summary>Ein Histogramm gibt es nur zu einem BILD. Ein Video hat keines: es wuerde einen
        ''' Standbild-Decode kosten, und ein leerer Kasten mit Ueberschrift sieht aus wie ein Fehler.
        ''' Deshalb entfaellt fuer Videos beides - das Rechnen und das Anzeigen.</summary>
        Public ReadOnly Property HasScope As Boolean
            Get
                Return ScopeSelectionViewModel.ShowInInfoSidebar AndAlso
                       IsSingleImage AndAlso Not VideoPreviewService.IsSupportedVideo(_path)
            End Get
        End Property

        ''' <summary>Ein paar Zahlen zur Auswahl, im selben Namen/Wert-Format wie die EXIF-Zeilen.</summary>
        Public ReadOnly Property SummaryFacts As New ObservableCollection(Of ExifTag)()

        ''' <summary>Bewertung, Herz und Etikett der Auswahl. Kostet nichts - die Werte stehen an den
        ''' Elementen selbst, es wird keine Datenbank gefragt.</summary>
        Private Sub LoadSummaryBaseData()
            Dim ratings = _items.Select(Function(i) i.Rating).Distinct().ToList()
            Dim favorites = _items.Select(Function(i) i.IsFavorite).Distinct().ToList()
            Dim labels = _items.Select(Function(i) If(i.ColorLabel, "")).Distinct().ToList()
            ApplyRatingState(If(ratings.Count = 1, ratings(0), 0),
                                favorites.Count = 1 AndAlso favorites(0),
                                If(labels.Count = 1, labels(0), ""))
        End Sub

        ''' <summary>Bewertung, Herz und Etikett aus den angezeigten Elementen NEU einlesen.
        '''
        ''' Es wird NICHTS geschrieben: die Werte kommen von den Elementen selbst.</summary>
        Public Sub ReloadRatingStateFromItems()
            If _items Is Nothing OrElse _items.Count = 0 Then Return
            LoadSummaryBaseData()
        End Sub

        ''' <summary>Die Elemente, an deren Meldungen das Panel gerade haengt.</summary>
        Private ReadOnly _watchedItems As New List(Of ImageItem)()

        ''' <summary>Laeuft gerade ein Schreibvorgang des Panels auf seine eigenen Elemente?
        ''' Dann ist die Auswahl waehrenddessen halb geschrieben, und ein Neueinlesen daraus
        ''' ergaebe einen Zustand, den niemand gewaehlt hat.</summary>
        Private _isWritingItems As Boolean

        ''' <summary>Das Panel hoert seinen Elementen ZU, statt auf Anstoesse von aussen zu warten.
        '''
        ''' Bewertung, Herz und Etikett lassen sich an vielen Stellen aendern: Sternemenue der
        ''' Fusszeile, Kontextmenue, Klick auf die Kachel, Ruecknahme nach einem abgelehnten
        ''' Immich-Schreibvorgang. Jeden dieser Wege daran zu erinnern, das Panel anzustossen, ist
        ''' eine Liste, die jemand vergisst - genau das ist beim Herz auf der Kachel passiert
        ''' (Nutzerbefund 2026-08-06, nachdem die uebrigen Wege schon versorgt waren).
        '''
        ''' Die Elemente melden ihre Aenderung ohnehin, sonst zeigte die Kachel sie nicht. Wer
        ''' kuenftig einen neuen Weg baut, ist damit bauartbedingt versorgt.</summary>
        Private Sub WatchItems()
            For Each previous In _watchedItems
                RemoveHandler previous.PropertyChanged, AddressOf OnWatchedItemChanged
            Next
            _watchedItems.Clear()
            For Each current In _items
                If current Is Nothing Then Continue For
                AddHandler current.PropertyChanged, AddressOf OnWatchedItemChanged
                _watchedItems.Add(current)
            Next
        End Sub

        Private Sub OnWatchedItemChanged(sender As Object, e As ComponentModel.PropertyChangedEventArgs)
            ' Der eigene Schreibvorgang zaehlt nicht als Aenderung "von aussen": das Panel kennt
            ' den Wert bereits und meldet ihn selbst, sobald alle Elemente ihn tragen.
            If _isWritingItems Then Return
            Select Case e.PropertyName
                Case NameOf(ImageItem.Rating), NameOf(ImageItem.IsFavorite), NameOf(ImageItem.ColorLabel)
                    ReloadRatingStateFromItems()
                Case NameOf(ImageItem.PlaceCity), NameOf(ImageItem.PlaceCountry)
                    ' Bei einem Serverbild kommen die Aufnahmedaten erst mit dem Detail-Abruf, und
                    ' der haengt am Sichtbereich - der Ort trifft also womoeglich erst ein, wenn die
                    ' Leiste das Bild schon zeigt.
                    LoadPlace()
            End Select
        End Sub

        ''' <summary>Die gemeinsamen Stichwoerter der Auswahl - der TEURE Teil der Uebersicht.
        '''
        ''' Gefragt wird in EINER Abfrage ueber alle Pfade, nicht je Bild einzeln. Vorher oeffnete
        ''' jedes markierte Bild seine eigene Verbindung; bei Strg+A auf einem grossen Ordner stand
        ''' die Oberflaeche. Aus demselben Grund laeuft das hier nur bei OFFENER Leiste - genau die
        ''' Zusage, die fuer den Hintergrundteil schon galt.</summary>
        ''' <summary>Die Personen auf dem markierten Bild. Nur bei GENAU EINEM Bild - bei mehreren
        ''' waere jede Angabe eine Behauptung ueber alle, und anders als bei Stichwoertern ist die
        ''' Schnittmenge hier fast immer leer (selten stehen dieselben Menschen auf allen Bildern
        ''' einer Auswahl).
        '''
        ''' Auch UNBENANNTE Gruppen kommen mit: sie sind der Normalfall direkt nach einem Durchlauf,
        ''' und genau hier soll man ihnen einen Namen geben koennen.</summary>
        Public ReadOnly Property People As New ObservableCollection(Of PersonFaceEntry)() Implements IInfoSidebarPanel.People

        ''' <summary>Die schon vergebenen Namen fuer die Vorschlagsliste am Namensfeld. Denselben
        ''' Namen ein zweites Mal zu tippen macht aus einer Person zwei.</summary>
        Public ReadOnly Property PersonNameSuggestions As New ObservableCollection(Of String)() Implements IInfoSidebarPanel.PersonNameSuggestions

        ''' <summary>Zeigt das Panel den Personen-Abschnitt? Nur mit eingeschalteter Erkennung, bei
        ''' genau einem Bild und wenn ueberhaupt jemand darauf erkannt wurde. Ein leerer Abschnitt
        ''' unter jedem Landschaftsfoto waere nur Platzverbrauch.</summary>
        Public ReadOnly Property HasPeople As Boolean Implements IInfoSidebarPanel.HasPeople
            Get
                Return People.Count > 0
            End Get
        End Property

        ''' <summary>Die Gesichter des Bildes, JE MIT AUSSCHNITT.
        '''
        ''' Der Ausschnitt ist der ganze Punkt: fuenf leere Namensfelder untereinander sagen
        ''' niemandem, welches zu welchem Gesicht gehoert. Mit dem Gesicht daneben ist es
        ''' offensichtlich - so machen es alle, die das anbieten.
        '''
        ''' Gebaut wird im FacePanelService, damit Betrachter und Editor dieselben Zeilen bekommen
        ''' und nicht jeder seinen eigenen Zuschnitt nachbaut.</summary>
        Private Sub LoadPeople()
            People.Clear()
            If _items.Count <> 1 Then
                RaisePeopleTabState()
                Return
            End If
            Dim path = _items(0)?.FilePath
            Dim entries = FacePanelService.BuildEntries(path)
            FacePanelService.FillNameSuggestions(PersonNameSuggestions)
            For Each entry In entries
                People.Add(entry)
            Next
            RaisePeopleTabState()

            ' Die Ausschnitte kommen nach. Der Vergleich gegen die Marke haelt den Nachschub am
            ' richtigen Bild fest - beim schnellen Blaettern faellt er sonst ins naechste.
            Dim token = _loadToken
            FacePanelService.LoadThumbnails(entries, path, Function() token = _loadToken)
        End Sub

        ''' <summary>Meldet den Personen-Abschnitt UND seinen Reiter.
        '''
        ''' Steht der Reiter gerade offen und das naechste Bild zeigt niemanden, waere er weg und
        ''' sein Inhalt auch - die Leiste bliebe leer zurueck. Deshalb faellt die Auswahl dann auf
        ''' "Allgemein".</summary>
        Private Sub RaisePeopleTabState()
            ' Nur bei genau EINEM Bild entscheiden. Im Sammelmodus ist die Reiterleiste ohnehin weg,
            ' und der gemerkte Reiter soll die Rueckkehr zu einem Einzelbild ueberleben.
            If Not _isSummary AndAlso _items.Count = 1 AndAlso People.Count = 0 AndAlso
               _selectedTab = InfoSidebarTab.People Then
                SelectedInfoTab = InfoSidebarTab.General
            End If
            Me.RaisePropertyChanged(NameOf(HasPeople))
            Me.RaisePropertyChanged(NameOf(IsInfoTabPeople))
        End Sub

        ''' <summary>Gibt einer Gruppe ihren Namen. Traegt schon jemand denselben, verschmelzen beide
        ''' - das entscheidet die Bibliothek, nicht das Panel (LibraryService.NamePerson).
        '''
        ''' Der Name gilt fuer die GANZE Gruppe, also fuer jedes Bild darin. Das ist gewollt: genau
        ''' dafuer wird gruppiert. Wer nur DIESES Gesicht meint, weil es falsch zugeordnet ist,
        ''' nimmt <see cref="DetachFace"/>.</summary>
        Public Sub RenamePerson(personId As String, newName As String, faceId As String) Implements IInfoSidebarPanel.RenamePerson
            If String.IsNullOrWhiteSpace(personId) Then Return
            Try
                ' NUR bei einer echten Aenderung neu aufbauen. Der Neuaufbau wirft die Gesichtszeilen
                ' weg und legt sie neu an - stand dabei eine Vorschlagsliste offen, verschwand sie
                ' mitsamt ihrem Feld, bevor der Klick ankam.
                ' NUR bei einer echten Aenderung neu aufbauen. Der Neuaufbau wirft die Gesichtszeilen
                ' weg und legt sie neu an - stand dabei eine Vorschlagsliste offen, verschwand sie
                ' mitsamt ihrem Feld, bevor der Klick ankam.
                If LibraryService.Instance.ApplyPersonName(personId, newName, faceId) <>
                   LibraryService.PersonNameOutcome.Unchanged Then LoadPeople()
            Catch ex As Exception
                DiagnosticLogService.LogException("InfoPanel.RenamePerson", ex)
            End Try
        End Sub

        Private _placeText As String = ""

        ''' <summary>Der Aufnahmeort, etwa "Norden, Deutschland".
        '''
        ''' Bei einem lokalen Bild kommt er aus dem Katalog und nicht aus der Datei: die Koordinaten
        ''' stehen zwar im Bild, der NAME dazu aber nirgends - den schlaegt die Ortstabelle nach, und
        ''' das Ergebnis liegt im Katalog. Bei einem Immich-Asset hat der Server ihn schon benannt
        ''' und schickt ihn mit; dort haengt er am Element (siehe PlacePanelService).</summary>
        Public ReadOnly Property PlaceText As String Implements IInfoSidebarPanel.PlaceText
            Get
                Return _placeText
            End Get
        End Property

        Public ReadOnly Property HasPlace As Boolean Implements IInfoSidebarPanel.HasPlace
            Get
                Return Not String.IsNullOrEmpty(_placeText)
            End Get
        End Property

        ''' <summary>Den Ort noch einmal lesen, waehrend dasselbe Bild angezeigt bleibt.
        '''
        ''' Gebraucht, sobald der Aufnahmeort geaendert wurde: ShowItem steigt bei unveraendertem
        ''' Pfad sofort wieder aus, die Ortszeile stuende sonst weiter auf dem alten Stand - in der
        ''' Galerie genauso wie in Betrachter und Editor.</summary>
        Public Sub RefreshPlace()
            LoadPlace()
        End Sub

        Private Sub LoadPlace()
            ' Ueber das ELEMENT, nicht ueber den Pfad: ein Immich-Asset traegt seinen Ort selbst,
            ' ein lokales Bild holt ihn aus dem Katalog. Der Pfad allein reichte nur fuer das eine
            ' von beiden, und bei einem Serverbild blieb die Zeile deshalb leer.
            _placeText = PlacePanelService.TextFor(If(_items.Count = 1, _items(0), Nothing))
            Me.RaisePropertyChanged(NameOf(PlaceText))
            Me.RaisePropertyChanged(NameOf(HasPlace))
        End Sub

        ''' <summary>Hebt die Zuordnung EINES Gesichts auf: es bekommt eine eigene, namenlose Gruppe.
        '''
        ''' Fuer den Fall, dass die Erkennung jemanden verwechselt hat. Andere Bilder derselben
        ''' Person bleiben unberuehrt - geaendert wird nur dieses eine Gesicht. Danach steht das Feld
        ''' wieder leer, und der richtige Name traegt es in die richtige Gruppe (heisst schon jemand
        ''' so, verschmilzt die Bibliothek beides).</summary>
        Public Sub DetachFace(faceId As String) Implements IInfoSidebarPanel.DetachFace
            If String.IsNullOrWhiteSpace(faceId) Then Return
            Try
                LibraryService.Instance.DetachFace(faceId)
                LoadPeople()
            Catch ex As Exception
                DiagnosticLogService.LogException("InfoPanel.DetachFace", ex)
            End Try
        End Sub

        Private Sub LoadSummaryTags()
            Tags.Clear()
            If _items.Count = 0 Then Return

            Dim byPath As Dictionary(Of String, LibraryImageMeta) = Nothing
            Try
                byPath = LibraryService.Instance.GetMetaForPaths(
                    _items.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath)).
                           Select(Function(i) i.FilePath))
            Catch ex As Exception
                DiagnosticLogService.LogException("InfoPanel.LoadSummaryTags", ex)
            End Try

            ' Nur die Stichwoerter, die JEDES Bild traegt. Eines, das nur auf der Haelfte steht,
            ' waere hier eine Behauptung ueber die ganze Auswahl.
            Dim common As List(Of String) = Nothing
            For Each entry In _items
                Dim own As List(Of String) = Nothing
                Dim meta As LibraryImageMeta = Nothing
                If byPath IsNot Nothing AndAlso Not String.IsNullOrEmpty(entry?.FilePath) AndAlso
                   byPath.TryGetValue(entry.FilePath, meta) Then own = meta.Tags
                If own Is Nothing OrElse own.Count = 0 Then own = If(entry?.Tags?.ToList(), New List(Of String)())
                If common Is Nothing Then
                    common = own.ToList()
                Else
                    common = common.Where(Function(t) own.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
                End If
                If common.Count = 0 Then Exit For
            Next

            For Each tag In If(common, New List(Of String)())
                Tags.Add(tag)
            Next
            RefreshTagSuggestions()
        End Sub

        ''' <summary>Die Stichwoerter eines Bildes.
        '''
        ''' Ueber GetTags, nicht ueber die Metadaten-Zeile: die traegt Kamera, Bewertung und Masse,
        ''' die Stichwoerter stehen in einer eigenen Tabelle. Genau daran blieb das Feld in der
        ''' Galerie leer, waehrend es im Betrachter gefuellt war - der fragt schon immer GetTags.</summary>
        Private Function TagsOf(entry As ImageItem) As List(Of String)
            If entry Is Nothing OrElse String.IsNullOrEmpty(entry.FilePath) Then Return New List(Of String)()
            Try
                Dim fromCatalog = LibraryService.Instance.GetTags(entry.FilePath)
                If fromCatalog IsNot Nothing AndAlso fromCatalog.Count > 0 Then Return fromCatalog.ToList()
            Catch ex As Exception
                DiagnosticLogService.LogException("InfoPanel.LoadTags", ex)
            End Try
            If entry.Tags IsNot Nothing Then Return entry.Tags.ToList()
            Return New List(Of String)()
        End Function

        Private Sub BuildSummary()
            SummaryFacts.Clear()
            SummaryFacts.Add(New ExifTag(LocalizationService.T("Bilder"),
                                         _items.Count.ToString(CultureInfo.InvariantCulture)))

            Dim totalBytes As Long = 0
            For Each entry In _items
                If entry.FileSize > 0 Then totalBytes += entry.FileSize
            Next
            If totalBytes > 0 Then
                SummaryFacts.Add(New ExifTag(LocalizationService.T("Gesamtgröße"), FormatSize(totalBytes)))
            End If

            ' Die Kamera steht im Katalog, nicht auf der Kachel - ein Lookup fuer alle Pfade.
            Dim cameras As New List(Of String)()
            Try
                Dim metaByPath = LibraryService.Instance.GetMetaForPaths(Targets().Select(Function(i) i.FilePath).ToList())
                cameras = metaByPath.Values.Select(Function(m) If(m?.Camera, "")).
                                            Where(Function(c) Not String.IsNullOrEmpty(c)).
                                            Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            Catch ex As Exception
                DiagnosticLogService.LogException("InfoPanel.BuildSummary", ex)
            End Try
            If cameras.Count = 1 Then
                SummaryFacts.Add(New ExifTag(LocalizationService.T("Kamera"), cameras(0)))
            ElseIf cameras.Count > 1 Then
                SummaryFacts.Add(New ExifTag(LocalizationService.T("Kameras"),
                                             cameras.Count.ToString(CultureInfo.InvariantCulture)))
            End If

            Dim folders = Targets().Select(Function(i) i.FilePath).ToList().Select(Function(p) IO.Path.GetDirectoryName(p)).
                                  Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If folders.Count > 1 Then
                SummaryFacts.Add(New ExifTag(LocalizationService.T("Ordner"),
                                             folders.Count.ToString(CultureInfo.InvariantCulture)))
            End If
        End Sub

        Private Shared Function FormatSize(bytes As Long) As String
            Dim units = {"B", "KB", "MB", "GB", "TB"}
            Dim size As Double = bytes
            Dim step_ = 0
            While size >= 1024 AndAlso step_ < units.Length - 1
                size /= 1024
                step_ += 1
            End While
            Return size.ToString(If(step_ = 0, "0", "0.#"), CultureInfo.InvariantCulture) & " " & units(step_)
        End Function

        ''' <summary>Nach dem Einblenden nachladen: solange das Panel aus war oder seine Ansicht im
        ''' Hintergrund stand, ist kein Analysebild berechnet worden.</summary>
        Public Sub Refresh()
            If Not IsPanelLive Then Return
            If _isSummary Then
                LoadSummaryTags()
                BuildSummary()
                Return
            End If
            If OwnerLoadsDetails Then Return
            If String.IsNullOrEmpty(_path) Then Return
            LoadInBackground(Interlocked.Increment(_loadToken), _path, _item)
        End Sub

        Private Sub LoadBaseData()
            Dim rating = 0
            Dim favorite = False
            Dim label = ""
            ' NICHT "tags" als Name: VB unterscheidet keine Gross-/Kleinschreibung, die lokale
            ' Variable verdeckte die Eigenschaft Tags. Tags.Clear() leerte dann die eben gefuellte
            ' LISTE statt der Anzeige, und im Panel stand nie ein Stichwort.
            Dim loadedTags = TagsOf(_item)

            If _item IsNot Nothing Then
                rating = _item.Rating
                favorite = _item.IsFavorite
                label = If(_item.ColorLabel, "")
            End If

            ApplyRatingState(rating, favorite, label)
            Tags.Clear()
            For Each tag In loadedTags
                Tags.Add(tag)
            Next
            RefreshTagSuggestions()
        End Sub

        ''' <summary>Alles, was Arbeit kostet, laeuft NUR bei offener Info-Leiste in der Ansicht, die
        ''' gerade auf dem Schirm steht.
        '''
        ''' Sonst sieht niemand das Ergebnis: kein Lesen der Aufnahmedaten, kein Herunterladen
        ''' eines Immich-Originals in den Temp-Ordner, kein Analysebild. Beim Aufklappen und beim
        ''' Wechsel in die Ansicht holt <see cref="Refresh"/> alles nach.</summary>
        Private Sub LoadInBackground(token As Integer, path As String, item As ImageItem)
            If Not IsPanelLive Then Return

            Dim istServerbild = item IsNot Nothing AndAlso item.IsRemoteAsset
            Task.Run(Async Function()
                         ' Ein Serverbild hat keine lokale Datei. Aufnahmedaten und Histogramm
                         ' brauchen eine - der Betrachter holt sie sich deshalb in den Temp-Ordner,
                         ' und hier geschieht dasselbe. ABER erst nach der Wartezeit und nur bei
                         ' offenem Panel: sonst laedt jeder Klick im Filmstreifen ein Original vom
                         ' Server, und das Blaettern durch ein Album zieht die halbe Mediathek.
                         ' Welcher Server, entscheidet das Element selbst (EnsureLocalOriginalAsync);
                         ' hier steht bewusst kein Zweig je Quelle mehr.
                         If istServerbild Then
                             If Not IsPanelLive Then Return
                             ' Warten, ohne einen Faden des Vorrats festzuhalten. Thread.Sleep tat
                             ' genau das: bei jedem Auswahlwechsel startet hier ein Lauf, und beim
                             ' Blaettern durch einen Ordner lagen sofort dutzende davon schlafend im
                             ' Vorrat. Der Vorrat legt neue Faeden nur zoegernd nach - Kacheln,
                             ' Katalog und Gesichter warteten dann auf einen freien.
                             Await Task.Delay(HistogramDelayMs)
                             If token <> _loadToken OrElse _isSummary OrElse Not IsPanelLive Then Return

                             path = Await item.EnsureLocalOriginalAsync()
                             If token <> _loadToken OrElse String.IsNullOrEmpty(path) OrElse Not IO.File.Exists(path) Then Return
                         End If

                         Dim size = ImageProcessor.GetOrientedImageSize(path)
                         Dim info = ImageInfoService.BuildImageInfo(path, size.Width, size.Height)
                         Dispatcher.UIThread.Post(Sub()
                                                      If token <> _loadToken Then Return
                                                      ExifInfo = info
                                                      Me.RaisePropertyChanged(NameOf(Name))
                                                  End Sub)

                         If Not IsPanelLive Then Return

                         ' Erst warten, dann pruefen, DANN rechnen - in genau dieser Reihenfolge.
                         '
                         ' Das Histogramm kostet einen vollen Decode; bei einem RAW sind das
                         ' Sekunden. Wer mit den Pfeiltasten durch einen Ordner geht oder eine
                         ' Mehrfachauswahl aufbaut, loeste vorher fuer JEDES beruehrte Bild einen
                         ' Lauf aus, und alle liefen gleichzeitig weiter - der Rechner stand auf
                         ' Anschlag. Die Pruefung des Merkmals stand damals erst NACH dem Decode
                         ' und verwarf nur noch das Ergebnis.
                         ' Ein Video bekommt gar kein Histogramm - weder gerechnet noch gezeigt.
                         If VideoPreviewService.IsSupportedVideo(path) Then Return
                         ' Und wer das Analysebild aus der Leiste genommen hat, soll den Decode
                         ' auch nicht bezahlen. Galerie und Betrachter zeigen es nur dort.
                         If Not ScopeSelectionViewModel.ShowInInfoSidebar Then Return

                         Await Task.Delay(HistogramDelayMs)
                         If token <> _loadToken OrElse _isSummary OrElse Not IsPanelLive Then Return

                         ' Darstellung UND Platzierung koennen sich waehrend des Laufs aendern; der
                         ' Bildwechsel-Merker allein faengt beides nicht (siehe
                         ' ScopeSelectionViewModel.Generation). Wer die Leiste inzwischen
                         ' abgeschaltet hat, bekaeme sonst doch noch ein Bild - und mit ihm den
                         ' Speicher dafuer.
                         Dim scopeGeneration = ScopeSelectionViewModel.Generation
                         Dim histogram = ImageProcessor.BuildScopeImage(path, 600, 300)
                         Dispatcher.UIThread.Post(Sub()
                                                      If token <> _loadToken OrElse
                                                         scopeGeneration <> ScopeSelectionViewModel.Generation Then
                                                          histogram?.Dispose()
                                                          Return
                                                      End If
                                                      ScopeImage = histogram
                                                  End Sub)
                     End Function)
        End Sub

        Private Sub ApplyRatingState(rating As Integer, favorite As Boolean, label As String)
            _rating = rating
            _isFavorite = favorite
            _colorLabel = If(label, "")
            Me.RaisePropertyChanged(NameOf(Rating))
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            RaiseColorLabelChanged()
        End Sub

        ''' <summary>Ob die Leiste ausgeklappt ist. Das Analysebild entsteht nur dann - es kostet
        ''' einen vollen Decode und niemand sieht es, solange die Leiste eingeklappt ist.</summary>
        Public Property IsInfoSidebarVisible As Boolean
            Get
                Return _isVisible
            End Get
            Set(value As Boolean)
                If _isVisible = value Then Return
                Me.RaiseAndSetIfChanged(_isVisible, value)
                If value Then Refresh()
            End Set
        End Property

        ''' <summary>Ob die Ansicht, zu der dieses Panel gehoert, gerade auf dem Schirm ist.
        '''
        ''' Die Leiste kann ausgeklappt sein, ohne dass jemand sie sieht: Galerie, Betrachter und
        ''' Editor bestehen alle drei die ganze Sitzung ueber, und jeder merkt sich seinen eigenen
        ''' Ausklappzustand. Wer in der Galerie die Darstellung umschaltet, loeste damit auch im
        ''' Betrachter und im Editor einen Lauf aus - fuer eine Leiste, die niemand vor sich hat.
        ''' Der Ansichtswechsel holt nach, was in der Zwischenzeit ausgefallen ist.</summary>
        Public Property IsOwnerViewActive As Boolean
            Get
                Return _isOwnerViewActive
            End Get
            Set(value As Boolean)
                If _isOwnerViewActive = value Then Return
                _isOwnerViewActive = value
                If value Then Refresh()
            End Set
        End Property

        ''' <summary>Ob teure Arbeit ueberhaupt jemand zu sehen bekommt: die Leiste ist ausgeklappt
        ''' UND ihre Ansicht steht auf dem Schirm.</summary>
        Private ReadOnly Property IsPanelLive As Boolean
            Get
                Return _isVisible AndAlso _isOwnerViewActive
            End Get
        End Property

        Public ReadOnly Property Name As String
            Get
                If _isSummary Then Return String.Format(LocalizationService.T("{0} Bilder ausgewählt"), _items.Count)
                Return If(_exifInfo?.FileName, "")
            End Get
        End Property

        ''' <summary>Ob es etwas zu zeigen gibt. Ist nichts gesetzt, steht statt der Reiter und
        ''' Felder nur <see cref="InfoPlaceholderText"/> in der Mitte.</summary>
        Public ReadOnly Property HasInfoContent As Boolean
            Get
                Return _isSummary OrElse Not String.IsNullOrEmpty(_path)
            End Get
        End Property

        ''' <summary>Warum gerade nichts zu sehen ist. Der Besitzer setzt den Text, denn nur er
        ''' kennt den Grund - kein Bild, mehrere oder ein Ordner.</summary>
        Public Property InfoPlaceholderText As String
            Get
                Return _placeholderText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_placeholderText, If(value, ""))
            End Set
        End Property

        Public Property ExifInfo As ExifData
            Get
                Return _exifInfo
            End Get
            Set(value As ExifData)
                Me.RaiseAndSetIfChanged(_exifInfo, If(value, New ExifData()))
                ' Der angezeigte Name kommt aus den Aufnahmedaten. Im Besitzer-Betrieb setzt sie
                ' der Betrachter oder der Editor, und dann meldet ihn sonst niemand.
                Me.RaisePropertyChanged(NameOf(Name))
            End Set
        End Property

        Public Property ScopeImage As Bitmap
            Get
                Return _histogramImage
            End Get
            Set(value As Bitmap)
                Dim previous = _histogramImage
                Me.RaiseAndSetIfChanged(_histogramImage, value)
                ' Erst melden, dann freigeben: sonst zeichnet die Anzeige noch auf eine Bitmap,
                ' die es nicht mehr gibt.
                If previous IsNot Nothing AndAlso Not ReferenceEquals(previous, value) Then previous.Dispose()
            End Set
        End Property

        Public Property Rating As Integer
            Get
                Return _rating
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_rating, value)
                ' Den Wert am Element setzt der BESITZER, nicht das Panel: nur so kennt er den
                ' alten Stand und kann ihn zuruecknehmen, wenn der Immich-Server ablehnt.
                Dim affected = Targets()
                If affected.Count > 0 AndAlso PersistRating IsNot Nothing Then PersistRating.Invoke(affected, value)
            End Set
        End Property

        ''' <summary>Die betroffenen Bilder - eines oder alle markierten.</summary>
        Private Function Targets() As List(Of ImageItem)
            Return _items.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath)).ToList()
        End Function

        ''' <summary>Wie eine Aenderung dauerhaft wird. Der BESITZER stellt diese Wege, denn nur er
        ''' weiss, ob ein Bild lokal liegt oder auf einem Immich-Server; das Panel kennt nur
        ''' <see cref="ImageItem"/>. Die Galerie teilt darin auf wie in ihrem eigenen Sternemenue:
        ''' lokale Pfade gebuendelt in den Katalog, Immich-Elemente einzeln an den Server, mit
        ''' Ruecknahme, falls der ablehnt.</summary>
        Public Property PersistRating As Action(Of IList(Of ImageItem), Integer)
        Public Property PersistFavorite As Action(Of IList(Of ImageItem), Boolean)
        Public Property PersistColorLabel As Action(Of IList(Of ImageItem), String)

        ''' <summary>Ein einzelnes Stichwort setzen (True) oder entfernen (False).</summary>
        Public Property PersistTag As Action(Of IList(Of ImageItem), String, Boolean)

        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFavorite, value)
                Dim affected = Targets()
                If affected.Count > 0 AndAlso PersistFavorite IsNot Nothing Then PersistFavorite.Invoke(affected, value)
            End Set
        End Property

        Public Property ColorLabel As String
            Get
                Return _colorLabel
            End Get
            Set(value As String)
                Dim newLabel = If(value, "")
                _colorLabel = newLabel
                ' Aus der ORTSVARIABLEN schreiben und den Lauscher dabei stumm stellen. Beides
                ' zusammen, weil hier zwei Fallen liegen: das gesetzte Element meldet SOFORT, und
                ' das Neueinlesen sah die Auswahl dann halb geschrieben - ein Element rot, die
                ' uebrigen noch leer, also "gemischt", also leer. Der Rest der Schleife trug
                ' anschliessend genau dieses Leer weiter, und der Katalog bekam es auch (gemessen
                ' 2026-08-06: von drei markierten Bildern behielt nur das erste die Farbe).
                _isWritingItems = True
                Try
                    For Each entry In _items
                        entry.ColorLabel = newLabel
                    Next
                Finally
                    _isWritingItems = False
                End Try
                Dim affected = Targets()
                If affected.Count > 0 AndAlso PersistColorLabel IsNot Nothing Then PersistColorLabel.Invoke(affected, newLabel)
                RaiseColorLabelChanged()
            End Set
        End Property

        ' Die Anzeige fragt je Farbe einzeln - eine Meldung fuer alle waere hier kuerzer, aber die
        ' Bindungen heissen nun einmal so, und sie sind dieselben wie im Betrachter.
        Private Sub RaiseColorLabelChanged()
            Me.RaisePropertyChanged(NameOf(ColorLabel))
            ' NICHT "name" als Schleifenvariable: VB loest das auf die Eigenschaft Name auf.
            For Each propertyName In {NameOf(IsColorLabelOrange), NameOf(IsColorLabelRed), NameOf(IsColorLabelPink),
                                   NameOf(IsColorLabelPurple), NameOf(IsColorLabelBlue), NameOf(IsColorLabelCyan),
                                   NameOf(IsColorLabelTeal), NameOf(IsColorLabelGreen), NameOf(IsColorLabelYellow)}
                Me.RaisePropertyChanged(propertyName)
            Next
        End Sub

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

        Public ReadOnly Property Tags As ObservableCollection(Of String)
        Public ReadOnly Property TagSuggestions As ObservableCollection(Of String)

        Public Property NewTagText As String
            Get
                Return _newTagText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newTagText, If(value, ""))
            End Set
        End Property

        Private Sub AddTag()
            ' Die Schreibweise bleibt, wie sie getippt wurde: aus einer Beistelldatei kommen
            ' Stichwoerter ebenfalls mit Grossbuchstaben, und Immich behaelt sie auch. Verglichen
            ' wird dafuer ohne Ruecksicht darauf - sonst stuende "Berlin" zweimal da.
            Dim tag = NewTagText.Trim()
            If String.IsNullOrEmpty(tag) OrElse
               Tags.Any(Function(vorhanden) String.Equals(vorhanden, tag, StringComparison.OrdinalIgnoreCase)) Then Return
            Tags.Add(tag)
            NewTagText = ""
            WriteTag(tag, add:=True)
            RefreshTagSuggestions()
        End Sub

        Private Sub RemoveTag(tag As String)
            If Not Tags.Remove(tag) Then Return
            WriteTag(tag, add:=False)
        End Sub

        ''' <summary>Ein Stichwort hinzufuegen oder entfernen wirkt auf JEDES betroffene Bild.
        '''
        ''' Bei Mehrfachauswahl zeigt die Liste nur die gemeinsamen Stichwoerter. Deshalb wird hier
        ''' nicht die angezeigte Liste zurueckgeschrieben - das loeschte die eigenen Stichwoerter
        ''' jedes Bildes. Stattdessen wird das eine Wort gezielt hinzugefuegt oder entfernt.</summary>
        Private Sub WriteTag(tag As String, add As Boolean)
            If String.IsNullOrEmpty(tag) Then Return
            ' Nur die Bilder sammeln, bei denen sich wirklich etwas aendert - wer das Wort schon
            ' traegt (oder nicht traegt), braucht keinen Schreibvorgang und keinen Serveraufruf.
            Dim changed As New List(Of ImageItem)()
            For Each entry In _items
                If entry Is Nothing OrElse String.IsNullOrEmpty(entry.FilePath) Then Continue For
                Dim own = TagsOf(entry)
                Dim has = own.Contains(tag, StringComparer.OrdinalIgnoreCase)
                If add = has Then Continue For
                If add Then
                    own.Add(tag)
                Else
                    own.RemoveAll(Function(t) String.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                End If
                entry.Tags = own
                changed.Add(entry)
            Next
            If changed.Count > 0 AndAlso PersistTag IsNot Nothing Then PersistTag.Invoke(changed, tag, add)
        End Sub

        Private Sub RefreshTagSuggestions()
            TagSuggestions.Clear()
            For Each tag In LibraryService.Instance.GetAllTags()
                TagSuggestions.Add(tag)
            Next
        End Sub

        Public Property SelectedInfoTab As InfoSidebarTab
            Get
                Return _selectedTab
            End Get
            Set(value As InfoSidebarTab)
                Me.RaiseAndSetIfChanged(_selectedTab, value)
                For Each propertyName In {NameOf(IsInfoTabGeneral), NameOf(IsInfoTabPeople), NameOf(IsInfoTabExif),
                                       NameOf(IsInfoTabIptc), NameOf(IsInfoTabXmp), NameOf(IsInfoTabIcc)}
                    Me.RaisePropertyChanged(propertyName)
                Next
            End Set
        End Property

        Private Sub SetInfoTab(tabName As String)
            Select Case If(tabName, "").Trim().ToLowerInvariant()
                Case "people"
                    SelectedInfoTab = InfoSidebarTab.People
                Case "exif"
                    SelectedInfoTab = InfoSidebarTab.Exif
                Case "iptc"
                    SelectedInfoTab = InfoSidebarTab.Iptc
                Case "xmp"
                    SelectedInfoTab = InfoSidebarTab.Xmp
                Case "icc"
                    SelectedInfoTab = InfoSidebarTab.Icc
                Case Else
                    SelectedInfoTab = InfoSidebarTab.General
            End Select
        End Sub

        Public ReadOnly Property IsInfoTabGeneral As Boolean
            Get
                Return _selectedTab = InfoSidebarTab.General
            End Get
        End Property

        ''' <summary>Der Personen-Reiter gilt nur, solange auf dem Bild ueberhaupt jemand erkannt
        ''' wurde - sonst gibt es den Reiter nicht, und ein Inhalt ohne Reiter waere nicht mehr zu
        ''' verlassen.</summary>
        Public ReadOnly Property IsInfoTabPeople As Boolean
            Get
                Return _selectedTab = InfoSidebarTab.People AndAlso HasPeople
            End Get
        End Property

        ' Die vier Metadaten-Reiter gibt es im Sammelmodus nicht: sie beschreiben genau eine Datei.
        ' Ohne diese Sperre blieb bei mehreren Bildern die Ueberschrift des zuletzt gewaehlten
        ' Reiters allein stehen - und die Reiterleiste zum Zuruecksschalten ist dort ausgeblendet.
        Public ReadOnly Property IsInfoTabExif As Boolean
            Get
                Return Not _isSummary AndAlso _selectedTab = InfoSidebarTab.Exif
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIptc As Boolean
            Get
                Return Not _isSummary AndAlso _selectedTab = InfoSidebarTab.Iptc
            End Get
        End Property

        Public ReadOnly Property IsInfoTabXmp As Boolean
            Get
                Return Not _isSummary AndAlso _selectedTab = InfoSidebarTab.Xmp
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIcc As Boolean
            Get
                Return Not _isSummary AndAlso _selectedTab = InfoSidebarTab.Icc
            End Get
        End Property

        Public ReadOnly Property SetInfoTabCommand As ICommand
        Public ReadOnly Property SetRatingCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property SetColorLabelCommand As ICommand
        Public ReadOnly Property AddTagCommand As ICommand
        Public ReadOnly Property RemoveTagCommand As ICommand
        Public ReadOnly Property OpenTagSearchCommand As ICommand

    End Class

End Namespace
