Imports System.Windows.Input

Namespace ViewModels

    ''' <summary>
    ''' Die Kommandos, die ein Bereich fuer sein Kontextmenue anbietet. Nicht gesetzte bleiben
    ''' Nothing - der Bauplan laesst den zugehoerigen Eintrag dann weg.
    '''
    ''' Warum ein Buendel und keine Schnittstelle je ViewModel: Galerie, Betrachter und Editor
    ''' benennen dieselbe Sache verschieden (ResizeSelectedCommand gegen ResizeCurrentCommand), und
    ''' nicht jeder kann alles. Ein Buendel macht diese Zuordnung an EINER Stelle sichtbar, statt
    ''' sie ueber drei ViewModels zu verteilen.
    ''' </summary>
    Public NotInheritable Class MenuCommands

        ' Immer da, unabhaengig vom Bild
        Public Property NewImage As ICommand
        Public Property Fullscreen As ICommand

        ' Oeffnen und Vergleichen
        Public Property ShowImage As ICommand
        Public Property Adjust As ICommand
        Public Property Compare As ICommand
        Public Property PinImage As ICommand

        ''' <summary>Nur im Filmstreifen des Editors, auf einer Kachel, die nicht geoeffnet ist
        ''' (MenuSite.EditorFilmstripOther): diese Kachel oeffnen bzw. als Ebene einsetzen.</summary>
        Public Property OpenInEditor As ICommand
        Public Property InsertAsLayer As ICommand

        ' Speichern (nur Editor)
        Public Property Save As ICommand
        Public Property SaveAs As ICommand

        ' Dateiarbeit
        Public Property NewFolder As ICommand
        Public Property Rename As ICommand
        Public Property Copy As ICommand
        Public Property Cut As ICommand
        Public Property Paste As ICommand
        Public Property Duplicate As ICommand
        Public Property Delete As ICommand

        ''' <summary>Zurueck aus dem Papierkorb einer Serverquelle. Nur die Galerie bietet es an, und
        ''' auch dort nur in der Papierkorbansicht.</summary>
        Public Property RestoreFromTrash As ICommand

        ''' <summary>Aussortieren: in einer Serie den Rest verwerfen, einen Stapel auf- oder
        ''' zuklappen. Nur die Galerie bietet sie an - Serien und Stapel gibt es nur dort. Behalten
        ''' und Verwerfen selbst sind keine Eintraege mehr, sondern Knoepfe neben dem Herz
        ''' (<see cref="PickState"/>).</summary>
        Public Property RejectRestOfStack As ICommand
        Public Property ToggleStack As ICommand

        ' Stapelarbeit am Bild
        Public Property ResizeImage As ICommand
        Public Property ApplyWatermark As ICommand
        Public Property ApplyFilter As ICommand
        ''' <summary>Bildanpassungen EINER Kachel in die gemeinsame Ablage, die Ablage auf die
        ''' Auswahl. Nur die Galerie bietet sie an; im Editor stehen Kopieren und Einfügen im
        ''' Anpassen-Werkzeug.</summary>
        Public Property CopyAdjustments As ICommand
        Public Property PasteAdjustments As ICommand
        Public Property ConvertTo As ICommand
        Public Property ExportTo As ICommand
        Public Property RemoveMetadata As ICommand
        Public Property CreateCollage As ICommand
        Public Property Print As ICommand

        ''' <summary>Ersetzt die automatisch ermittelten Stichwörter der betroffenen lokalen
        ''' Bilder durch einen neuen Lauf des installierten KI-Modells.</summary>
        Public Property ReanalyzeAiTags As ICommand

        ' Bild drehen (Betrachter und Editor)
        Public Property RotateLeft As ICommand
        Public Property RotateRight As ICommand

        ' Bewertung, Favorit, Etikett
        Public Property Favorite As ICommand
        Public Property Rating As ICommand
        Public Property ColorLabel As ICommand

        ''' <summary>Behalten oder Verwerfen setzen: der Parameter ist der ZIELwert (1 behalten,
        ''' -1 verworfen, 0 keiner). Das Umschalten, derselbe Knopf noch einmal nimmt es zurueck,
        ''' rechnet die Menuezeile aus, die den gemeinsamen Stand kennt.</summary>
        Public Property PickState As ICommand

        ''' <summary>Der Zielwert aus dem Parameter von <see cref="PickState"/>.</summary>
        Public Shared Function PickStateFrom(parameter As Object) As Integer
            Dim value As Integer
            If parameter Is Nothing OrElse Not Integer.TryParse(Convert.ToString(parameter, Globalization.CultureInfo.InvariantCulture),
                                                              Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, value) Then Return 0
            Return Services.LibraryService.NormalizePickState(value)
        End Function

        ''' <summary>Behalten oder Verwerfen fuer die gemeinten Bilder schreiben, fuer Betrachter und
        ''' Editor. Nur LOKALE Bilder: die Markierung lebt im lokalen Katalog. Gibt die Pfade zurueck,
        ''' damit der Aufrufer sein offenes Bild nachziehen kann.</summary>
        Public Shared Function WritePickState(items As IEnumerable(Of Models.ImageItem), state As Integer) As List(Of String)
            Dim locals = If(items, Enumerable.Empty(Of Models.ImageItem)()).
                Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not i.IsRemoteAsset AndAlso
                                  Not String.IsNullOrEmpty(i.FilePath)).ToList()
            For Each item In locals
                item.PickState = state
            Next
            Dim paths = locals.Select(Function(i) i.FilePath).ToList()
            If paths.Count > 0 Then Services.LibraryService.Instance.SetPickStateForMany(paths, state, syncToXmp:=True)
            Return paths
        End Function

        ''' <summary>Aufnahmeort: den eines Bildes merken, den gemerkten auf eine Auswahl anwenden,
        ''' oder einen im Dialog bestimmen. Kopieren gilt fuer EIN Bild, Einfuegen und Setzen fuer
        ''' die ganze Auswahl - der Regelfall ist eine Reihe Aufnahmen vom selben Ort.</summary>
        Public Property CopyPlace As ICommand
        Public Property PastePlace As ICommand
        Public Property SetPlace As ICommand
        ''' <summary>Den Ort aus einer GPS-Aufzeichnung holen, ueber die Aufnahmezeit. Steht neben
        ''' "Setzen", weil es dieselbe Frage beantwortet - nur fuer eine ganze Reihe Bilder auf
        ''' einmal und mit je eigener Koordinate.</summary>
        Public Property SetPlaceFromTrack As ICommand
        Public Property RemovePlace As ICommand
        Public Property SetCopyright As ICommand
        Public Property SetCaptureDate As ICommand
        Public Property OpenPlaceInOsm As ICommand

        ' Wege nach draussen
        Public Property CopyPath As ICommand
        Public Property ShowInFileManager As ICommand

        ''' <summary>"Öffnen mit": das Kommando bekommt die Kennung des Programms als Parameter.
        ''' Die Programme stehen daneben, damit der Bauplan ohne Blick in die Einstellungen
        ''' auskommt. Keine Programme heisst: das Untermenue faellt weg.</summary>
        Public Property OpenWith As ICommand
        Public Property OpenWithPrograms As IReadOnlyList(Of Services.OpenWithProgramSettings)

        ''' <summary>Das gerenderte Bild an G'MIC geben und das Ergebnis als Ebene zurueckholen.
        ''' Nur der Editor setzt es, und nur, wenn G'MIC gefunden wurde.</summary>
        Public Property EditWithGmic As ICommand

        ''' <summary>Den einfarbigen Hintergrund des Fotos entfernen, das Ergebnis als Ebene darueber.
        ''' Nur der Editor setzt es.</summary>
        Public Property RemovePhotoBackground As ICommand

    End Class

End Namespace
