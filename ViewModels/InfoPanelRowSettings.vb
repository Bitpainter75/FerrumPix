Imports System
Imports System.Collections.Generic
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Was die Info-Leiste zeigen kann. Die ersten siebzehn sind ZEILEN des Reiters
    ''' "Allgemein", die letzten drei ganze BEREICHE der Leiste - sie stehen unter dem Reiter und
    ''' gelten auch bei mehreren markierten Bildern.</summary>
    Public Enum InfoPanelRow
        DateTaken = 0
        Camera = 1
        Lens = 2
        Aperture = 3
        ShutterSpeed = 4
        Iso = 5
        FocalLength = 6
        Dimensions = 7
        Megapixels = 8
        AspectRatio = 9
        ColorSpace = 10
        FileSize = 11
        FileCreated = 12
        FileModified = 13
        FolderPath = 14
        Place = 15
        Copyright = 16
        Rating = 17
        ColorLabel = 18
        Tags = 19
    End Enum

    ''' <summary>
    ''' Welche Zeilen der Reiter "Allgemein" zeigt - eine Wahl des Benutzers, anwendungsweit.
    '''
    ''' <para>Warum statisch, und warum ueberhaupt eine eigene Klasse: die Info-Leiste ist EIN
    ''' Steuerelement mit drei Datenkontexten (Galerie, Betrachter, Editor), und alle drei
    ''' <see cref="InfoPanelViewModel"/> entstehen beim Programmstart. Jede Instanz die Einstellung
    ''' einmal lesen zu lassen liefe nach dem ersten Umschalten auseinander - genau der Fehler, den
    ''' <see cref="ScopeSelectionViewModel"/> schon einmal hatte. Gelesen wird die Datei deshalb
    ''' beim Laden der Klasse und danach nur noch, wenn der Einstellungsdialog etwas aendert; eine
    ''' Abfrage je Bindung waere sonst ein Deserialisieren der ganzen Einstellungsdatei, und die
    ''' Sichtbarkeit wird pro Bild und pro Zeile abgefragt.</para>
    '''
    ''' <para>Das Ereignis haelt die Instanzen am Leben, solange die Anwendung laeuft. Das ist hier
    ''' unbedenklich und Absicht: es gibt genau drei, alle im MainWindowViewModel angelegt und so
    ''' langlebig wie das Fenster. Wer eine vierte, kurzlebige anlegt, muss sich abmelden.</para>
    ''' </summary>
    Public NotInheritable Class InfoPanelRowSettings

        Private Sub New()
        End Sub

        Private Shared ReadOnly _visible As New Dictionary(Of InfoPanelRow, Boolean)()

        ''' <summary>Meldet jede Aenderung an alle Leisten. Ohne sie zeigte nur die Ansicht, die
        ''' gerade auf dem Schirm steht, den neuen Stand - die anderen beiden erst beim naechsten
        ''' Bildwechsel.</summary>
        Public Shared Event Changed()

        Shared Sub New()
            ReadFrom(AppSettingsService.Load())
        End Sub

        ''' <summary>Die Zeilen des Reiters "Allgemein" in ihrer ANZEIGEREIHENFOLGE. Ein Ort fuer die
        ''' Reihenfolge: die Leiste zeichnet sie, der Einstellungsdialog listet sie, und beide sollen
        ''' dieselbe Abfolge zeigen. Der Ordner steht vorn, weil er in der Leiste unter dem
        ''' Dateinamen steht; Ort und Urheberrecht schliessen die Liste ab.</summary>
        Public Shared ReadOnly Property AllRows As IReadOnlyList(Of InfoPanelRow) =
            New InfoPanelRow() {InfoPanelRow.FolderPath,
                                InfoPanelRow.DateTaken, InfoPanelRow.Camera, InfoPanelRow.Lens,
                                InfoPanelRow.Aperture, InfoPanelRow.ShutterSpeed, InfoPanelRow.Iso,
                                InfoPanelRow.FocalLength, InfoPanelRow.Dimensions,
                                InfoPanelRow.Megapixels, InfoPanelRow.AspectRatio,
                                InfoPanelRow.ColorSpace, InfoPanelRow.FileSize,
                                InfoPanelRow.FileCreated, InfoPanelRow.FileModified,
                                InfoPanelRow.Place, InfoPanelRow.Copyright}

        ''' <summary>Die BEREICHE unter dem Reiter. Sie stehen im Dialog getrennt von den Zeilen,
        ''' weil sie etwas anderes sind: nicht Angaben ueber das Bild, sondern Bedienung - und sie
        ''' gelten auch bei mehreren markierten Bildern, wo es den Reiter gar nicht gibt.</summary>
        Public Shared ReadOnly Property AllSections As IReadOnlyList(Of InfoPanelRow) =
            New InfoPanelRow() {InfoPanelRow.Rating, InfoPanelRow.ColorLabel, InfoPanelRow.Tags}

        ''' <summary>Zeilen und Bereiche zusammen - fuer alles, was den ganzen Bestand braucht.</summary>
        Public Shared ReadOnly Property AllEntries As IReadOnlyList(Of InfoPanelRow) =
            AllRows.Concat(AllSections).ToList()

        Public Shared Function IsVisible(row As InfoPanelRow) As Boolean
            Dim value As Boolean
            Return If(_visible.TryGetValue(row, value), value, True)
        End Function

        ''' <summary>Uebernimmt eine Wahl aus dem Einstellungsdialog: erst schreiben, dann melden.
        ''' Umgekehrt zeichnete die Leiste noch mit dem alten Stand.</summary>
        Public Shared Sub SetVisible(row As InfoPanelRow, visible As Boolean)
            If IsVisible(row) = visible Then Return
            _visible(row) = visible
            AppSettingsService.Update(Sub(s) WriteTo(s, row, visible))
            RaiseEvent Changed()
        End Sub

        Private Shared Sub ReadFrom(settings As AppSettings)
            If settings Is Nothing Then Return
            _visible(InfoPanelRow.DateTaken) = settings.InfoPanelShowDateTaken
            _visible(InfoPanelRow.Camera) = settings.InfoPanelShowCamera
            _visible(InfoPanelRow.Lens) = settings.InfoPanelShowLens
            _visible(InfoPanelRow.Aperture) = settings.InfoPanelShowAperture
            _visible(InfoPanelRow.ShutterSpeed) = settings.InfoPanelShowShutterSpeed
            _visible(InfoPanelRow.Iso) = settings.InfoPanelShowIso
            _visible(InfoPanelRow.FocalLength) = settings.InfoPanelShowFocalLength
            _visible(InfoPanelRow.Dimensions) = settings.InfoPanelShowDimensions
            _visible(InfoPanelRow.Megapixels) = settings.InfoPanelShowMegapixels
            _visible(InfoPanelRow.AspectRatio) = settings.InfoPanelShowAspectRatio
            _visible(InfoPanelRow.ColorSpace) = settings.InfoPanelShowColorSpace
            _visible(InfoPanelRow.FileSize) = settings.InfoPanelShowFileSize
            _visible(InfoPanelRow.FileCreated) = settings.InfoPanelShowFileCreated
            _visible(InfoPanelRow.FileModified) = settings.InfoPanelShowFileModified
            _visible(InfoPanelRow.FolderPath) = settings.InfoPanelShowFolderPath
            _visible(InfoPanelRow.Place) = settings.InfoPanelShowPlace
            _visible(InfoPanelRow.Copyright) = settings.InfoPanelShowCopyright
            _visible(InfoPanelRow.Rating) = settings.InfoPanelShowRating
            _visible(InfoPanelRow.ColorLabel) = settings.InfoPanelShowColorLabel
            _visible(InfoPanelRow.Tags) = settings.InfoPanelShowTags
        End Sub

        Private Shared Sub WriteTo(settings As AppSettings, row As InfoPanelRow, visible As Boolean)
            If settings Is Nothing Then Return
            Select Case row
                Case InfoPanelRow.DateTaken : settings.InfoPanelShowDateTaken = visible
                Case InfoPanelRow.Camera : settings.InfoPanelShowCamera = visible
                Case InfoPanelRow.Lens : settings.InfoPanelShowLens = visible
                Case InfoPanelRow.Aperture : settings.InfoPanelShowAperture = visible
                Case InfoPanelRow.ShutterSpeed : settings.InfoPanelShowShutterSpeed = visible
                Case InfoPanelRow.Iso : settings.InfoPanelShowIso = visible
                Case InfoPanelRow.FocalLength : settings.InfoPanelShowFocalLength = visible
                Case InfoPanelRow.Dimensions : settings.InfoPanelShowDimensions = visible
                Case InfoPanelRow.Megapixels : settings.InfoPanelShowMegapixels = visible
                Case InfoPanelRow.AspectRatio : settings.InfoPanelShowAspectRatio = visible
                Case InfoPanelRow.ColorSpace : settings.InfoPanelShowColorSpace = visible
                Case InfoPanelRow.FileSize : settings.InfoPanelShowFileSize = visible
                Case InfoPanelRow.FileCreated : settings.InfoPanelShowFileCreated = visible
                Case InfoPanelRow.FileModified : settings.InfoPanelShowFileModified = visible
                Case InfoPanelRow.FolderPath : settings.InfoPanelShowFolderPath = visible
                Case InfoPanelRow.Place : settings.InfoPanelShowPlace = visible
                Case InfoPanelRow.Copyright : settings.InfoPanelShowCopyright = visible
                Case InfoPanelRow.Rating : settings.InfoPanelShowRating = visible
                Case InfoPanelRow.ColorLabel : settings.InfoPanelShowColorLabel = visible
                Case InfoPanelRow.Tags : settings.InfoPanelShowTags = visible
            End Select
        End Sub

    End Class

End Namespace
