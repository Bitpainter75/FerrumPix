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
    ''' Der Zustand des Infopanels fuer EIN Bild.
    '''
    ''' Warum eigenstaendig: dasselbe Panel haengt jetzt auch an der Galerie. Betrachter und Editor
    ''' tragen ihren Panel-Zustand noch selbst, weil dort Bildwechsel, Immich-Sitzung und Vergleich
    ''' mit hineinspielen. Die Galerie braucht davon nichts - sie zeigt schlicht das markierte Bild.
    '''
    ''' Geschrieben wird ueber den Katalog, genau wie im Betrachter. Das markierte Element bekommt
    ''' die Aenderung zusaetzlich direkt, sonst zeigte die Kachel daneben weiter den alten Stand.
    ''' </summary>
    Public Class InfoPanelViewModel
        Inherits ViewModelBase

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
            _item = item
            _path = path

            ' Erst MELDEN, wenn der Pfad steht: IsSingleImage liest ihn. Wurde vorher gemeldet,
            ' rechnete die Anzeige noch mit dem leeren Pfad und blieb ausgeblendet - nach einer
            ' Mehrfachauswahl war der ganze Bereich weg.
            RaiseStateChanged()
            If String.IsNullOrEmpty(_path) Then Return

            ' Sofort ein Stand des RICHTIGEN Bildes, damit nie die Angaben des vorherigen stehen
            ' bleiben. Ein Immich-Asset steht in keinem lokalen Katalog - dort kommt das, was das
            ' Element selbst weiss, und der Rest nach dem Holen der Datei.
            ExifInfo = If(item IsNot Nothing AndAlso item.IsImmichAsset,
                          BuildFromItem(item),
                          ImageInfoService.BuildProvisionalFromCatalog(_path))
            Me.RaisePropertyChanged(NameOf(Name))
            LoadBaseData()
            LoadInBackground(token, _path, item)
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
            RaiseStateChanged()

            LoadSummaryBaseData()
            BuildSummary()
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
            _item = Nothing
            _path = ""
            _selectedTab = InfoSidebarTab.General
            HistogramImage = Nothing
            ExifInfo = New ExifData()
            Tags.Clear()
            SummaryFacts.Clear()
            ApplyRatingState(0, False, "")
            Return token
        End Function

        ''' <summary>Alles melden, was von Auswahl und Betriebsart abhaengt.</summary>
        Private Sub RaiseStateChanged()
            For Each propertyName In {NameOf(IsSummary), NameOf(IsSingleImage), NameOf(HasInfoContent),
                                      NameOf(Name), NameOf(IsInfoTabGeneral), NameOf(IsInfoTabExif),
                                      NameOf(IsInfoTabIptc), NameOf(IsInfoTabXmp), NameOf(IsInfoTabIcc)}
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

        ''' <summary>Ein paar Zahlen zur Auswahl, im selben Namen/Wert-Format wie die EXIF-Zeilen.</summary>
        Public ReadOnly Property SummaryFacts As New ObservableCollection(Of ExifTag)()

        Private Sub LoadSummaryBaseData()
            Dim ratings = _items.Select(Function(i) i.Rating).Distinct().ToList()
            Dim favorites = _items.Select(Function(i) i.IsFavorite).Distinct().ToList()
            Dim labels = _items.Select(Function(i) If(i.ColorLabel, "")).Distinct().ToList()
            ApplyRatingState(If(ratings.Count = 1, ratings(0), 0),
                                favorites.Count = 1 AndAlso favorites(0),
                                If(labels.Count = 1, labels(0), ""))

            ' Nur die Stichwoerter, die JEDES Bild traegt. Eines, das nur auf der Haelfte steht,
            ' waere hier eine Behauptung ueber die ganze Auswahl.
            Dim common As List(Of String) = Nothing
            For Each entry In _items
                Dim own = TagsOf(entry)
                If common Is Nothing Then
                    common = own
                Else
                    common = common.Where(Function(t) own.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
                End If
                If common.Count = 0 Then Exit For
            Next

            Tags.Clear()
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
                Dim metaByPath = LibraryService.Instance.GetMetaForPaths(Paths())
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

            Dim folders = Paths().Select(Function(p) IO.Path.GetDirectoryName(p)).
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

        ''' <summary>Nach dem Einblenden nachladen: solange das Panel aus war, ist kein Histogramm
        ''' berechnet worden.</summary>
        Public Sub Refresh()
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

        ''' <summary>Alles, was Arbeit kostet, laeuft NUR bei offener Info-Leiste.
        '''
        ''' Ist sie zu, sieht niemand das Ergebnis: kein Lesen der Aufnahmedaten, kein Herunterladen
        ''' eines Immich-Originals in den Temp-Ordner, kein Histogramm. Beim Aufklappen holt
        ''' <see cref="Refresh"/> alles nach.</summary>
        Private Sub LoadInBackground(token As Integer, path As String, item As ImageItem)
            If Not _isVisible Then Return

            Dim istImmich = item IsNot Nothing AndAlso item.IsImmichAsset
            Task.Run(Async Function()
                         ' Ein Immich-Asset hat keine lokale Datei. Aufnahmedaten und Histogramm
                         ' brauchen eine - der Betrachter holt sie sich deshalb in den Temp-Ordner,
                         ' und hier geschieht dasselbe. ABER erst nach der Wartezeit und nur bei
                         ' offenem Panel: sonst laedt jeder Klick im Filmstreifen ein Original vom
                         ' Server, und das Blaettern durch ein Album zieht die halbe Mediathek.
                         If istImmich Then
                             If Not _isVisible Then Return
                             Thread.Sleep(HistogramDelayMs)
                             If token <> _loadToken OrElse _isSummary OrElse Not _isVisible Then Return

                             path = If(item.ImmichLocalPath, "")
                             If String.IsNullOrEmpty(path) OrElse Not IO.File.Exists(path) Then
                                 path = Await ImmichService.DownloadOriginalToTempAsync(item.ImmichAssetId,
                                                                                        item.ImmichOriginalFileName)
                                 If Not String.IsNullOrEmpty(path) Then item.ImmichLocalPath = path
                             End If
                             If token <> _loadToken OrElse String.IsNullOrEmpty(path) OrElse Not IO.File.Exists(path) Then Return
                         End If

                         Dim size = ImageProcessor.GetOrientedImageSize(path)
                         Dim info = ImageInfoService.BuildImageInfo(path, size.Width, size.Height)
                         Dispatcher.UIThread.Post(Sub()
                                                      If token <> _loadToken Then Return
                                                      ExifInfo = info
                                                      Me.RaisePropertyChanged(NameOf(Name))
                                                  End Sub)

                         If Not _isVisible Then Return

                         ' Erst warten, dann pruefen, DANN rechnen - in genau dieser Reihenfolge.
                         '
                         ' Das Histogramm kostet einen vollen Decode; bei einem RAW sind das
                         ' Sekunden. Wer mit den Pfeiltasten durch einen Ordner geht oder eine
                         ' Mehrfachauswahl aufbaut, loeste vorher fuer JEDES beruehrte Bild einen
                         ' Lauf aus, und alle liefen gleichzeitig weiter - der Rechner stand auf
                         ' Anschlag. Die Pruefung des Merkmals stand damals erst NACH dem Decode
                         ' und verwarf nur noch das Ergebnis.
                         Thread.Sleep(HistogramDelayMs)
                         If token <> _loadToken OrElse _isSummary OrElse Not _isVisible Then Return

                         Dim histogram = ImageProcessor.BuildHistogramImage(path, 240, 120)
                         Dispatcher.UIThread.Post(Sub()
                                                      If token <> _loadToken Then
                                                          histogram?.Dispose()
                                                          Return
                                                      End If
                                                      HistogramImage = histogram
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

        ''' <summary>Ob das Panel sichtbar ist. Das Histogramm entsteht nur dann - es kostet einen
        ''' vollen Decode und niemand sieht es, solange das Panel eingeklappt ist.</summary>
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
            End Set
        End Property

        Public Property HistogramImage As Bitmap
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
                For Each entry In _items
                    entry.Rating = value
                Next
                ' NICHT "paths" als Name: VB unterscheidet keine Gross-/Kleinschreibung und
                ' loest ihn auf die Funktion Paths auf.
                Dim filePaths = Paths()
                If filePaths.Count > 0 Then LibraryService.Instance.SetRatingForMany(filePaths, value, syncToXmp:=True)
            End Set
        End Property

        ''' <summary>Die Pfade der betroffenen Bilder - eines oder alle markierten.</summary>
        Private Function Paths() As List(Of String)
            Return _items.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath)).
                          Select(Function(i) i.FilePath).ToList()
        End Function

        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFavorite, value)
                For Each entry In _items
                    entry.IsFavorite = value
                Next
                For Each path In Paths()
                    LibraryService.Instance.SetFavorite(path, value)
                Next
            End Set
        End Property

        Public Property ColorLabel As String
            Get
                Return _colorLabel
            End Get
            Set(value As String)
                _colorLabel = If(value, "")
                For Each entry In _items
                    entry.ColorLabel = _colorLabel
                Next
                ' NICHT "paths" als Name: VB unterscheidet keine Gross-/Kleinschreibung und
                ' loest ihn auf die Funktion Paths auf.
                Dim filePaths = Paths()
                If filePaths.Count > 0 Then
                    LibraryService.Instance.SetColorLabelForMany(filePaths, _colorLabel, syncToXmp:=True)
                End If
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
                LibraryService.Instance.SetTags(entry.FilePath, own, syncToXmp:=True)
            Next
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
                For Each propertyName In {NameOf(IsInfoTabGeneral), NameOf(IsInfoTabExif), NameOf(IsInfoTabIptc),
                                       NameOf(IsInfoTabXmp), NameOf(IsInfoTabIcc)}
                    Me.RaisePropertyChanged(propertyName)
                Next
            End Set
        End Property

        Private Sub SetInfoTab(tabName As String)
            Select Case If(tabName, "").Trim().ToLowerInvariant()
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
