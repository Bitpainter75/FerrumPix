Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    Public Class SavedSearchSettings
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property TextQuery As String = ""
        Public Property RootFolder As String = ""
        Public Property IncludeSubfolders As Boolean = True
        Public Property FavoriteMode As String = "Any"
        Public Property RatingMin As Integer = -1
    End Class

    Public Class WatermarkPresetSettings
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Text As String = ""
        Public Property ImagePath As String = ""
        Public Property OffsetXPixels As Double = 24
        Public Property OffsetYPixels As Double = 24
        Public Property WidthPixels As Double = 480
        Public Property HeightPixels As Double = 180
        Public Property Anchor As String = "BottomRight"
        ''' Seitenverhaeltnis des Wasserzeichens gesperrt (nur bei Bild-Wasserzeichen sinnvoll).
        ''' Gemerkt, damit der Stapel-Dialog die Hoehe aus der Breite ableiten kann statt zu raten.
        Public Property LockAspect As Boolean = True
        Public Property RotationDegrees As Double = 0
        Public Property Opacity As Double = 100
        Public Property FontFamily As String = "Arial"
        Public Property FontSizePixels As Double = 48
        Public Property FillColor As String = "#FFFFFFFF"
    End Class

    ''' <summary>Eine gespeicherte Regler-Zusammenstellung („Vorlage") aus dem Anpassen-Werkzeug.
    ''' Inhalt ist dasselbe Rezept-JSON wie bei der Kopier-Ablage - also NUR die Pixel-Anpassungen
    ''' (Anpassen, Farbe, Details, Effekte). Zuschnitt, Drehung, Objekte und Masken beschreiben ein
    ''' bestimmtes Bild und gehören bewusst nicht dazu.</summary>
    Public Class AdjustmentPresetSettings
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property RecipeJson As String = ""
    End Class

    ''' <summary>Eine von Hand gesetzte Objektiv-Zuordnung: der Name aus dem EXIF und der Eintrag
    ''' aus der mitgelieferten Sammlung, der dafuer gelten soll.</summary>
    Public Class LensAssignment
        Public Property ExifName As String = ""
        Public Property Modell As String = ""
    End Class

    Public Class XmpPresetSettings
        Implements INotifyPropertyChanged

        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Path As String = ""

        Private _isLastApplied As Boolean
        Public Property IsLastApplied As Boolean
            Get
                Return _isLastApplied
            End Get
            Set(value As Boolean)
                If _isLastApplied = value Then Return
                _isLastApplied = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsLastApplied)))
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    End Class

    Public Class LutPresetSettings
        Implements INotifyPropertyChanged

        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Path As String = ""

        Private _isLastApplied As Boolean
        Public Property IsLastApplied As Boolean
            Get
                Return _isLastApplied
            End Get
            Set(value As Boolean)
                If _isLastApplied = value Then Return
                _isLastApplied = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsLastApplied)))
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    End Class

    Public Class AppSettings
        Public Property GalleryThumbnailSize As Double = 260
        Public Property GalleryViewMode As String = "Grid"
        Public Property GallerySortMode As String = AppSettingsService.DefaultGallerySortMode
        Public Property GallerySortAscending As Boolean = AppSettingsService.DefaultGallerySortAscending
        Public Property GalleryShowFolders As Boolean = True
        Public Property GalleryShowParentFolder As Boolean = True
        ' Galerie-Kachel-Badges: True = immer sichtbar, False = erst beim Mouseover. Standard spiegelt
        ' das bisherige Verhalten (Sterne nur beim Überfahren, Favorit/Metadaten immer).
        Public Property GalleryRatingBadgesAlwaysVisible As Boolean = False
        Public Property GalleryFavoriteBadgeAlwaysVisible As Boolean = True
        Public Property GalleryMetadataBadgesAlwaysVisible As Boolean = True
        Public Property GalleryFilterFavorite As String = "All"
        Public Property GalleryFilterRatings As New List(Of Integer)()
        Public Property GalleryFilterFileType As String = "All"
        Public Property GalleryStartupFolderMode As String = "Pictures"
        Public Property GalleryStartupCustomFolder As String = ""
        Public Property LastGalleryFolder As String = ""
        Public Property LastSaveAsTargetFolder As String = ""
        Public Property ViewerShowFilmstrip As Boolean = True
        Public Property ViewerSlideshowIntervalSeconds As Integer = 3
        Public Property ViewerOpenFitToWindow As Boolean = True
        ''' "Always" (immer einpassen, auch kleinere Bilder hochskalieren) oder "OnlyWhenLarger"
        ''' (nur einpassen, wenn das Bild größer als die Darstellungsfläche ist, sonst 100%).
        Public Property ViewerFitBehavior As String = "Always"
        ''' Dasselbe für den Editor, aber GETRENNT einstellbar: beim Betrachten will man ein kleines
        ''' Bild oft formatfüllend sehen, beim Bearbeiten dagegen in Originalgröße.
        ''' Leer = noch nie gesetzt; dann wird beim Laden EINMALIG der Viewer-Wert übernommen, damit
        ''' die bisherige gemeinsame Einstellung nicht stillschweigend zurückspringt.
        Public Property EditorFitBehavior As String = ""
        Public Property EditorShowFilmstrip As Boolean = True
        ''' Kantenlänge einer Rasterzelle im Editor, in Bildpixeln.
        Public Property EditorGridSize As Integer = 50
        Public Property EditorShowRulers As Boolean = False
        Public Property EditorShowGrid As Boolean = False
        Public Property ShowHiddenFolders As Boolean = False
        ''' Ob Dateiarbeit einem Verweis folgen darf, der aus dem Benutzerordner hinausfuehrt.
        ''' Standard AUS: das ist die sichere Richtung. Wer seinen Bilderordner bewusst per Verweis
        ''' auf eine andere Platte legt, schaltet es ein.
        Public Property FollowLinkedFolders As Boolean = False
        ' Löschen: standardmäßig in den Papierkorb und mit Sicherheitsabfrage. Beide Schalter können das
        ' einzeln abschalten (True = überspringen).
        Public Property DeleteSkipTrash As Boolean = False
        Public Property DeleteSkipConfirmation As Boolean = False
        Public Property ThemeMode As String = "Dark"
        Public Property AccentColor As String = "#F08A1A"
        Public Property StartupImageMode As String = "Viewer"
        ''' Womit ein Bild aus der Galerie geoeffnet wird: "Viewer" oder "Editor".
        Public Property GalleryOpenTarget As String = "Viewer"
        ''' <summary>Was beim Start OHNE Bildparameter erscheint. "Viewer" öffnet das erste Bild des
        ''' Startordners, "Editor" den leeren Editor mit dem Dialog „Neues Bild".</summary>
        Public Property StartupNoImageMode As String = "Gallery"
        Public Property LanguageMode As String = "System"
        Public Property ThumbnailCacheEnabled As Boolean = True
        Public Property ThumbnailQuality As Integer = 82
        Public Property GalleryThumbnailMemoryCacheCapacity As Integer = 250
        Public Property JpgSaveQuality As Integer = 90
        ''' Vorgewähltes Zielformat in „Speichern unter", „Konvertieren nach" und „Exportieren nach".
        ''' "JPG" | "PNG" | "WEBP" | "FPX" - FPX gibt es nur beim Speichern unter, die übrigen
        ''' Dialoge fallen dort auf JPG zurück.
        Public Property DefaultSaveFormat As String = "JPG"
        Public Property PreserveMetadataOnSave As Boolean = True
        ''' Optionaler XMP-Katalog-Sync (Standard AUS): schreibt Rating/Farb-Label/Stichworte zusätzlich
        ''' zum .fpxmp-Rezept in ein Adobe-XMP-Sidecar, damit andere Programme sie sehen. .fpxmp bleibt
        ''' die primäre Bearbeitungsquelle. Erst per Einstellung aktivierbar.
        Public Property SyncCatalogToXmp As Boolean = False
        ''' Ob der Katalog-Sync dabei auch eine FEHLENDE .xmp neu anlegen darf (Standard AUS: nur
        ''' vorhandene Sidecars aktualisieren).
        Public Property CreateXmpSidecarIfMissing As Boolean = False
        ''' Entwickelte RAW-Vorschau statt eingebettetem JPG (Standard AUS). Wirkt NUR, wenn eine .fpxmp
        ''' existiert - unbearbeitete RAWs bleiben beim schnellen eingebetteten JPG. Getrennt für
        ''' Galerie-Thumbnails und Viewer, weil der Viewer teurer entwickelt.
        Public Property DevelopRawThumbnails As Boolean = False
        Public Property DevelopRawInViewer As Boolean = False
        ''' RAWs OHNE .fpxmp-Rezept auch in den Stapelfunktionen voll entwickeln (Demosaic in
        ''' Sensoraufloesung) statt ihre eingebettete JPEG-Vorschau zu nehmen. Mit Rezept wird
        ''' IMMER entwickelt - dort waere die Vorschau schlicht das falsche Bild.
        ''' Standard an: das ist das bisherige Verhalten. Aus ist der schnelle Weg.
        Public Property DevelopRawInBatch As Boolean = True

        ''' <summary>Kamera-Referenzwerte fuer die Grundbelichtung benutzen (CameraBaselineTable).
        ''' Standard AUS: die Werte gleichen die Kameramodelle UNTEREINANDER an, ihre absolute Lage
        ''' haengt aber an einer einzigen Kamera mit echtem Referenzexport - und die ist selbst
        ''' untypisch. Bis ein zweiter Referenzexport vorliegt, ist das eine Wahl des Nutzers und
        ''' keine Vorgabe. Siehe Audits/OFFENE_PUNKTE.md.</summary>
        Public Property UseCameraBaselineTable As Boolean = False
        Public Property EditorInfoSidebarExpanded As Boolean = True
        ''' Ob das Ebenen-Panel im Editor zuletzt eingeblendet war - gemerkter Bedienzustand (wie die
        ''' Info-Leiste), kein Schalter in den Einstellungen. Standard aus: es ist ein Profi-Werkzeug.
        Public Property EditorLayersPanelExpanded As Boolean = False
        ''' Linke Werkzeugleiste des Editors eingeklappt (nur Symbole, keine Beschriftungen) -
        ''' gemerkter Bedienzustand wie die Info-Leiste, der Umschalter sitzt in der Leiste selbst.
        Public Property EditorToolSidebarCollapsed As Boolean = False
        ''' Werkzeug, das beim Betreten des Editors aktiv ist: "Selection" (Auswahl, bisheriges
        ''' Verhalten) oder "Adjust" (Anpassen).
        Public Property EditorStartupTool As String = "Selection"
        ''' Reihenfolge der drei Werkzeuggruppen in der linken Editor-Leiste, von oben nach unten.
        ''' Erlaubt sind genau "Adjust", "Transform" und "Tools", jeder Name genau einmal.
        Public Property EditorToolGroupOrder As String = "Adjust,Transform,Tools"
        ''' Anpassungsgruppen, die im Editor NICHT erscheinen sollen, als Komma-Liste ihrer
        ''' Schluessel. Leer heisst: alle sind da. Ausblenden aendert nur die Anzeige - eingestellte
        ''' Werte bleiben erhalten und wirken weiter.
        Public Property HiddenAdjustmentGroups As String = ""
        ''' Gemerkter Auf-/Zuklapp-Zustand jeder Editor-Gruppe (Expander), Schlüssel = stabiler
        ''' Gruppenname (siehe Controls.ExpanderState). Fehlt ein Schlüssel, gilt der XAML-Standard.
        Public Property EditorExpanderStates As New Dictionary(Of String, Boolean)()
        ''' „Originale überschreiben" im Filter-anwenden-Dialog - bleibt über Sitzungen erhalten
        '''.
        Public Property BatchFilterOverwriteOriginals As Boolean = False
        ''' „Originale überschreiben" im Wasserzeichen-anwenden-Dialog - bleibt über Sitzungen erhalten
        ''' (analog zu Filter anwenden).
        Public Property BatchWatermarkOverwriteOriginals As Boolean = True
        ''' „Originale überschreiben" im Bildgröße-ändern-Dialog. Standard AN = bisheriges Verhalten;
        ''' abgewählt entstehen Kopien mit Formatauswahl wie beim Filter-Dialog.
        Public Property BatchResizeOverwriteOriginals As Boolean = True

        ''' Welche Sektionen im Dialog "Exportieren nach" zuletzt aktiv waren - der Sammel-Export
        ''' wird meist mit derselben Zusammenstellung wiederholt.
        Public Property ExportToUseFilter As Boolean = False
        Public Property ExportToUseWatermark As Boolean = False
        Public Property ExportToUseResize As Boolean = False
        ''' „Wasserzeichen nicht mitskalieren" im Export-Dialog: normalerweise gelten die Maße der
        ''' Vorlage für das Originalbild und schrumpfen mit der Verkleinerung mit. Ist der Schalter
        ''' an, gelten sie für das fertige Bild - das Wasserzeichen bleibt in jeder Ausgabegröße
        ''' gleich groß.
        Public Property ExportToWatermarkKeepSize As Boolean = False
        ''' Abstand der EINRAST-Linien (Sicherheitsabstand) zu den Bildrändern in Prozent - die
        ''' pinken Ziel-Linien, an denen Objekte beim Verschieben einrasten
        ''' (präzisiert: gemeint war diese Linie, nicht das Zuschneiden-Werkzeug).
        ''' 0 = deaktiviert; Ränder und Mitte rasten immer. 4 = bisheriges festes Verhalten.
        Public Property EditorSnapMarginPercent As Integer = 4
        ''' Ob der Vorher/Nachher-Vergleich im Editor zuletzt eingeschaltet war - gemerkter Bedienzustand
        ''' (wie die Info-Leiste), kein Schalter in den Einstellungen.
        Public Property EditorShowComparison As Boolean = True
        Public Property ViewerInfoSidebarExpanded As Boolean = True
        ''' In der Galerie ist die Info-Leiste ab Werk ZU: dort stehen links schon Ordnerbaum und
        ''' Filter, und wer die Galerie oeffnet, sucht ein Bild und liest keine Metadaten.
        Public Property GalleryInfoSidebarExpanded As Boolean = False
        ''' Ganzzahliger Versatz auf alle Text-Schriftgrößen (siehe FontScaleService). 0 = Auslieferung.
        Public Property FontSizeOffset As Integer = 0
        Public Property ApplicationScale As Double = 1.0
        Public Property ApplicationScaleScreen As String = "HDMI-A-1"
        Public Property MainWindowLeft As Integer = -1
        Public Property MainWindowTop As Integer = -1
        Public Property MainWindowWidth As Double = 1536
        Public Property MainWindowHeight As Double = 1024
        ''' Nur maximiert/nicht maximiert. Der Viewer-Vollbildmodus (WindowState.FullScreen mit
        ''' verstecktem Zeiger) ist ein ANSICHTS-Modus und wird bewusst nicht wiederhergestellt -
        ''' beim Start ohne Bild waere er eine Sackgasse. Wer im Vollbild beendet, kommt maximiert zurueck.
        Public Property MainWindowMaximized As Boolean = False
        Public Property SavedSearches As New List(Of SavedSearchSettings)()
        Public Property VideoHardwareAcceleration As Boolean = False

        ''' <summary>Die gelernten Modelle auf der Grafikkarte rechnen lassen. AB WERK AUS.
        '''
        ''' Nicht, weil das Ergebnis ein anderes wäre - dieselbe Datei rechnet auf der Karte
        ''' dasselbe wie auf dem Prozessor -, sondern weil es Rechner gibt, auf denen es schadet:
        ''' eine im Prozessor eingebaute Grafikeinheit teilt sich den Speicher mit allem anderen,
        ''' und wenn ein großes Modell dort nicht hineinpasst, wird nicht nur das Modell langsam,
        ''' sondern die ganze Oberfläche. Wer seine Karte kennt, schaltet es ein. Die Einzelheiten
        ''' stehen bei <c>GpuAccelerationService</c>.</summary>
        Public Property GpuAccelerationEnabled As Boolean = False

        ''' <summary>WELCHE Grafikkarte, wenn mehrere im Rechner stecken. Leer heißt: die
        ''' Anwendung sucht sich eine aus und bevorzugt dabei die eigene Karte.
        '''
        ''' Der Wert ist der Schlüssel aus <c>GpuAccelerationService.GpuDeviceInfo</c>, also
        ''' Hersteller, Modell und Steckplatz - nicht die Stelle in der Liste. Zeigt er nach einem
        ''' Umbau ins Leere, greift wieder die Vorauswahl, statt die Funktion abzuschalten.</summary>
        Public Property GpuAccelerationDevice As String = ""

        ''' <summary>Gesichter im eigenen Bestand suchen und zu Personen zusammenfassen. AB WERK AUS.
        '''
        ''' Kein Standardverhalten, sondern eine Entscheidung des Benutzers: die Erkennung liest den
        ''' ganzen Bestand durch und legt biometrische Merkmale in der Bibliothek ab. Das ist etwas
        ''' anderes als ein Stichwort, auch wenn es lokal bleibt - wer es nicht ausdrücklich
        ''' einschaltet, bekommt es nicht. Ohne die beiden Modelldateien bleibt der Schalter
        ''' wirkungslos; das entscheidet <c>AiModelService</c>.</summary>
        Public Property FaceRecognitionEnabled As Boolean = False

        ''' <summary>Aufnahmeorte auf einer Karte zeigen. AB WERK AUS.
        '''
        ''' Ebenfalls eine bewusste Entscheidung: die Koordinaten liegen zwar längst in der
        ''' Bibliothek, aber ein Kartenbild kommt von einem fremden Dienst, und jede Anfrage verrät
        ''' ihm, WO fotografiert wurde. Bei einer Anwendung, die sonst nichts nach draußen gibt,
        ''' darf das niemand ungefragt bekommen.</summary>
        Public Property PhotoMapEnabled As Boolean = False

        ''' <summary>Wie gross ein Gesicht mindestens sein muss, um ueberhaupt aufgenommen zu werden -
        ''' in Prozent der KUERZEREN Bildkante.
        '''
        ''' Relativ und nicht in Bildpunkten: 80 Punkte sind auf einem 24-Megapixel-Foto ein Kopf in
        ''' der Menge und auf einem Handyschnappschuss ein Portraet. Die Prozentzahl meint dasselbe
        ''' auf jedem Bild.
        '''
        ''' 0 heisst: keine zusaetzliche Grenze - dann gilt allein die absolute Untergrenze der
        ''' Erkennung (FaceDetectionService.MinimumFaceSize), unter der es ohnehin keine Merkmale
        ''' gibt. Wer auf einem Stadtfest nicht die dritte Reihe im Hintergrund in seiner
        ''' Personenliste haben will, dreht hier hoch.
        '''
        ''' AB WERK 3, und der Regler geht nur bis 10. Gemessen an einem gewachsenen Bestand: bei 4
        ''' Prozent hatte das kleinste noch behaltene Gesicht rund 100 bis 130 Punkte und lag damit
        ''' klar ueber der absoluten Untergrenze - der Wert schneidet nach Geschmack und nicht vor
        ''' Unbrauchbarem. 3 laesst etwas mehr stehen und liegt auf einem ueblichen Foto (kurze Kante
        ''' 3000) immer noch bei 90 Punkten. Nach oben ist bei 10 Schluss: dort faellt schon rund die
        ''' Haelfte aller gefundenen Gesichter weg, weiter zu drehen streicht Leute, die man haben
        ''' will.</summary>
        Public Property FaceMinimumSizePercent As Double = 3

        Public Property TransparencyBackgroundMode As String = "Checkerboard"
        Public Property TransparencyBackgroundColor As String = "#FFFFFFFF"
        Public Property LastBatchRenamePattern As String = "{name}_###"
        Public Property LastBatchRenameStart As Integer = 1
        Public Property LastBatchRenameStep As Integer = 1
        ''' Muster fuer die ZIELDATEINAMEN der Stapelfunktionen (Exportieren nach, Konvertieren
        ''' nach, Bildgroesse aendern, Filter/Wasserzeichen anwenden). Leer = Originalname behalten.
        ''' Wird ueber Dialogoeffnungen hinweg gemerkt: wer einmal ein Muster festgelegt hat, will
        ''' es beim naechsten Stapel meist wieder.
        Public Property LastTargetNamePattern As String = ""
        ''' Kopierte Bildanpassungen als Rezept-JSON (nur die Pixel-Anpassungen, keine Geometrie
        ''' und keine Objekte). Liegt in den Einstellungen statt im Arbeitsspeicher, damit
        ''' "Kopieren" in der einen Sitzung und "Einfuegen" in der naechsten funktioniert - genau
        ''' dafuer ist die Funktion da. Leer = nichts kopiert.
        Public Property CopiedAdjustments As String = ""
        Public Property LastBatchResizeWidth As Integer = 0
        Public Property LastBatchResizeHeight As Integer = 0
        Public Property LastBatchResizeScalePercent As Integer = 0
        Public Property LastBatchResizeLockAspect As Boolean = True
        ''' "Nicht vergroessern" im Bildgroessen-/Export-Formular (Standard aus = bisheriges Verhalten).
        Public Property LastBatchResizeNoUpscale As Boolean = False
        ''' "Lange Kante": statt Breite und Hoehe wird EIN Wert eingetragen, der bei jedem Bild die
        ''' laengere Kante begrenzt - die Ausrichtung ist dann egal. Das Seitenverhaeltnis ist dabei
        ''' zwingend gehalten.
        Public Property LastBatchResizeLongEdge As Boolean = False
        Public Property LastBatchResizeInterpolation As String = "Bilinear"
        Public Property LastWatermarkPresetName As String = ""
        Public Property EnableDiagnosticLogging As Boolean = False
        Public Property WatermarkPresets As New List(Of WatermarkPresetSettings)()
        ''' Gespeicherte Regler-Zusammenstellungen aus dem Anpassen-Werkzeug (siehe
        ''' AdjustmentPresetSettings) und der Name der zuletzt benutzten.
        Public Property AdjustmentPresets As New List(Of AdjustmentPresetSettings)()
        Public Property LastAdjustmentPresetName As String = ""
        ' Der Name bleibt so: er ist der Schluessel in der settings.json. Umbenennen hiesse,
        ' dass bereits gespeicherte Presets bei vorhandenen Installationen verschwinden.
        ''' <summary>Objektivkorrektur (Verzeichnung, Farbquerfehler, Vignettierung) als
        ''' VORGABE fuer neue Bilder. Standard AN: die Korrektur beruht auf Messwerten des
        ''' jeweiligen Objektivs, und ohne Messwerte passiert ohnehin nichts. Pro Bild ist
        ''' sie im Werkzeug uebersteuerbar.</summary>
        Public Property LensCorrectionEnabled As Boolean = True
        Public Property LensAssignments As New List(Of LensAssignment)()

        Public Property LightroomPresets As New List(Of XmpPresetSettings)()
        Public Property LutPresets As New List(Of LutPresetSettings)()

        ' Immich-Anbindung (self-hosted Foto-Server). Der Baum blendet den Immich-Zweig nur ein,
        ' wenn Enabled=True und eine Server-URL hinterlegt ist. Der API-Key wird - wie bei den meisten
        ' self-hosted-Tools üblich - im Klartext in settings.json gehalten; die Datei liegt im
        ' Benutzerprofil (AppData/.config). Wer strengere Geheimnisverwaltung braucht, kann später auf
        ' einen plattformspezifischen Tresor umstellen (siehe ImmichService).
        Public Property ImmichEnabled As Boolean = False
        Public Property ImmichServerUrl As String = ""
        Public Property ImmichApiKey As String = ""
        Public Property ImmichStoreRatingInDescription As Boolean = False
        Public Property ImmichStoreTagsInDescription As Boolean = False
        ' Bearbeitete Immich-Bilder: Standard ist, ein neues Asset anzulegen (das Original bleibt
        ' unangetastet). Mit ImmichUpdateExistingAssets=True ersetzt eine Bearbeitung stattdessen das
        ' Quell-Asset - erst dann ist im Editor auch „Speichern" (statt nur „Speichern unter") möglich.
        Public Property ImmichUpdateExistingAssets As Boolean = False
        ' Löschen wirkt standardmäßig NICHT auf den Server: ein versehentliches Entf in der Galerie soll
        ' keine Bilder aus Immich entfernen. ImmichDeletePermanently umgeht zusätzlich den Immich-Papierkorb.
        Public Property ImmichAllowDelete As Boolean = False
        ''' Wo die Zeitleiste am rechten Galerierand erscheint: "All" (Immich UND Ordner),
        ''' "Immich" (nur Immich-Ansichten), "Folders" (nur Ordner-/Suchansichten), "Off".
        Public Property GalleryTimelineMode As String = "All"
        Public Property ImmichDeletePermanently As Boolean = False

        ''' <summary>Erkannte Personen als Stichworte zurueck auf den Immich-Server schreiben.
        '''
        ''' AB WERK AUS, und das ist keine Vorsicht ohne Grund: der Server gehoert dem Benutzer, und
        ''' wer dort etwas hinschreibt, aendert Daten ausserhalb dieses Programms. Wer wen auf einem
        ''' Foto erkannt hat, ist ausserdem eine Angabe fuer sich - sie soll nicht als Nebenwirkung
        ''' eines Suchlaufs den Rechner verlassen. Geschrieben werden nur BENANNTE Gruppen.</summary>
        Public Property ImmichWritePeopleTags As Boolean = False

        ' Zuletzt im Druckdialog gewählte Optionen. Sie gelten auch für das Zielformat PDF in
        ' „Speichern unter"/„Konvertieren nach", damit Drucken und PDF-Export dasselbe Seitenlayout
        ' liefern. Die Strings tragen die englischen Logikschlüssel, nie den übersetzten Anzeigetext.
        Public Property PrintPageSize As String = "A4"
        Public Property PrintLandscape As Boolean = False
        Public Property PrintMarginMm As Double = 10
        Public Property PrintFitMode As String = "Fit"
        Public Property PrintImagesPerPage As Integer = 1
        Public Property PrintShowCaption As Boolean = False
        Public Property PrintBorderless As Boolean = False

        ''' <summary>Die gespeicherten Druckoptionen als PrintOptions. Kontaktabzug-Einstellungen
        ''' (Bilder pro Seite, Unterschrift) reicht WriteSinglePagePdf bewusst nicht durch.</summary>
        Public Function ToPrintOptions() As PrintOptions
            Return New PrintOptions With {
                .PageSize = PrintPageSize,
                .Landscape = PrintLandscape,
                .MarginMm = PrintMarginMm,
                .FitMode = PrintFitMode,
                .ImagesPerPage = PrintImagesPerPage,
                .ShowCaption = PrintShowCaption,
                .Borderless = PrintBorderless
            }
        End Function
    End Class

    Public NotInheritable Class AppSettingsService
        Private Sub New()
        End Sub

        Private Shared ReadOnly SettingsDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")

        Private Shared ReadOnly SettingsPath As String =
            Path.Combine(SettingsDirectory, "settings.json")

        ''' Der zuletzt bekannte, bereits normalisierte Stand als JSON. Load liest daraus statt von der
        ''' Platte: die Einstellungen werden an 34 Stellen abgefragt, unter anderem aus Vorschaubild-Threads.
        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared _cachedJson As String = Nothing

        ''' Geschrieben wird verzögert und zusammengefasst. Sonst löste jedes Häkchen im Dialog ein
        ''' vollständiges Serialisieren samt Temporärdatei und Umbenennen aus. Flush erzwingt das Schreiben -
        ''' beim Programmende und immer dann, wenn ein Verlust der letzten Sekunde nicht hinnehmbar wäre.
        Private Const WriteDebounceMs As Integer = 1500
        Private Shared ReadOnly _writeLock As New Object()
        Private Shared _pendingJson As String = Nothing
        Private Shared _flushTimer As Timers.Timer = Nothing

        ''' Der zuletzt geöffnete Galerie-Ordner wird beim Navigieren nur gemerkt, nicht geschrieben. Ein
        ''' Ordnerklick soll gar nichts serialisieren; persistiert wird der Ordner gesammelt beim nächsten
        ''' Flush (Programmende oder ohnehin fälliger Schreibvorgang). Gelesen wird er nur beim Start.
        Private Shared ReadOnly _pendingFolderLock As New Object()
        Private Shared _pendingLastGalleryFolder As String = Nothing

        Shared Sub New()
            AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender As Object, e As EventArgs) Flush()
        End Sub

        Public Shared Function Load() As AppSettings
            Try
                Dim json As String
                Dim readError As Exception = Nothing
                SyncLock _cacheLock
                    If _cachedJson Is Nothing Then _cachedJson = ReadSettingsJson(readError)
                    json = _cachedJson
                End SyncLock

                ' Erst protokollieren, wenn _cachedJson steht: DiagnosticLogService.LogException fragt
                ' seinerseits Load nach EnableDiagnosticLogging. Aus ReadSettingsJson heraus zu
                ' protokollieren liefe deshalb in eine Endlosrekursion - SyncLock ist reentrant und
                ' hielte sie nicht auf.
                If readError IsNot Nothing Then DiagnosticLogService.LogException("Settings.Read", readError)

                If String.IsNullOrEmpty(json) Then Return New AppSettings()

                Dim settings = JsonSerializer.Deserialize(Of AppSettings)(json)
                If settings Is Nothing Then Return New AppSettings()

                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryTimelineMode = NormalizeGalleryTimelineMode(settings.GalleryTimelineMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.GalleryStartupCustomFolder = NormalizeFolderPath(settings.GalleryStartupCustomFolder)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.LastSaveAsTargetFolder = NormalizeFolderPath(settings.LastSaveAsTargetFolder)
                settings.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(settings.GalleryFilterFavorite)
                settings.GalleryFilterRatings = NormalizeGalleryFilterRatings(settings.GalleryFilterRatings)
                settings.GalleryFilterFileType = NormalizeGalleryFilterFileType(settings.GalleryFilterFileType)
                settings.ThemeMode = NormalizeThemeMode(settings.ThemeMode)
                settings.AccentColor = NormalizeAccentColor(settings.AccentColor)
                settings.StartupImageMode = NormalizeStartupImageMode(settings.StartupImageMode)
                settings.GalleryOpenTarget = NormalizeGalleryOpenTarget(settings.GalleryOpenTarget)
                settings.StartupNoImageMode = NormalizeStartupNoImageMode(settings.StartupNoImageMode)
                settings.LanguageMode = LocalizationService.NormalizeLanguageMode(settings.LanguageMode)
                settings.ThumbnailQuality = NormalizeThumbnailQuality(settings.ThumbnailQuality)
                settings.GalleryThumbnailMemoryCacheCapacity = NormalizeGalleryThumbnailMemoryCacheCapacity(settings.GalleryThumbnailMemoryCacheCapacity)
                settings.JpgSaveQuality = NormalizeJpgSaveQuality(settings.JpgSaveQuality)
                settings.DefaultSaveFormat = NormalizeDefaultSaveFormat(settings.DefaultSaveFormat)
                settings.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(settings.ViewerSlideshowIntervalSeconds)
                settings.EditorGridSize = NormalizeEditorGridSize(settings.EditorGridSize)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                ' Einmalige Übernahme: vor der Trennung galt der Viewer-Wert für beide Ansichten.
                If String.IsNullOrWhiteSpace(settings.EditorFitBehavior) Then settings.EditorFitBehavior = settings.ViewerFitBehavior
                settings.EditorFitBehavior = NormalizeViewerFitBehavior(settings.EditorFitBehavior)
                settings.EditorStartupTool = NormalizeEditorStartupTool(settings.EditorStartupTool)
                settings.EditorToolGroupOrder = NormalizeEditorToolGroupOrder(settings.EditorToolGroupOrder)
                settings.HiddenAdjustmentGroups = NormalizeHiddenAdjustmentGroups(settings.HiddenAdjustmentGroups)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.FontSizeOffset = NormalizeFontSizeOffset(settings.FontSizeOffset)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                settings.LastBatchRenamePattern = NormalizeBatchRenamePattern(settings.LastBatchRenamePattern)
                settings.LastBatchRenameStart = NormalizeBatchRenameStart(settings.LastBatchRenameStart)
                settings.LastBatchRenameStep = NormalizeBatchRenameStep(settings.LastBatchRenameStep)
                settings.LastBatchResizeWidth = NormalizeBatchResizeDimension(settings.LastBatchResizeWidth)
                settings.LastBatchResizeHeight = NormalizeBatchResizeDimension(settings.LastBatchResizeHeight)
                settings.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(settings.LastBatchResizeScalePercent)
                settings.LastBatchResizeInterpolation = NormalizeResizeInterpolationModeName(settings.LastBatchResizeInterpolation)
                settings.LastWatermarkPresetName = NormalizePresetName(settings.LastWatermarkPresetName)
                settings.WatermarkPresets = NormalizeWatermarkPresets(settings.WatermarkPresets)
                settings.AdjustmentPresets = NormalizeAdjustmentPresets(settings.AdjustmentPresets)
                settings.LightroomPresets = NormalizeXmpPresets(settings.LightroomPresets)
                settings.LutPresets = NormalizeLutPresets(settings.LutPresets)
                Return settings
            Catch ex As JsonException
                ' Kaputte Datei: der nächste Save überschriebe sie mit Standardwerten. Vorher zur
                ' Seite legen, damit Presets und gespeicherte Suchen von Hand zu retten sind.
                BackupUnreadableSettings()
                SyncLock _cacheLock
                    _cachedJson = ""
                End SyncLock
                Return New AppSettings()
            Catch
                Return New AppSettings()
            End Try
        End Function

        ''' Liest die Datei roh. Protokolliert selbst nichts - siehe Load.
        Private Shared Function ReadSettingsJson(ByRef readError As Exception) As String
            Try
                If Not File.Exists(SettingsPath) Then Return ""
                Return File.ReadAllText(SettingsPath)
            Catch ex As Exception
                readError = ex
                Return ""
            End Try
        End Function

        Private Shared Sub BackupUnreadableSettings()
            Try
                If Not File.Exists(SettingsPath) Then Return
                File.Move(SettingsPath, SettingsPath & ".corrupt", overwrite:=True)
            Catch
            End Try
        End Sub

        Public Shared Sub Save(settings As AppSettings)
            Try
                Directory.CreateDirectory(SettingsDirectory)
                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryTimelineMode = NormalizeGalleryTimelineMode(settings.GalleryTimelineMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.GalleryStartupCustomFolder = NormalizeFolderPath(settings.GalleryStartupCustomFolder)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.LastSaveAsTargetFolder = NormalizeFolderPath(settings.LastSaveAsTargetFolder)
                settings.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(settings.GalleryFilterFavorite)
                settings.GalleryFilterRatings = NormalizeGalleryFilterRatings(settings.GalleryFilterRatings)
                settings.GalleryFilterFileType = NormalizeGalleryFilterFileType(settings.GalleryFilterFileType)
                settings.ThemeMode = NormalizeThemeMode(settings.ThemeMode)
                settings.AccentColor = NormalizeAccentColor(settings.AccentColor)
                settings.StartupImageMode = NormalizeStartupImageMode(settings.StartupImageMode)
                settings.GalleryOpenTarget = NormalizeGalleryOpenTarget(settings.GalleryOpenTarget)
                settings.StartupNoImageMode = NormalizeStartupNoImageMode(settings.StartupNoImageMode)
                settings.LanguageMode = LocalizationService.NormalizeLanguageMode(settings.LanguageMode)
                settings.ThumbnailQuality = NormalizeThumbnailQuality(settings.ThumbnailQuality)
                settings.GalleryThumbnailMemoryCacheCapacity = NormalizeGalleryThumbnailMemoryCacheCapacity(settings.GalleryThumbnailMemoryCacheCapacity)
                settings.JpgSaveQuality = NormalizeJpgSaveQuality(settings.JpgSaveQuality)
                settings.DefaultSaveFormat = NormalizeDefaultSaveFormat(settings.DefaultSaveFormat)
                settings.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(settings.ViewerSlideshowIntervalSeconds)
                settings.EditorGridSize = NormalizeEditorGridSize(settings.EditorGridSize)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                ' Einmalige Übernahme: vor der Trennung galt der Viewer-Wert für beide Ansichten.
                If String.IsNullOrWhiteSpace(settings.EditorFitBehavior) Then settings.EditorFitBehavior = settings.ViewerFitBehavior
                settings.EditorFitBehavior = NormalizeViewerFitBehavior(settings.EditorFitBehavior)
                settings.EditorStartupTool = NormalizeEditorStartupTool(settings.EditorStartupTool)
                settings.EditorToolGroupOrder = NormalizeEditorToolGroupOrder(settings.EditorToolGroupOrder)
                settings.HiddenAdjustmentGroups = NormalizeHiddenAdjustmentGroups(settings.HiddenAdjustmentGroups)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.FontSizeOffset = NormalizeFontSizeOffset(settings.FontSizeOffset)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                settings.LastBatchRenamePattern = NormalizeBatchRenamePattern(settings.LastBatchRenamePattern)
                settings.LastBatchRenameStart = NormalizeBatchRenameStart(settings.LastBatchRenameStart)
                settings.LastBatchRenameStep = NormalizeBatchRenameStep(settings.LastBatchRenameStep)
                settings.LastBatchResizeWidth = NormalizeBatchResizeDimension(settings.LastBatchResizeWidth)
                settings.LastBatchResizeHeight = NormalizeBatchResizeDimension(settings.LastBatchResizeHeight)
                settings.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(settings.LastBatchResizeScalePercent)
                settings.LastBatchResizeInterpolation = NormalizeResizeInterpolationModeName(settings.LastBatchResizeInterpolation)
                settings.LastWatermarkPresetName = NormalizePresetName(settings.LastWatermarkPresetName)
                settings.WatermarkPresets = NormalizeWatermarkPresets(settings.WatermarkPresets)
                settings.AdjustmentPresets = NormalizeAdjustmentPresets(settings.AdjustmentPresets)
                settings.LightroomPresets = NormalizeXmpPresets(settings.LightroomPresets)
                settings.LutPresets = NormalizeLutPresets(settings.LutPresets)
                Dim json = JsonSerializer.Serialize(settings, New JsonSerializerOptions With {.WriteIndented = True})

                ' Der neue Stand gilt ab sofort für alle Leser; auf die Platte geht er gesammelt.
                SyncLock _cacheLock
                    _cachedJson = json
                End SyncLock
                ThumbnailCacheService.InvalidateSettingsCache()
                ScheduleWrite(json)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.Save", ex)
            End Try
        End Sub

        Private Shared Sub ScheduleWrite(json As String)
            SyncLock _writeLock
                _pendingJson = json
                If _flushTimer Is Nothing Then
                    _flushTimer = New Timers.Timer(WriteDebounceMs) With {.AutoReset = False}
                    AddHandler _flushTimer.Elapsed, Sub(sender As Object, e As Timers.ElapsedEventArgs) Flush()
                End If
                _flushTimer.Stop()
                _flushTimer.Start()
            End SyncLock
        End Sub

        ''' <summary>Schreibt einen ausstehenden Stand sofort auf die Platte. Wird beim Programmende gerufen
        ''' (ProcessExit) und darf jederzeit zusätzlich aufgerufen werden - ohne ausstehende Änderung tut sie
        ''' nichts.</summary>
        Public Shared Sub Flush()
            ' Erst den gemerkten Ordner in einen ausstehenden Stand überführen, dann diesen wegschreiben.
            CommitPendingLastGalleryFolder()

            Dim json As String
            SyncLock _writeLock
                json = _pendingJson
                _pendingJson = Nothing
                _flushTimer?.Stop()
            End SyncLock
            If json Is Nothing Then Return

            Try
                Directory.CreateDirectory(SettingsDirectory)
                ' Nicht direkt in settings.json schreiben: ein Absturz mitten im Schreiben hinterließe eine
                ' abgeschnittene Datei. Load würde die beim nächsten Start als unlesbar verwerfen - samt
                ' Wasserzeichen-Presets, gespeicherten Suchen und Theme. Erst vollständig danebenschreiben,
                ' dann ersetzen.
                Dim tempPath = SettingsPath & ".tmp"
                File.WriteAllText(tempPath, json)
                File.Move(tempPath, SettingsPath, overwrite:=True)
            Catch ex As Exception
                ' Volle Platte, fehlende Rechte: früher fiel das lautlos unter den Tisch und der Nutzer
                ' glaubte, seine Einstellung sei gespeichert.
                DiagnosticLogService.LogException("Settings.Flush", ex)
            End Try
        End Sub

        ''' <summary>Ändert genau ein paar Felder und speichert. Ersetzt das fünfzehnmal kopierte
        ''' Load-ändern-Save-Muster.</summary>
        Public Shared Sub Update(mutate As Action(Of AppSettings))
            If mutate Is Nothing Then Return
            Dim settings = Load()
            mutate(settings)
            Save(settings)
        End Sub

        Public Shared Function NormalizeThumbnailSize(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 260
            Return Math.Max(140, Math.Min(480, value))
        End Function

        Public Shared Function NormalizeThumbnailQuality(value As Integer) As Integer
            Return Math.Max(45, Math.Min(95, value))
        End Function

        Public Shared Function NormalizeGalleryThumbnailMemoryCacheCapacity(value As Integer) As Integer
            Return Math.Max(50, Math.Min(5000, value))
        End Function

        Public Shared Function NormalizeJpgSaveQuality(value As Integer) As Integer
            Return Math.Max(1, Math.Min(100, value))
        End Function

        Public Shared Function NormalizeBatchRenamePattern(value As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return "{name}_###"
            Return value.Trim()
        End Function

        Public Shared Function NormalizeBatchRenameStart(value As Integer) As Integer
            Return Math.Max(0, Math.Min(999999, value))
        End Function

        Public Shared Function NormalizeBatchRenameStep(value As Integer) As Integer
            Return Math.Max(1, Math.Min(999999, value))
        End Function

        Public Shared Function NormalizeBatchResizeDimension(value As Integer) As Integer
            Return Math.Max(0, Math.Min(100000, value))
        End Function

        Public Shared Function NormalizeBatchResizeScalePercent(value As Integer) As Integer
            Return Math.Max(0, Math.Min(1000, value))
        End Function

        Public Shared Function NormalizeResizeInterpolationModeName(value As String) As String
            Select Case If(value, "").Trim()
                Case "Nearest", "Bicubic"
                    Return value.Trim()
                Case Else
                    Return "Bilinear"
            End Select
        End Function

        Public Shared Function NormalizePresetName(value As String) As String
            Return If(value, "").Trim()
        End Function

        Public Shared Function NormalizeViewerSlideshowIntervalSeconds(value As Integer) As Integer
            Return Math.Max(1, Math.Min(30, value))
        End Function

        Public Shared Function NormalizeEditorGridSize(value As Integer) As Integer
            Return Math.Max(2, Math.Min(1000, value))
        End Function

        Public Shared Function NormalizeViewerFitBehavior(value As String) As String
            Select Case If(value, "").Trim()
                Case "OnlyWhenLarger"
                    Return "OnlyWhenLarger"
                Case Else
                    Return "Always"
            End Select
        End Function

        Public Shared Function NormalizeDefaultSaveFormat(value As String) As String
            Select Case If(value, "").Trim().ToUpperInvariant()
                Case "PNG" : Return "PNG"
                Case "WEBP" : Return "WEBP"
                Case "PDF" : Return "PDF"
                Case "FPX" : Return "FPX"
                Case Else : Return "JPG"
            End Select
        End Function

        Public Shared Function NormalizeEditorStartupTool(value As String) As String
            Select Case If(value, "").Trim()
                Case "Adjust"
                    Return "Adjust"
                Case Else
                    Return "Selection"
            End Select
        End Function

        ''' <summary>Die drei Werkzeuggruppen der Editor-Leiste, in der Reihenfolge, die als Vorgabe
        ''' gilt.</summary>
        Public Shared ReadOnly Property EditorToolGroupNames As String()
            Get
                Return {"Adjust", "Transform", "Tools"}
            End Get
        End Property

        ''' <summary>Die Anpassungsgruppen, die sich ausblenden lassen, mit ihrer Beschriftung.
        '''
        ''' EINE Liste fuer alles: den Einstellungsdialog, die Normierung und den Waechter. Die
        ''' Schluessel sind dieselben, unter denen der Editor sich auch den Auf- und Zuklapp-Zustand
        ''' merkt - ein zweiter Satz Namen daneben waere die erste Gelegenheit, dass beide
        ''' auseinanderlaufen.
        '''
        ''' NICHT dabei sind Gruppen, die IHR Werkzeug ausmachen: Zuschneiden, Maske, Auswahl,
        ''' Objekte, Zeichnen, Drehen und Bildgroesse. Sie auszublenden liesse ein leeres Werkzeug
        ''' zurueck, und das ist kein Aufraeumen mehr, sondern ein kaputter Zustand.</summary>
        Public Shared ReadOnly Property HideableAdjustmentGroups As (Key As String, Bezeichnung As String)()
            Get
                Return {
                    ("adjust-presets", "Anpassungen"),
                    ("light", "Licht"),
                    ("color", "Farbe"),
                    ("curve", "Tonwertkurve"),
                    ("hsl", "Farbmischer"),
                    ("color-grading", "Farbgradierung"),
                    ("calibration", "Kalibrierung"),
                    ("objektivkorrektur", "Objektivkorrektur"),
                    ("film-negative", "Filmnegativ"),
                    ("details", "Details"),
                    ("rauschen", "Rauschen"),
                    ("sharpen", "Schärfe"),
                    ("soften", "Weichzeichnen"),
                    ("bokeh", "Tiefen-Unschärfe"),
                    ("vignette", "Vignette"),
                    ("grain", "Körnung"),
                    ("frame", "Rahmen"),
                    ("filter", "Filter"),
                    ("lut", "LUT (.cube)"),
                    ("xmp-preset", "XMP-Preset"),
                    ("perspektive", "Perspektive"),
                    ("gitterverzerrung", "Gitterverzerrung"),
                    ("verformen", "Verformen"),
                    ("resize-canvas", "Leinwand"),
                    ("hintergrund", "Hintergrund")}
            End Get
        End Property

        ''' <summary>Welche Anpassungsgruppen zu welchem Werkzeug gehoeren - aber nur fuer die
        ''' Werkzeuge, die AUSSCHLIESSLICH aus ausblendbaren Gruppen bestehen.
        '''
        ''' Blendet man dort alle Gruppen aus, bliebe ein Werkzeug uebrig, das man anklicken kann und
        ''' das dann ein leeres Panel zeigt. Dann verschwindet es besser mit. Drehen und Bildgroesse
        ''' stehen bewusst NICHT hier: sie tragen je eine Gruppe, die sich nicht ausblenden laesst,
        ''' und koennen deshalb nie leer werden.</summary>
        Public Shared ReadOnly Property ToolGroups As (Werkzeug As String, Gruppen As String())()
            Get
                Return {
                    ("Adjust", New String() {"adjust-presets", "light", "curve", "objektivkorrektur", "film-negative"}),
                    ("Color", New String() {"color", "hsl", "color-grading", "calibration"}),
                    ("Details", New String() {"details", "rauschen", "sharpen", "soften", "bokeh"}),
                    ("Effects", New String() {"vignette", "grain", "frame"}),
                    ("Filters", New String() {"filter", "lut", "xmp-preset"})}
            End Get
        End Property

        ''' <summary>Nur bekannte Schluessel, jeder hoechstens einmal. Ein Schluessel, den es nicht
        ''' mehr gibt, verschwindet still - sonst bliebe eine Gruppe fuer immer versteckt, weil sie
        ''' einmal anders geheissen hat.</summary>
        Public Shared Function NormalizeHiddenAdjustmentGroups(value As String) As String
            Dim result As New List(Of String)()
            For Each part In If(value, "").Split(","c)
                Dim name = part.Trim()
                If name.Length = 0 Then Continue For
                Dim treffer = HideableAdjustmentGroups.FirstOrDefault(
                    Function(e) String.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase))
                If treffer.Key IsNot Nothing AndAlso Not result.Contains(treffer.Key) Then
                    result.Add(treffer.Key)
                End If
            Next
            Return String.Join(",", result)
        End Function

        ''' <summary>Sorgt dafür, dass die gespeicherte Reihenfolge IMMER eine vollständige
        ''' Permutation der drei Gruppen ist: Unbekanntes und Doppeltes fliegt raus, Fehlendes wird
        ''' in der Vorgabereihenfolge angehängt. Eine unvollständige Liste hätte eine ganze Gruppe
        ''' aus der Leiste verschwinden lassen - und mit ihr die Werkzeuge darin.</summary>
        Public Shared Function NormalizeEditorToolGroupOrder(value As String) As String
            Dim result As New List(Of String)()
            For Each part In If(value, "").Split(","c)
                Dim name = part.Trim()
                Dim treffer = EditorToolGroupNames.FirstOrDefault(
                    Function(e) String.Equals(e, name, StringComparison.OrdinalIgnoreCase))
                If treffer IsNot Nothing AndAlso Not result.Contains(treffer) Then result.Add(treffer)
            Next
            For Each name In EditorToolGroupNames
                If Not result.Contains(name) Then result.Add(name)
            Next
            Return String.Join(",", result)
        End Function

        ' Untergrenze -1: die kleinste Schrift der Oberfläche ist 9px (FP.Font.Label), bei -2 wäre sie
        ' 7px und damit unlesbar. Nach oben ist reichlich Luft, die Layouts sind flexibel.
        Public Shared Function NormalizeFontSizeOffset(value As Integer) As Integer
            Return Math.Max(-1, Math.Min(6, value))
        End Function

        Public Shared Function NormalizeApplicationScale(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 1.0
            Return Math.Max(1.0, Math.Min(2.5, Math.Round(value, 2)))
        End Function

        Public Shared Function NormalizeApplicationScaleScreen(value As String) As String
            value = If(value, "").Trim()
            If String.IsNullOrWhiteSpace(value) Then Return "HDMI-A-1"
            Return value.Replace(";"c, "_"c).Replace("="c, "_"c)
        End Function

        Public Shared Sub ApplyApplicationScaleEnvironment()
            ' AVALONIA_SCREEN_SCALE_FACTORS wirkt nur auf Avalonias X11-Backend. Unter Windows und
            ' macOS skaliert Avalonia nativ pro Monitor, die Einstellung wäre dort wirkungslos.
            If Not OperatingSystem.IsLinux() Then Return

            Dim settings = Load()
            Dim scale = NormalizeApplicationScale(settings.ApplicationScale)
            If scale <= 1.0001 Then
                Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", Nothing)
                Return
            End If

            Dim screen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
            Dim scaleText = scale.ToString("0.##", CultureInfo.InvariantCulture)
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", $"{screen}={scaleText}")
        End Sub

        Public Shared Function NormalizeGalleryTimelineMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Immich", "Folders", "Off"
                    Return If(value, "").Trim()
                Case Else
                    Return "All"
            End Select
        End Function

        Public Shared Function NormalizeGalleryViewMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "List"
                    Return "List"
                Case Else
                    Return "Grid"
            End Select
        End Function

        ''' <summary>Der Standard der Galerie-Sortierung, an EINER Stelle. Er ist der Anfangswert der
        ''' Einstellung und zugleich das Ziel des Zuruecksetzens ueber den Mausradklick - stuenden
        ''' beide getrennt da, liefen sie beim naechsten Umstellen auseinander.</summary>
        Public Const DefaultGallerySortMode As String = "Name"

        Public Const DefaultGallerySortAscending As Boolean = True

        Public Shared Function NormalizeGallerySortMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Size", "Type", "Rating", "Favorite",
                     "Width", "Height", "FileCreatedAt", "FileModifiedAt", "ExifDateTaken", "ExifDateModified",
                     "Camera", "Iso", "Aperture"
                    Return value.Trim()
                Case "Date" ' Alte Sortiereinstellung aus Versionen vor 0.4.0 - Bestandsnutzer nicht stillschweigend auf "Name" zurückfallen lassen.
                    Return "FileModifiedAt"
                Case Else
                    Return DefaultGallerySortMode
            End Select
        End Function

        Public Shared Function NormalizeThemeMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Light", "GrayDark", "GrayLight"
                    Return value.Trim()
                Case "System", "Gray"
                    Return "GrayDark"
                Case Else
                    Return "Dark"
            End Select
        End Function

        Public Shared Function NormalizeAccentColor(value As String) As String
            Select Case If(value, "").Trim().ToUpperInvariant()
                Case "#D97706"
                    Return "#FACC15"
                Case "#F08A1A", "#E74C3C", "#F03B88", "#8B5CF6", "#3B82F6", "#0891B2", "#0F766E", "#22C55E", "#FACC15"
                    Return value.Trim().ToUpperInvariant()
                Case Else
                    Return "#F08A1A"
            End Select
        End Function

        Public Shared Function NormalizeTransparencyBackgroundMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Solid"
                    Return "Solid"
                Case "None"
                    Return "None"
                Case Else
                    Return "Checkerboard"
            End Select
        End Function

        Public Shared Function NormalizeHexColor(value As String, fallback As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim trimmed = value.Trim()
            If Not System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$") Then Return fallback
            Return trimmed.ToUpperInvariant()
        End Function

        ''' <summary>Womit ein Bild aus der Galerie geoeffnet wird (Doppelklick und Eingabetaste).
        ''' Videos gehen unabhaengig davon immer in den Betrachter - der Editor kann sie nicht.</summary>
        Public Shared Function NormalizeGalleryOpenTarget(value As String) As String
            Select Case If(value, "").Trim()
                Case "Editor"
                    Return "Editor"
                Case Else
                    Return "Viewer"
            End Select
        End Function

        Public Shared Function NormalizeStartupImageMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Gallery", "Editor", "Fullscreen"
                    Return value.Trim()
                Case Else
                    Return "Viewer"
            End Select
        End Function

        Public Shared Function NormalizeStartupNoImageMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Viewer", "Editor"
                    Return value.Trim()
                Case Else
                    Return "Gallery"
            End Select
        End Function

        Public Shared Function NormalizeGalleryStartupFolderMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Pictures", "Last", "Custom", "Immich"
                    Return value.Trim()
                Case Else
                    Return "Pictures"
            End Select
        End Function

        Public Shared Function NormalizeFolderPath(value As String) As String
            Return If(value, "").Trim()
        End Function

        Public Shared Function NormalizeWindowDimension(value As Double, fallback As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value < 200 OrElse value > 10000 Then
                Return fallback
            End If
            Return Math.Round(value, 1)
        End Function

        Public Shared Function NormalizeSavedSearches(value As List(Of SavedSearchSettings)) As List(Of SavedSearchSettings)
            Dim result As New List(Of SavedSearchSettings)()
            For Each search In If(value, New List(Of SavedSearchSettings)())
                If search Is Nothing Then Continue For
                Dim name = If(search.Name, "").Trim()
                Dim textQuery = If(search.TextQuery, "").Trim()
                Dim rootFolder = NormalizeFolderPath(search.RootFolder)
                Dim favoriteMode = NormalizeSearchFavoriteMode(search.FavoriteMode)
                Dim ratingMin = Math.Max(-1, Math.Min(5, search.RatingMin))
                If String.IsNullOrWhiteSpace(name) Then
                    If Not String.IsNullOrWhiteSpace(textQuery) Then
                        name = textQuery
                    ElseIf favoriteMode = "Only" Then
                        name = "Favoriten"
                    ElseIf ratingMin >= 0 Then
                        name = If(ratingMin = 0, "Nicht bewertet", $"{ratingMin}+ Sterne")
                    Else
                        Continue For
                    End If
                End If
                result.Add(New SavedSearchSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(search.Id), Guid.NewGuid().ToString("N"), search.Id),
                    .Name = name,
                    .TextQuery = textQuery,
                    .RootFolder = rootFolder,
                    .IncludeSubfolders = search.IncludeSubfolders,
                    .FavoriteMode = favoriteMode,
                    .RatingMin = ratingMin
                })
            Next
            Return result
        End Function

        ''' <summary>Eine Vorlage ohne Namen oder ohne Rezept ist nicht anwendbar und fliegt raus -
        ''' sonst stünde ein Eintrag in der Liste, dessen Auswahl nichts täte.</summary>
        Public Shared Function NormalizeAdjustmentPresets(value As List(Of AdjustmentPresetSettings)) As List(Of AdjustmentPresetSettings)
            Dim result As New List(Of AdjustmentPresetSettings)()
            For Each preset In If(value, New List(Of AdjustmentPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim name = If(preset.Name, "").Trim()
                Dim recipe = If(preset.RecipeJson, "").Trim()
                If String.IsNullOrWhiteSpace(name) OrElse String.IsNullOrWhiteSpace(recipe) Then Continue For
                result.Add(New AdjustmentPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .RecipeJson = recipe
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Legt eine Vorlage an oder ERSETZT die gleichnamige - so wie bei den
        ''' Wasserzeichen-Vorlagen. Zwei Einträge mit demselben Namen wären in der Auswahlliste
        ''' nicht auseinanderzuhalten.</summary>
        Public Shared Sub SaveAdjustmentPreset(name As String, recipeJson As String)
            Dim sauber = If(name, "").Trim()
            Dim recipe = If(recipeJson, "").Trim()
            If String.IsNullOrWhiteSpace(sauber) OrElse String.IsNullOrWhiteSpace(recipe) Then Return
            Update(Sub(s)
                       Dim items = If(s.AdjustmentPresets, New List(Of AdjustmentPresetSettings)())
                       items.RemoveAll(Function(p) p IsNot Nothing AndAlso String.Equals(p.Name, sauber, StringComparison.OrdinalIgnoreCase))
                       items.Add(New AdjustmentPresetSettings With {.Name = sauber, .RecipeJson = recipe})
                       s.AdjustmentPresets = NormalizeAdjustmentPresets(items)
                       s.LastAdjustmentPresetName = sauber
                   End Sub)
        End Sub

        Public Shared Sub DeleteAdjustmentPreset(name As String)
            Dim sauber = If(name, "").Trim()
            If String.IsNullOrWhiteSpace(sauber) Then Return
            Update(Sub(s)
                       Dim items = If(s.AdjustmentPresets, New List(Of AdjustmentPresetSettings)())
                       items.RemoveAll(Function(p) p IsNot Nothing AndAlso String.Equals(p.Name, sauber, StringComparison.OrdinalIgnoreCase))
                       s.AdjustmentPresets = NormalizeAdjustmentPresets(items)
                       If String.Equals(s.LastAdjustmentPresetName, sauber, StringComparison.OrdinalIgnoreCase) Then
                           s.LastAdjustmentPresetName = If(s.AdjustmentPresets.FirstOrDefault()?.Name, "")
                       End If
                   End Sub)
        End Sub

        Public Shared Sub SaveLastAdjustmentPresetName(value As String)
            Update(Sub(s) s.LastAdjustmentPresetName = If(value, "").Trim())
        End Sub

        Public Shared Function NormalizeWatermarkPresets(value As List(Of WatermarkPresetSettings)) As List(Of WatermarkPresetSettings)
            Dim result As New List(Of WatermarkPresetSettings)()
            For Each preset In If(value, New List(Of WatermarkPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then Continue For
                result.Add(New WatermarkPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Text = If(preset.Text, "").Trim(),
                    .ImagePath = NormalizeFolderPath(preset.ImagePath),
                    .OffsetXPixels = Math.Max(-100000, Math.Min(100000, preset.OffsetXPixels)),
                    .OffsetYPixels = Math.Max(-100000, Math.Min(100000, preset.OffsetYPixels)),
                    .WidthPixels = Math.Max(1, Math.Min(100000, preset.WidthPixels)),
                    .HeightPixels = Math.Max(1, Math.Min(100000, preset.HeightPixels)),
                    .Anchor = NormalizeAnnotationAnchorName(preset.Anchor),
                    .LockAspect = preset.LockAspect,
                    .RotationDegrees = Math.Max(-180, Math.Min(180, preset.RotationDegrees)),
                    .Opacity = Math.Max(0, Math.Min(100, preset.Opacity)),
                    .FontFamily = If(preset.FontFamily, "Arial").Trim(),
                    .FontSizePixels = Math.Max(8, Math.Min(5000, preset.FontSizePixels)),
                    .FillColor = NormalizeHexColor(preset.FillColor, "#FFFFFFFF")
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Shared Function NormalizeAnnotationAnchorName(value As String) As String
            Select Case If(value, "").Trim()
                Case "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"
                    Return value.Trim()
                Case Else
                    Return "BottomRight"
            End Select
        End Function

        Public Shared Function NormalizeXmpPresets(value As List(Of XmpPresetSettings)) As List(Of XmpPresetSettings)
            Dim result As New List(Of XmpPresetSettings)()
            Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each preset In If(value, New List(Of XmpPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim presetPath = NormalizeFolderPath(preset.Path)
                If String.IsNullOrWhiteSpace(presetPath) OrElse Not seenPaths.Add(presetPath) Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = IO.Path.GetFileNameWithoutExtension(presetPath)
                result.Add(New XmpPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Path = presetPath
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Shared Function NormalizeLutPresets(value As List(Of LutPresetSettings)) As List(Of LutPresetSettings)
            Dim result As New List(Of LutPresetSettings)()
            Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each preset In If(value, New List(Of LutPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim presetPath = NormalizeFolderPath(preset.Path)
                If String.IsNullOrWhiteSpace(presetPath) OrElse Not seenPaths.Add(presetPath) Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = IO.Path.GetFileNameWithoutExtension(presetPath)
                result.Add(New LutPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Path = presetPath
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Shared Function NormalizeSearchFavoriteMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Only", "Not"
                    Return value.Trim()
                Case Else
                    Return "Any"
            End Select
        End Function

        Public Shared Sub SaveGalleryThumbnailSize(value As Double)
            Update(Sub(s) s.GalleryThumbnailSize = value)
        End Sub

        Public Shared Sub SaveGalleryViewMode(value As String)
            Update(Sub(s) s.GalleryViewMode = NormalizeGalleryViewMode(value))
        End Sub

        Public Shared Sub SaveGallerySort(sortMode As String, sortAscending As Boolean)
            Update(Sub(s)
                       s.GallerySortMode = sortMode
                       s.GallerySortAscending = sortAscending
                   End Sub)
        End Sub

        Public Shared Function NormalizeGalleryFilterFavorite(value As String) As String
            If String.Equals(value, "Only", StringComparison.OrdinalIgnoreCase) Then Return "Only"
            Return "All"
        End Function

        Public Shared Function NormalizeGalleryFilterFileType(value As String) As String
            If String.Equals(value, "Raw", StringComparison.OrdinalIgnoreCase) Then Return "Raw"
            If String.Equals(value, "NonRaw", StringComparison.OrdinalIgnoreCase) Then Return "NonRaw"
            Return "All"
        End Function

        Public Shared Function NormalizeGalleryFilterRatings(value As List(Of Integer)) As List(Of Integer)
            If value Is Nothing Then Return New List(Of Integer)()
            Return value.Where(Function(r) r >= 0 AndAlso r <= 5).Distinct().OrderBy(Function(r) r).ToList()
        End Function

        Public Shared Sub SaveGalleryFilters(favorite As String, ratings As IEnumerable(Of Integer), fileType As String)
            Update(Sub(s)
                       s.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(favorite)
                       s.GalleryFilterRatings = NormalizeGalleryFilterRatings(If(ratings, Enumerable.Empty(Of Integer)()).ToList())
                       s.GalleryFilterFileType = NormalizeGalleryFilterFileType(fileType)
                   End Sub)
        End Sub

        Public Shared Sub SaveGalleryStartupFolderMode(mode As String)
            Update(Sub(s) s.GalleryStartupFolderMode = mode)
        End Sub

        ''' Merkt sich den zuletzt geöffneten Galerie-Ordner nur im Speicher (kein Serialisieren, kein
        ''' Schreiben). Persistiert wird er beim nächsten Flush - siehe CommitPendingLastGalleryFolder.
        Public Shared Sub RememberLastGalleryFolder(folderPath As String)
            SyncLock _pendingFolderLock
                _pendingLastGalleryFolder = folderPath
            End SyncLock
        End Sub

        ''' Überführt den gemerkten Ordner in einen ausstehenden Schreibvorgang, falls er sich vom
        ''' gespeicherten Stand unterscheidet. Läuft vor jedem Flush; ohne gemerkten Ordner ein No-Op.
        Private Shared Sub CommitPendingLastGalleryFolder()
            Dim folder As String
            SyncLock _pendingFolderLock
                folder = _pendingLastGalleryFolder
                _pendingLastGalleryFolder = Nothing
            End SyncLock
            If folder Is Nothing Then Return

            If String.Equals(Load().LastGalleryFolder, NormalizeFolderPath(folder), StringComparison.Ordinal) Then Return
            Update(Sub(s) s.LastGalleryFolder = folder)
        End Sub

        Public Shared Sub SaveJpgSaveQuality(value As Integer)
            Update(Sub(s) s.JpgSaveQuality = value)
        End Sub

        Public Shared Sub SaveLastSaveAsTargetFolder(folderPath As String)
            Update(Sub(s) s.LastSaveAsTargetFolder = NormalizeFolderPath(folderPath))
        End Sub

        ''' <summary>Merkt das Zieldateinamen-Muster. Leer ist ein gueltiger Wert (Originalname
        ''' behalten) und wird deshalb NICHT auf einen Standard zurueckgebogen - anders als beim
        ''' Stapel-Umbenennen, wo ohne Muster gar nichts passieren wuerde.</summary>
        Public Shared Sub SaveLastTargetNamePattern(pattern As String)
            Update(Sub(s) s.LastTargetNamePattern = NormalizeTargetNamePattern(pattern))
        End Sub

        Public Shared Function NormalizeTargetNamePattern(value As String) As String
            Return If(value, "").Trim()
        End Function

        Public Shared Sub SaveCopiedAdjustments(recipeJson As String)
            Update(Sub(s) s.CopiedAdjustments = If(recipeJson, "").Trim())
        End Sub

        Public Shared Sub SaveLastBatchRenameSettings(pattern As String, start As Integer, stepValue As Integer)
            Update(Sub(s)
                       s.LastBatchRenamePattern = NormalizeBatchRenamePattern(pattern)
                       s.LastBatchRenameStart = NormalizeBatchRenameStart(start)
                       s.LastBatchRenameStep = NormalizeBatchRenameStep(stepValue)
                   End Sub)
        End Sub

        ''' <summary>Merkt sich die aktiven Sektionen des "Exportieren nach"-Dialogs.</summary>
        Public Shared Sub SaveExportToSections(useFilter As Boolean, useWatermark As Boolean, useResize As Boolean,
                                               watermarkKeepSize As Boolean)
            Update(Sub(s)
                       s.ExportToUseFilter = useFilter
                       s.ExportToUseWatermark = useWatermark
                       s.ExportToUseResize = useResize
                       s.ExportToWatermarkKeepSize = watermarkKeepSize
                   End Sub)
        End Sub

        Public Shared Sub SaveLastBatchRenamePattern(value As String)
            Dim settings = Load()
            SaveLastBatchRenameSettings(value, settings.LastBatchRenameStart, settings.LastBatchRenameStep)
        End Sub

        Public Shared Sub SaveLastBatchResizeSettings(width As Integer, height As Integer, scalePercent As Integer, lockAspect As Boolean, interpolation As ResizeInterpolationMode, noUpscale As Boolean,
                                                      longEdge As Boolean)
            Update(Sub(s)
                       s.LastBatchResizeWidth = NormalizeBatchResizeDimension(width)
                       s.LastBatchResizeHeight = NormalizeBatchResizeDimension(height)
                       s.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(scalePercent)
                       s.LastBatchResizeLockAspect = lockAspect
                       s.LastBatchResizeNoUpscale = noUpscale
                       s.LastBatchResizeInterpolation = interpolation.ToString()
                       s.LastBatchResizeLongEdge = longEdge
                   End Sub)
        End Sub

        Public Shared Sub SaveLastWatermarkPresetName(value As String)
            Update(Sub(s) s.LastWatermarkPresetName = NormalizePresetName(value))
        End Sub

        Public Shared Sub SaveViewerSlideshowIntervalSeconds(value As Integer)
            Update(Sub(s) s.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(value))
        End Sub

        Public Shared Sub SaveEditorInfoSidebarExpanded(value As Boolean)
            Update(Sub(s) s.EditorInfoSidebarExpanded = value)
        End Sub

        Public Shared Sub SaveBatchFilterOverwriteOriginals(value As Boolean)
            Update(Sub(s) s.BatchFilterOverwriteOriginals = value)
        End Sub

        Public Shared Sub SaveBatchWatermarkOverwriteOriginals(value As Boolean)
            Update(Sub(s) s.BatchWatermarkOverwriteOriginals = value)
        End Sub

        Public Shared Sub SaveBatchResizeOverwriteOriginals(value As Boolean)
            Update(Sub(s) s.BatchResizeOverwriteOriginals = value)
        End Sub

        ''' <summary>Die im Druckdialog zuletzt bestätigten Optionen. Sie gelten auch für das
        ''' Zielformat PDF in „Speichern unter"/„Konvertieren nach".</summary>
        Public Shared Sub SavePrintOptions(options As PrintOptions)
            If options Is Nothing Then Return
            Update(Sub(s)
                       s.PrintPageSize = options.PageSize
                       s.PrintLandscape = options.Landscape
                       s.PrintMarginMm = options.MarginMm
                       s.PrintFitMode = options.FitMode
                       s.PrintImagesPerPage = options.ImagesPerPage
                       s.PrintShowCaption = options.ShowCaption
                       s.PrintBorderless = options.Borderless
                   End Sub)
        End Sub

        Public Shared Sub SaveEditorSnapMarginPercent(value As Integer)
            Update(Sub(s) s.EditorSnapMarginPercent = Math.Max(0, Math.Min(20, value)))
        End Sub

        Public Shared Sub SaveEditorLayersPanelExpanded(value As Boolean)
            Update(Sub(s) s.EditorLayersPanelExpanded = value)
        End Sub

        Public Shared Sub SaveEditorExpanderState(key As String, expanded As Boolean)
            If String.IsNullOrEmpty(key) Then Return
            Update(Sub(s)
                       If s.EditorExpanderStates Is Nothing Then s.EditorExpanderStates = New Dictionary(Of String, Boolean)()
                       s.EditorExpanderStates(key) = expanded
                   End Sub)
        End Sub

        Public Shared Sub SaveEditorShowComparison(value As Boolean)
            Update(Sub(s) s.EditorShowComparison = value)
        End Sub

        Public Shared Sub SaveEditorShowRulers(value As Boolean)
            Update(Sub(s) s.EditorShowRulers = value)
        End Sub

        Public Shared Sub SaveEditorShowGrid(value As Boolean)
            Update(Sub(s) s.EditorShowGrid = value)
        End Sub

        Public Shared Sub SaveMainWindowMaximized(value As Boolean)
            Update(Sub(s) s.MainWindowMaximized = value)
        End Sub

        Public Shared Sub SaveMainWindowPlacement(left As Integer, top As Integer, width As Double, height As Double)
            Update(Sub(s)
                       s.MainWindowLeft = left
                       s.MainWindowTop = top
                       s.MainWindowWidth = NormalizeWindowDimension(width, s.MainWindowWidth)
                       s.MainWindowHeight = NormalizeWindowDimension(height, s.MainWindowHeight)
                   End Sub)
        End Sub
    End Class

End Namespace
