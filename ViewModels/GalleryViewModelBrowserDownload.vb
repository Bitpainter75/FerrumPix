Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ' Das Ziel "Herunterladen" der Stapeldialoge im Browser (Exportieren nach, Anpassungen anwenden,
    ' Bildgroesse, Wasserzeichen, Konvertieren). Geschrieben wird wie in einen Ordner, nur in einen
    ' eigenen der Bruecke im Datenordner (BrowserBridge.CreateDownloadFolder); danach geht EIN Angebot
    ' an die Seite, mit sofortigem Start, mehrere Dateien als ZIP. Der Ordner gehoert dem Angebot und
    ' geht mit ihm (BrowserBridgeDownloads.vb). Im Bestand entsteht dabei nichts, auch keine Zeile im
    ' Katalog: die Katalogdaten wandern nicht mit, die Datei liegt ja nur bis zum Download dort.
    Partial Public Class GalleryViewModel

        ''' <summary>Ein Stapel mit dem Ziel "Herunterladen": der Ordner, in den er schreibt, und
        ''' was dort gelungen ist.</summary>
        Private NotInheritable Class BrowserDownloadRun
            Public ReadOnly Folder As String
            Private ReadOnly _written As New List(Of String)()

            Public Sub New(folder As String)
                Me.Folder = folder
            End Sub

            ''' <summary>Der Schreiber, der sich jedes gelungene Ziel merkt. Er laeuft auf
            ''' Hintergrundfaeden, daher unter der Sperre.</summary>
            Public Function Wrap(writer As Func(Of String, String, Boolean)) As Func(Of String, String, Boolean)
                Return Function(source As String, target As String)
                           Dim saved = writer(source, target)
                           If saved Then
                               SyncLock _written
                                   _written.Add(target)
                               End SyncLock
                           End If
                           Return saved
                       End Function
            End Function

            ''' <summary>Keine Katalogdaten fuer eine Datei, die nur bis zum Download liegt.</summary>
            Public Shared ReadOnly Property NoMetaCopy As CatalogMetaCopyOptions =
                New CatalogMetaCopyOptions With {.CopyRating = False, .CopyFavorite = False, .CopyColorLabel = False, .CopyKeywords = False}

            ''' <summary>Bietet an, was entstanden ist, und gibt den Ordner an das Angebot ab; ohne
            ''' Ergebnis wird er geloescht.</summary>
            Public Sub Finish()
                Dim files As List(Of String)
                SyncLock _written
                    files = _written.ToList()
                End SyncLock
                BrowserBridge.OfferDownloadFolder(Folder, files)
            End Sub
        End Class

        ''' <summary>Bereitet das Ziel "Herunterladen" vor, wenn es gewaehlt ist. Proceed False: der
        ''' Stapel endet hier (kein Ordner oder kein Platz, die Meldung ist schon gezeigt). Run ist
        ''' Nothing bei jedem anderen Ziel.</summary>
        Private Async Function BeginBrowserDownloadAsync(target As String, title As String,
                                                         sources As IEnumerable(Of String)) As Task(Of (Proceed As Boolean, Run As BrowserDownloadRun))
            If Not String.Equals(target, MainWindowViewModel.SaveAsTargetDownload, StringComparison.OrdinalIgnoreCase) Then Return (True, Nothing)
            Dim folder = BrowserBridge.CreateDownloadFolder()
            If folder Is Nothing Then
                Await _mainVm.ShowMessageAsync(title, LocalizationService.T("Der Ordner für den Download ließ sich im Datenordner nicht anlegen."))
                Return (False, Nothing)
            End If
            Dim problem = BrowserBridge.DownloadSpaceProblem(folder, sources)
            If problem IsNot Nothing Then
                BrowserBridge.DiscardDownloadFolder(folder)
                Await _mainVm.ShowMessageAsync(title, problem)
                Return (False, Nothing)
            End If
            Return (True, New BrowserDownloadRun(folder))
        End Function

        ''' <summary>Die Elemente, die das Abzeichen "Herunterladen" einer Kachel meint: dieselben
        ''' wie "Herunterladen" im Kontextmenue an dieser Kachel (ContextTarget.Affected). Liegt die
        ''' Kachel in einer Auswahl mehrerer Bilder, die ganze Auswahl, sonst nur sie. Anders als der
        ''' Rechtsklick waehlt das Abzeichen dabei nichts aus.</summary>
        Public Function TileDownloadItems(item As ImageItem) As IList(Of ImageItem)
            If item Is Nothing Then Return New List(Of ImageItem)()
            Return Global.FerrumPix.Controls.ContextTarget.Affected(item, SelectedItems, Nothing)
        End Function

        ''' <summary>Meint das Abzeichen dieser Kachel mehr als ein Bild? Dann heisst sein Hinweis
        ''' "Auswahl herunterladen".</summary>
        Public Function TileDownloadsSelection(item As ImageItem) As Boolean
            Return TileDownloadItems(item).Count > 1
        End Function

        ''' <summary>Der Klick auf das Abzeichen "Herunterladen" einer Kachel. Derselbe Weg wie im
        ''' Kontextmenue (MenuCommands.BrowserDownload, sofortiger Start, mehrere Dateien als EIN
        ''' ZIP samt Beistelldateien, die Dateien wie beim Kopieren ueber GetPathsForTransfer).
        ''' Ausserhalb des Browsers und an einem Element ohne Datei geschieht nichts. Liefert, ob
        ''' der Weg angestossen wurde.</summary>
        Public Function DownloadFromTile(item As ImageItem) As Boolean
            If item Is Nothing OrElse Not item.IsDownloadable Then Return False
            Dim command = MenuCommands.BrowserDownload(Function() GetPathsForTransfer(TileDownloadItems(item)))
            If command Is Nothing Then Return False
            command.Execute(Nothing)
            Return True
        End Function
    End Class
End Namespace
