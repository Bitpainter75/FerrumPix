Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Windows.Input
Imports Avalonia.Media
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Stichwort als Marke in einer Spalte des Vergleichs.</summary>
    Public Class MetadataCompareChip
        Public Property Text As String = ""
        ''' <summary>Steht nur auf dieser Seite.</summary>
        Public Property OnlyHere As Boolean
        Public Property RemoveCommand As ICommand
        ''' <summary>Kopiert genau dieses Stichwort auf die andere Seite. Nur, wo es dort fehlt.</summary>
        Public Property CopyCommand As ICommand
        ''' <summary>Steht die Marke links (Datei)? Dann zeigt ihr Pfeil nach rechts.</summary>
        Public Property IsFileSide As Boolean

        Public ReadOnly Property CanCopy As Boolean
            Get
                Return OnlyHere AndAlso CopyCommand IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property CopyIconSource As String
            Get
                Return "avares://FerrumPix/Assets/Icons/phosphor/regular/" & If(IsFileSide, "arrow-square-right", "arrow-square-left") & ".svg"
            End Get
        End Property
        Public ReadOnly Property CopyTip As String
            Get
                Return If(IsFileSide, LocalizationService.T("In den Katalog kopieren"), LocalizationService.T("In die Datei kopieren"))
            End Get
        End Property
    End Class

    ''' <summary>Ein Stern der Bewertung: gefuellt oder leer.</summary>
    Public Class MetadataCompareStar
        Public Property IsOn As Boolean
        Public ReadOnly Property Source As String
            Get
                Return If(IsOn, "avares://FerrumPix/Assets/Icons/phosphor/fill/star-fill.svg",
                                "avares://FerrumPix/Assets/Icons/phosphor/regular/star.svg")
            End Get
        End Property
    End Class

    ''' <summary>Eine Zeile des Vergleichs: entweder eine Abschnittsueberschrift oder eine Angabe
    ''' mit Datei- und Katalogwert. Die Zeilen werden nach jeder Aktion neu gebaut, deshalb sind sie
    ''' einfache Werte ohne eigene Meldungen.</summary>
    Public Class MetadataCompareRow
        Public Property IsSection As Boolean
        ''' <summary>Symbol einer Abschnittsueberschrift.</summary>
        Public Property IconSource As String = ""
        Public Property Label As String = ""
        Public Property FileText As String = ""
        Public Property CatalogText As String = ""
        ''' <summary>Die Bewertung als fuenf Sterne statt als Text.</summary>
        Public Property IsRating As Boolean
        Public Property FileStars As New List(Of MetadataCompareStar)()
        Public Property CatalogStars As New List(Of MetadataCompareStar)()

        ''' <summary>Was in der Zelle steht: der Wert, oder ein ruhiger Platzhalter, wenn es keinen
        ''' gibt. Eine leere Zelle sah aus, als fehle etwas in der Anzeige.</summary>
        Public ReadOnly Property FileDisplay As String
            Get
                Return If(FileText.Length > 0, FileText, LocalizationService.T("Keine Angabe"))
            End Get
        End Property
        Public ReadOnly Property CatalogDisplay As String
            Get
                Return If(CatalogText.Length > 0, CatalogText, LocalizationService.T("Keine Angabe"))
            End Get
        End Property
        Public ReadOnly Property FileIsEmpty As Boolean
            Get
                Return FileText.Length = 0
            End Get
        End Property
        Public ReadOnly Property CatalogIsEmpty As Boolean
            Get
                Return CatalogText.Length = 0
            End Get
        End Property
        Public ReadOnly Property ShowFileKeywordPlaceholder As Boolean
            Get
                Return IsKeywords AndAlso FileChips.Count = 0
            End Get
        End Property
        Public ReadOnly Property ShowCatalogKeywordPlaceholder As Boolean
            Get
                Return IsKeywords AndAlso CatalogChips.Count = 0
            End Get
        End Property
        Public ReadOnly Property IsPlainText As Boolean
            Get
                Return Not IsSection AndAlso Not IsKeywords AndAlso Not IsRating
            End Get
        End Property
        ''' <summary>Die Pfeile stehen bei jeder kopierbaren Angabe; bei gleichen Werten sind sie
        ''' ausgegraut (<see cref="Differs"/>), damit man sieht, dass es den Weg gibt.</summary>
        Public ReadOnly Property ShowCopy As Boolean
            Get
                Return CanCopy
            End Get
        End Property
        Public Property FileSwatch As IBrush
        Public Property CatalogSwatch As IBrush
        Public Property IsKeywords As Boolean
        Public Property FileChips As New List(Of MetadataCompareChip)()
        Public Property CatalogChips As New List(Of MetadataCompareChip)()
        ''' <summary>Hinweis, wenn Datei und Beistelldatei verschiedenes sagen.</summary>
        Public Property ConflictText As String = ""
        ''' <summary>Datei und Katalog unterscheiden sich in einer Angabe, die der Katalog als
        ''' eigene fuehrt.</summary>
        Public Property Differs As Boolean
        Public Property FilePending As Boolean
        Public Property CatalogPending As Boolean
        Public Property CopyToFileCommand As ICommand
        Public Property CopyToCatalogCommand As ICommand
        Public Property DeleteFileCommand As ICommand
        Public Property DeleteCatalogCommand As ICommand

        Public ReadOnly Property IsValue As Boolean
            Get
                Return Not IsSection
            End Get
        End Property
        Public ReadOnly Property IsPlainValue As Boolean
            Get
                Return Not IsSection AndAlso Not IsKeywords
            End Get
        End Property
        Public ReadOnly Property HasConflict As Boolean
            Get
                Return ConflictText.Length > 0
            End Get
        End Property
        Public ReadOnly Property HasFileSwatch As Boolean
            Get
                Return FileSwatch IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property HasCatalogSwatch As Boolean
            Get
                Return CatalogSwatch IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property CanCopy As Boolean
            Get
                Return CopyToFileCommand IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property CanDeleteFile As Boolean
            Get
                Return DeleteFileCommand IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property CanDeleteCatalog As Boolean
            Get
                Return DeleteCatalogCommand IsNot Nothing
            End Get
        End Property
        Public ReadOnly Property FileOnlyText As String
            Get
                Return OnlyText(FileChips)
            End Get
        End Property
        Public ReadOnly Property CatalogOnlyText As String
            Get
                Return OnlyText(CatalogChips)
            End Get
        End Property
        Public ReadOnly Property HasFileOnly As Boolean
            Get
                Return FileOnlyText.Length > 0
            End Get
        End Property
        Public ReadOnly Property HasCatalogOnly As Boolean
            Get
                Return CatalogOnlyText.Length > 0
            End Get
        End Property

        Private Shared Function OnlyText(chips As List(Of MetadataCompareChip)) As String
            Dim only = chips.Where(Function(c) c.OnlyHere).Select(Function(c) c.Text).ToList()
            If only.Count = 0 Then Return ""
            Return String.Format(LocalizationService.T("Nur hier: {0}"), String.Join(", ", only))
        End Function
    End Class

    ''' <summary>Was der Vergleich beim Uebernehmen schreiben soll. Leer heisst: nichts.</summary>
    Public Class MetadataCompareChanges
        Public Property FileValues As CatalogValues
        Public Property FileFields As CatalogField
        Public Property CatalogValues As CatalogValues
        Public Property CatalogFields As CatalogField
        ''' <summary>Beschreibende Felder, die an der Datei gesetzt werden; "" entfernt das Feld.</summary>
        Public Property SetDescription As New Dictionary(Of ImageDescriptionField, String)()
        ''' <summary>Urheberrechtshinweis an der Datei: Nothing laesst ihn, "" entfernt ihn.</summary>
        Public Property SetCopyright As String
        Public Property ClearPlace As Boolean
        ''' <summary>Die Katalog-Abschrift von Beschreibung und Copyright aus der Datei neu
        ''' einlesen ("Dateiwert in den Katalog" bei diesen Feldern).</summary>
        Public Property RefreshCatalog As Boolean
        ''' <summary>Die Felder dazu; ein im Katalog geleertes Feld folgt danach wieder der Datei.</summary>
        Public Property RefreshCatalogKeys As New List(Of String)()
        ''' <summary>Katalog-Abschriften, die geleert werden: Namen der beschreibenden Felder,
        ''' "Copyright" oder "Place" (siehe <see cref="LibraryService.ClearCatalogCopies"/>).</summary>
        Public Property ClearCatalogCopies As New List(Of String)()

        Public ReadOnly Property HasChanges As Boolean
            Get
                Return FileFields <> CatalogField.None OrElse CatalogFields <> CatalogField.None OrElse
                       SetDescription.Count > 0 OrElse SetCopyright IsNot Nothing OrElse ClearPlace OrElse RefreshCatalog OrElse
                       ClearCatalogCopies.Count > 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Der Dialog "Metadaten vergleichen" fuer EIN Bild: links, was an der Datei steht (Datei und
    ''' Beistelldatei zusammen, so wie andere Programme es sehen), rechts, was der Katalog fuehrt.
    '''
    ''' Kopieren und Loeschen gibt es nur, wo das Ergebnis Bestand hat: Bewertung, Etikett,
    ''' Markierung und Stichwoerter fuehrt der Katalog als EIGENE Angaben, sie lassen sich in beide
    ''' Richtungen kopieren und auf beiden Seiten loeschen. Die beschreibenden Felder, Copyright und
    ''' der Ort sind im Katalog nur eine Abschrift der Datei; sie lassen sich auf beiden Seiten
    ''' loeschen, die Abschrift kommt aber zurueck, wenn die geaenderte Datei neu eingelesen wird.
    ''' Kamera und Aufnahmezeit sind reine Anzeige.
    '''
    ''' Nichts wird sofort geschrieben: jede Aktion wird vorgemerkt, geschrieben wird erst mit
    ''' "Anwenden" (<see cref="BuildChanges"/>, ausgefuehrt in der Galerie).
    ''' </summary>
    Public Class MetadataCompareViewModel
        Inherits ReactiveObject

        Private ReadOnly _path As String
        Private ReadOnly _file As FileCatalogSources
        Private ReadOnly _fileTarget As CatalogValues
        Private _fileChanged As CatalogField
        Private ReadOnly _catalogOriginal As CatalogValues
        Private ReadOnly _catalogTarget As CatalogValues
        Private _catalogChanged As CatalogField
        Private ReadOnly _isFavorite As Boolean
        Private ReadOnly _description As ImageDescription
        Private ReadOnly _descriptionEmbedded As ImageDescription
        Private ReadOnly _descriptionSidecar As ImageDescription
        ''' <summary>Vorgemerkte Werte der beschreibenden Felder an der Datei; "" entfernt.</summary>
        Private ReadOnly _descriptionSet As New Dictionary(Of ImageDescriptionField, String)()
        Private ReadOnly _catalogMeta As LibraryImageMeta
        Private ReadOnly _copyright As String
        ''' <summary>Vorgemerkter Urheberrechtshinweis an der Datei; Nothing = unveraendert.</summary>
        Private _copyrightSet As String
        ''' <summary>Felder (Name des Enums bzw. "Copyright"), deren Katalog-Abschrift aus der Datei
        ''' neu gelesen werden soll.</summary>
        Private ReadOnly _catalogFromFile As New HashSet(Of String)(StringComparer.Ordinal)
        ''' <summary>Felder (wie oben, dazu "Place"), deren Katalog-Abschrift geleert werden soll.</summary>
        Private ReadOnly _catalogClear As New HashSet(Of String)(StringComparer.Ordinal)
        Private Const CopyrightKey As String = "Copyright"
        Private Const PlaceKey As String = "Place"
        Private ReadOnly _camera As String
        Private ReadOnly _dateTaken As String
        Private ReadOnly _filePlace As String
        Private ReadOnly _hasCoordinates As Boolean
        Private _placeClear As Boolean

        Public ReadOnly Property Rows As New ObservableCollection(Of MetadataCompareRow)()

        ' Die Befehle entstehen beim ersten BuildRows, also auf dem Anzeigefaden; der Konstruktor
        ' laeuft im Hintergrund, weil er Dateien liest.
        Private _copyAllToCatalogCommand As ICommand
        Private _copyAllToFileCommand As ICommand

        Public ReadOnly Property CopyAllToCatalogCommand As ICommand
            Get
                Return _copyAllToCatalogCommand
            End Get
        End Property

        Public ReadOnly Property CopyAllToFileCommand As ICommand
            Get
                Return _copyAllToFileCommand
            End Get
        End Property

        Public ReadOnly Property FileName As String
            Get
                Return Path.GetFileName(_path)
            End Get
        End Property

        ''' <summary>Wohin "Datei" schreibt - das ist bei einer RAW nicht die Datei selbst.</summary>
        Public ReadOnly Property TargetNote As String
            Get
                If ImageProcessor.CanWriteKeywordsInto(_path) Then
                    Return LocalizationService.T("Änderungen an der Datei gehen in die Datei; eine vorhandene Beistelldatei wird mitgezogen.")
                End If
                Return LocalizationService.T("Änderungen an der Datei gehen in die XMP-Beistelldatei; die Bilddatei selbst bleibt unverändert.")
            End Get
        End Property

        Private _pendingText As String = ""
        Public Property PendingText As String
            Get
                Return _pendingText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_pendingText, value)
                Me.RaisePropertyChanged(NameOf(HasPending))
            End Set
        End Property

        Public ReadOnly Property HasPending As Boolean
            Get
                Return _pendingText.Length > 0
            End Get
        End Property

        ''' <summary>Liest alles fuer den Vergleich. Liest Dateien, also im Hintergrund rufen; die
        ''' Zeilen entstehen erst mit <see cref="BuildRows"/> auf dem Anzeigefaden.</summary>
        Public Shared Function Load(imagePath As String) As MetadataCompareViewModel
            Return New MetadataCompareViewModel(imagePath)
        End Function

        Private Sub New(imagePath As String)
            _path = imagePath
            _file = CatalogMetadataService.ReadFile(imagePath)
            _fileTarget = _file.Effective.Clone()
            _catalogOriginal = CatalogMetadataService.ReadCatalog(imagePath)
            _catalogTarget = _catalogOriginal.Clone()
            _catalogMeta = Nothing
            LibraryService.Instance.GetMetaForPaths({imagePath}).TryGetValue(imagePath, _catalogMeta)
            If _catalogMeta Is Nothing Then _catalogMeta = New LibraryImageMeta With {.FilePath = imagePath}
            _isFavorite = _catalogMeta.IsFavorite

            Dim exif = ExifService.ReadExif(imagePath)
            _description = If(exif.Description, New ImageDescription())
            _descriptionEmbedded = ImageDescriptionService.FromEmbeddedTags(exif)
            _descriptionSidecar = ExifService.ReadSidecarDescription(imagePath)
            _copyright = CopyrightService.NormalizeText(exif.Copyright)
            _camera = If(exif.DisplayCamera, "")
            _dateTaken = ExifService.FormatExifDate(exif.DateTaken)
            Dim fields = ExifService.ExtractSearchFields(exif, imagePath)
            _hasCoordinates = fields.GpsLatitude.HasValue AndAlso fields.GpsLongitude.HasValue
            _filePlace = PlacePanelService.Compose(fields.City, fields.Country)
            If _filePlace.Length = 0 AndAlso _hasCoordinates Then _filePlace = If(exif.GPS, "")
        End Sub

        ' ---------------------------------------------------------------- Aktionen

        Private Sub CopyAll(toCatalog As Boolean)
            For Each field In CatalogMetadataService.AllFields
                Copy(field, toCatalog, rebuild:=False)
            Next
            BuildRows()
        End Sub

        Private Sub Copy(field As CatalogField, toCatalog As Boolean, Optional rebuild As Boolean = True)
            Dim source = If(toCatalog, _fileTarget, _catalogTarget)
            Dim target = If(toCatalog, _catalogTarget, _fileTarget)
            SetField(target, field, source)
            If toCatalog Then _catalogChanged = _catalogChanged Or field Else _fileChanged = _fileChanged Or field
            If rebuild Then BuildRows()
        End Sub

        Private Sub Clear(field As CatalogField, onCatalog As Boolean)
            SetField(If(onCatalog, _catalogTarget, _fileTarget), field, New CatalogValues())
            If onCatalog Then _catalogChanged = _catalogChanged Or field Else _fileChanged = _fileChanged Or field
            BuildRows()
        End Sub

        ''' <summary>Genau ein Stichwort auf die andere Seite.</summary>
        Private Sub AddKeyword(keyword As String, toCatalog As Boolean)
            Dim target = If(toCatalog, _catalogTarget, _fileTarget)
            If Not target.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase) Then
                target.Keywords = target.Keywords.Concat({keyword}).ToList()
            End If
            If toCatalog Then _catalogChanged = _catalogChanged Or CatalogField.Keywords Else _fileChanged = _fileChanged Or CatalogField.Keywords
            BuildRows()
        End Sub

        ' Beschreibende Felder und Copyright: an der Datei sind sie echte Werte, im Katalog eine
        ' Abschrift, die beim naechsten Einlesen wieder aus der Datei kommt. "Nach links" schreibt
        ' den Katalogwert in die Datei; "nach rechts" liest die Abschrift aus der Datei neu ein.

        Private Function FileDescription(field As ImageDescriptionField) As String
            Dim value As String = Nothing
            Return If(_descriptionSet.TryGetValue(field, value), value, _description.GetValue(field))
        End Function

        Private Function ShownCatalogDescription(field As ImageDescriptionField) As String
            If _catalogClear.Contains(field.ToString()) Then Return ""
            Return If(_catalogFromFile.Contains(field.ToString()), FileDescription(field), CatalogDescription(field))
        End Function

        ''' <summary>Die Katalog-Abschrift leeren (Loeschen rechts). Hebt ein vorgemerktes
        ''' Neueinlesen desselben Feldes auf: beides zugleich widerspraeche sich.</summary>
        Private Sub ClearCatalogCopy(key As String)
            _catalogFromFile.Remove(key)
            _catalogClear.Add(key)
            BuildRows()
        End Sub

        Private Sub SetFileDescription(field As ImageDescriptionField, value As String)
            If String.Equals(value, _description.GetValue(field), StringComparison.Ordinal) Then
                _descriptionSet.Remove(field)
            Else
                _descriptionSet(field) = value
            End If
            BuildRows()
        End Sub

        Private Function FileCopyright() As String
            Return If(_copyrightSet, _copyright)
        End Function

        Private Function ShownCatalogCopyright() As String
            If _catalogClear.Contains(CopyrightKey) Then Return ""
            Return If(_catalogFromFile.Contains(CopyrightKey), FileCopyright(), If(_catalogMeta.Copyright, ""))
        End Function

        Private Sub SetFileCopyright(value As String)
            _copyrightSet = If(String.Equals(value, _copyright, StringComparison.Ordinal), Nothing, value)
            BuildRows()
        End Sub

        Private Sub RefreshCatalogFromFile(key As String)
            _catalogClear.Remove(key)
            _catalogFromFile.Add(key)
            BuildRows()
        End Sub

        Private Sub RemoveKeyword(keyword As String, onCatalog As Boolean)
            Dim target = If(onCatalog, _catalogTarget, _fileTarget)
            target.Keywords = target.Keywords.Where(Function(k) Not String.Equals(k, keyword, StringComparison.OrdinalIgnoreCase)).ToList()
            If onCatalog Then _catalogChanged = _catalogChanged Or CatalogField.Keywords Else _fileChanged = _fileChanged Or CatalogField.Keywords
            BuildRows()
        End Sub

        Private Shared Sub SetField(target As CatalogValues, field As CatalogField, source As CatalogValues)
            Select Case field
                Case CatalogField.Rating : target.Rating = source.Rating
                Case CatalogField.ColorLabel : target.ColorLabel = If(source.ColorLabel, "")
                Case CatalogField.PickState : target.PickState = source.PickState
                Case CatalogField.Keywords : target.Keywords = New List(Of String)(source.Keywords)
            End Select
        End Sub

        ''' <summary>Was beim Uebernehmen geschrieben werden soll. Eine Angabe, die vorgemerkt
        ''' wurde, aber wieder beim Ausgangsstand angekommen ist, faellt heraus: sie zu schreiben
        ''' aenderte nichts und kostete einen Schreibvorgang.</summary>
        Public Function BuildChanges() As MetadataCompareChanges
            Dim result As New MetadataCompareChanges With {
                .FileValues = _fileTarget.Clone(),
                .CatalogValues = _catalogTarget.Clone(),
                .SetCopyright = _copyrightSet,
                .ClearPlace = _placeClear,
                .RefreshCatalog = _catalogFromFile.Count > 0,
                .RefreshCatalogKeys = _catalogFromFile.ToList(),
                .ClearCatalogCopies = _catalogClear.ToList()}
            For Each field In CatalogMetadataService.AllFields
                If (_fileChanged And field) <> 0 AndAlso Not _fileTarget.SameAs(_file.Effective, field) Then result.FileFields = result.FileFields Or field
                If (_catalogChanged And field) <> 0 AndAlso Not _catalogTarget.SameAs(_catalogOriginal, field) Then result.CatalogFields = result.CatalogFields Or field
            Next
            For Each pair In _descriptionSet
                result.SetDescription(pair.Key) = pair.Value
            Next
            Return result
        End Function

        ' ---------------------------------------------------------------- Zeilen

        ''' <summary>Baut alle Zeilen neu. Auf dem Anzeigefaden rufen.</summary>
        Public Sub BuildRows()
            If _copyAllToCatalogCommand Is Nothing Then
                _copyAllToCatalogCommand = ReactiveCommand.Create(Sub() CopyAll(toCatalog:=True))
                _copyAllToFileCommand = ReactiveCommand.Create(Sub() CopyAll(toCatalog:=False))
                Me.RaisePropertyChanged(NameOf(CopyAllToCatalogCommand))
                Me.RaisePropertyChanged(NameOf(CopyAllToFileCommand))
            End If
            Rows.Clear()

            Rows.Add(Section(LocalizationService.T("Bewertung und Ordnung"), "star"))
            Rows.Add(CatalogRow(CatalogField.Rating, LocalizationService.T("Bewertung"), AddressOf RatingText))
            Rows.Add(CatalogRow(CatalogField.ColorLabel, LocalizationService.T("Etikett"), AddressOf LabelText))
            Rows.Add(CatalogRow(CatalogField.PickState, LocalizationService.T("Markierung"), AddressOf PickText))
            Rows.Add(New MetadataCompareRow With {
                .Label = LocalizationService.T("Favorit"),
                .FileText = LocalizationService.T("Gibt es nur im Katalog"),
                .CatalogText = If(_isFavorite, LocalizationService.T("Ja"), LocalizationService.T("Nein"))})
            Rows.Add(KeywordRow())

            Rows.Add(Section(LocalizationService.T("Beschreibung"), "text-aa"))
            For Each field In ImageDescription.AllFields
                Rows.Add(DescriptionRow(field))
            Next
            Rows.Add(CopyRow(LocalizationService.T("Copyright"), FileCopyright(), ShownCatalogCopyright(),
                             filePending:=_copyrightSet IsNot Nothing,
                             catalogPending:=Not String.Equals(ShownCatalogCopyright(), If(_catalogMeta.Copyright, ""), StringComparison.Ordinal),
                             toFile:=Sub() SetFileCopyright(ShownCatalogCopyright()),
                             toCatalog:=Sub() RefreshCatalogFromFile(CopyrightKey),
                             clearFile:=Sub() SetFileCopyright(""),
                             clearCatalog:=Sub() ClearCatalogCopy(CopyrightKey)))

            Rows.Add(Section(LocalizationService.T("Aufnahme"), "camera"))
            Rows.Add(New MetadataCompareRow With {.Label = LocalizationService.T("Kamera"), .FileText = _camera, .CatalogText = If(_catalogMeta.Camera, "")})
            Rows.Add(New MetadataCompareRow With {
                .Label = LocalizationService.T("Aufnahmedatum"), .FileText = _dateTaken,
                .CatalogText = ExifService.FormatExifDate(_catalogMeta.DateTaken)})
            Dim catalogPlace = PlacePanelService.Compose(_catalogMeta.City,
                                   PlaceLookupService.LocalizedCountry(_catalogMeta.CountryCode, _catalogMeta.Country))
            If catalogPlace.Length = 0 AndAlso _catalogMeta.GpsLatitude.HasValue AndAlso _catalogMeta.GpsLongitude.HasValue Then
                catalogPlace = String.Format(CultureInfo.InvariantCulture, "{0:0.#####}, {1:0.#####}",
                                             _catalogMeta.GpsLatitude.Value, _catalogMeta.GpsLongitude.Value)
            End If
            Dim catalogPlaceCleared = _catalogClear.Contains(PlaceKey)
            Rows.Add(New MetadataCompareRow With {
                .Label = LocalizationService.T("Ort"),
                .FileText = If(_placeClear, "", _filePlace),
                .FilePending = _placeClear,
                .CatalogText = If(catalogPlaceCleared, "", catalogPlace),
                .CatalogPending = catalogPlaceCleared,
                .DeleteFileCommand = If(_filePlace.Length > 0 AndAlso Not _placeClear,
                                        ReactiveCommand.Create(Sub()
                                                                   _placeClear = True
                                                                   BuildRows()
                                                               End Sub), Nothing),
                .DeleteCatalogCommand = If(catalogPlace.Length > 0 AndAlso Not catalogPlaceCleared,
                                           ReactiveCommand.Create(Sub() ClearCatalogCopy(PlaceKey)), Nothing)})

            Dim changes = BuildChanges()
            Dim pending = CountBits(changes.FileFields) + CountBits(changes.CatalogFields) +
                          changes.SetDescription.Count + If(changes.SetCopyright IsNot Nothing, 1, 0) +
                          If(_placeClear, 1, 0) + _catalogFromFile.Count + _catalogClear.Count
            PendingText = If(pending = 0, "", String.Format(LocalizationService.T("{0} Änderungen vorgemerkt"), pending))
        End Sub

        Private Shared Function Section(title As String, icon As String) As MetadataCompareRow
            Return New MetadataCompareRow With {
                .IsSection = True, .Label = title,
                .IconSource = "avares://FerrumPix/Assets/Icons/phosphor/regular/" & icon & ".svg"}
        End Function

        Private Function CatalogRow(field As CatalogField, label As String, format As Func(Of CatalogValues, String)) As MetadataCompareRow
            Dim fileHasValue = HasValue(_fileTarget, field)
            Dim catalogHasValue = HasValue(_catalogTarget, field)
            Dim row As New MetadataCompareRow With {
                .Label = label,
                .FileText = If(fileHasValue, format(_fileTarget), ""),
                .CatalogText = If(catalogHasValue, format(_catalogTarget), ""),
                .Differs = Not _fileTarget.SameAs(_catalogTarget, field),
                .FilePending = (_fileChanged And field) <> 0 AndAlso Not _fileTarget.SameAs(_file.Effective, field),
                .CatalogPending = (_catalogChanged And field) <> 0 AndAlso Not _catalogTarget.SameAs(_catalogOriginal, field),
                .CopyToFileCommand = ReactiveCommand.Create(Sub() Copy(field, toCatalog:=False)),
                .CopyToCatalogCommand = ReactiveCommand.Create(Sub() Copy(field, toCatalog:=True)),
                .DeleteFileCommand = If(fileHasValue, ReactiveCommand.Create(Sub() Clear(field, onCatalog:=False)), Nothing),
                .DeleteCatalogCommand = If(catalogHasValue, ReactiveCommand.Create(Sub() Clear(field, onCatalog:=True)), Nothing)}
            If field = CatalogField.ColorLabel Then
                row.FileSwatch = Swatch(_fileTarget.ColorLabel)
                row.CatalogSwatch = Swatch(_catalogTarget.ColorLabel)
            End If
            If field = CatalogField.Rating Then
                row.IsRating = True
                row.FileStars = Stars(_fileTarget.Rating)
                row.CatalogStars = Stars(_catalogTarget.Rating)
            End If
            If (_file.Conflicts And field) <> 0 AndAlso _file.Sidecar IsNot Nothing Then
                row.ConflictText = String.Format(LocalizationService.T("Datei und Beistelldatei weichen ab: in der Datei {0}, in der Beistelldatei {1}"),
                                                 format(_file.Embedded), format(_file.Sidecar))
            End If
            Return row
        End Function

        Private Function KeywordRow() As MetadataCompareRow
            Dim fileSet As New HashSet(Of String)(_fileTarget.Keywords, StringComparer.OrdinalIgnoreCase)
            Dim catalogSet As New HashSet(Of String)(_catalogTarget.Keywords, StringComparer.OrdinalIgnoreCase)
            Dim row = CatalogRow(CatalogField.Keywords, LocalizationService.T("Stichwörter"), Function(v) String.Join(", ", v.Keywords))
            row.IsKeywords = True
            row.FileChips = _fileTarget.Keywords.Select(Function(k) New MetadataCompareChip With {
                .Text = k, .OnlyHere = Not catalogSet.Contains(k), .IsFileSide = True,
                .CopyCommand = ReactiveCommand.Create(Sub() AddKeyword(k, toCatalog:=True)),
                .RemoveCommand = ReactiveCommand.Create(Sub() RemoveKeyword(k, onCatalog:=False))}).ToList()
            row.CatalogChips = _catalogTarget.Keywords.Select(Function(k) New MetadataCompareChip With {
                .Text = k, .OnlyHere = Not fileSet.Contains(k), .IsFileSide = False,
                .CopyCommand = ReactiveCommand.Create(Sub() AddKeyword(k, toCatalog:=False)),
                .RemoveCommand = ReactiveCommand.Create(Sub() RemoveKeyword(k, onCatalog:=True))}).ToList()
            Return row
        End Function

        ''' <summary>Eine Zeile fuer ein Feld, das an der Datei ein echter Wert und im Katalog eine
        ''' Abschrift ist. Kopieren in beide Richtungen, Loeschen auf beiden Seiten. Loeschen im
        ''' Katalog leert nur die Abschrift; aendert sich die Datei spaeter und wird neu eingelesen,
        ''' kommt ihr Wert zurueck.</summary>
        Private Function CopyRow(label As String, fileValue As String, catalogValue As String,
                                 filePending As Boolean, catalogPending As Boolean,
                                 toFile As Action, toCatalog As Action, clearFile As Action,
                                 clearCatalog As Action) As MetadataCompareRow
            Return New MetadataCompareRow With {
                .Label = label,
                .FileText = fileValue,
                .CatalogText = catalogValue,
                .Differs = Not String.Equals(fileValue, catalogValue, StringComparison.Ordinal),
                .FilePending = filePending,
                .CatalogPending = catalogPending,
                .CopyToFileCommand = ReactiveCommand.Create(toFile),
                .CopyToCatalogCommand = ReactiveCommand.Create(toCatalog),
                .DeleteFileCommand = If(fileValue.Length > 0, ReactiveCommand.Create(clearFile), Nothing),
                .DeleteCatalogCommand = If(catalogValue.Length > 0, ReactiveCommand.Create(clearCatalog), Nothing)}
        End Function

        Private Function DescriptionRow(field As ImageDescriptionField) As MetadataCompareRow
            Dim fileValue = FileDescription(field)
            Dim catalogValue = ShownCatalogDescription(field)
            Dim row = CopyRow(DescriptionLabel(field), fileValue, catalogValue,
                              filePending:=_descriptionSet.ContainsKey(field),
                              catalogPending:=Not String.Equals(catalogValue, CatalogDescription(field), StringComparison.Ordinal),
                              toFile:=Sub() SetFileDescription(field, ShownCatalogDescription(field)),
                              toCatalog:=Sub() RefreshCatalogFromFile(field.ToString()),
                              clearFile:=Sub() SetFileDescription(field, ""),
                              clearCatalog:=Sub() ClearCatalogCopy(field.ToString()))
            Dim embedded = _descriptionEmbedded.GetValue(field)
            Dim sidecar = _descriptionSidecar.GetValue(field)
            If embedded.Length > 0 AndAlso sidecar.Length > 0 AndAlso Not String.Equals(embedded, sidecar, StringComparison.Ordinal) Then
                row.ConflictText = String.Format(LocalizationService.T("Datei und Beistelldatei weichen ab: in der Datei {0}, in der Beistelldatei {1}"),
                                                 embedded, sidecar)
            End If
            Return row
        End Function

        Private Function CatalogDescription(field As ImageDescriptionField) As String
            Select Case field
                Case ImageDescriptionField.Title : Return If(_catalogMeta.Title, "")
                Case ImageDescriptionField.Headline : Return If(_catalogMeta.Headline, "")
                Case ImageDescriptionField.Description : Return If(_catalogMeta.Description, "")
                Case ImageDescriptionField.Creator : Return If(_catalogMeta.Creator, "")
                Case Else : Return If(_catalogMeta.Credit, "")
            End Select
        End Function

        Private Shared Function DescriptionLabel(field As ImageDescriptionField) As String
            Select Case field
                Case ImageDescriptionField.Title : Return LocalizationService.T("Titel")
                Case ImageDescriptionField.Headline : Return LocalizationService.T("Überschrift")
                Case ImageDescriptionField.Description : Return LocalizationService.T("Beschreibung")
                Case ImageDescriptionField.Creator : Return LocalizationService.T("Urheber")
                Case Else : Return LocalizationService.T("Quellenangabe")
            End Select
        End Function

        ' ---------------------------------------------------------------- Anzeige der Werte

        Private Shared Function RatingText(values As CatalogValues) As String
            If values.Rating <= 0 Then Return LocalizationService.T("Keine")
            Return String.Format(LocalizationService.T("{0} von 5"), values.Rating)
        End Function

        Private Shared Function PickText(values As CatalogValues) As String
            If values.PickState > 0 Then Return LocalizationService.T("Behalten")
            If values.PickState < 0 Then Return LocalizationService.T("Verworfen")
            Return LocalizationService.T("Keine")
        End Function

        ''' <summary>Der Name der Etikettfarbe, uebersetzt. Ein Hex-Wert, der kein Palettenwert
        ''' ist, steht als Hex da: das ist ehrlicher als ein geratener Name.</summary>
        Private Shared Function LabelText(values As CatalogValues) As String
            If String.IsNullOrEmpty(values.ColorLabel) Then Return LocalizationService.T("Kein")
            Select Case XmpSidecarService.LabelToXmpWord(values.ColorLabel)
                Case "Red" : Return LocalizationService.T("Rot")
                Case "Yellow" : Return LocalizationService.T("Gelb")
                Case "Green" : Return LocalizationService.T("Grün")
                Case "Blue" : Return LocalizationService.T("Blau")
                Case "Purple" : Return LocalizationService.T("Lila")
                Case "Orange" : Return LocalizationService.T("Orange")
                Case "Pink" : Return LocalizationService.T("Pink")
                Case Else : Return values.ColorLabel
            End Select
        End Function

        Private Shared Function Swatch(colorHex As String) As IBrush
            If String.IsNullOrEmpty(colorHex) Then Return Nothing
            Try
                Return New SolidColorBrush(Color.Parse(colorHex))
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function Stars(rating As Integer) As List(Of MetadataCompareStar)
            Return Enumerable.Range(1, 5).Select(Function(i) New MetadataCompareStar With {.IsOn = i <= rating}).ToList()
        End Function

        ''' <summary>Steht ueberhaupt etwas da? Bewertung 0, kein Etikett, keine Markierung und keine
        ''' Stichwoerter heissen "keine Angabe"; nur dann gibt es nichts zu loeschen.</summary>
        Private Shared Function HasValue(values As CatalogValues, field As CatalogField) As Boolean
            Select Case field
                Case CatalogField.Rating : Return values.Rating > 0
                Case CatalogField.ColorLabel : Return Not String.IsNullOrEmpty(values.ColorLabel)
                Case CatalogField.PickState : Return values.PickState <> 0
                Case CatalogField.Keywords : Return values.Keywords.Count > 0
                Case Else : Return False
            End Select
        End Function

        Private Shared Function CountBits(fields As CatalogField) As Integer
            Return CatalogMetadataService.AllFields.Where(Function(f) (fields And f) <> 0).Count()
        End Function

    End Class

End Namespace
