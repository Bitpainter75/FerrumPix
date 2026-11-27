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
            If item Is Nothing OrElse item.IsFolder Then Return False
            Return TileDownloadItems(item).Count > 1
        End Function

        ''' <summary>Der Klick auf das Abzeichen "Herunterladen" einer Kachel. Derselbe Weg wie im
        ''' Kontextmenue (MenuCommands.BrowserDownload, sofortiger Start, mehrere Dateien als EIN
        ''' ZIP samt Beistelldateien, die Dateien wie beim Kopieren ueber GetPathsForTransfer).
        ''' Ausserhalb des Browsers und an einem Element ohne Datei geschieht nichts. Liefert, ob
        ''' der Weg angestossen wurde.</summary>
        Public Function DownloadFromTile(item As ImageItem) As Boolean
            If item Is Nothing Then Return False
            If item.IsFolder Then
                ' Eine Ordnerkachel meint immer nur ihren Ordner, auch in einer Auswahl.
                If Not BrowserBridge.IsActive OrElse Not item.IsFolderDownloadable Then Return False
                Dim ignored = DownloadFolderAsync(item.FilePath)
                Return True
            End If
            If Not item.IsDownloadable Then Return False
            Dim command = MenuCommands.BrowserDownload(Function() GetPathsForTransfer(TileDownloadItems(item)))
            If command Is Nothing Then Return False
            command.Execute(Nothing)
            Return True
        End Function

        ''' <summary>Ab so vielen Dateien fragt "Herunterladen" an einem Ordner vorher nach. Im
        ''' Pruefstand herabsetzbar.</summary>
        Friend Shared Property FolderDownloadConfirmFiles As Integer = 5000

        ''' <summary>Ab so vielen Bytes fragt "Herunterladen" an einem Ordner vorher nach.</summary>
        Friend Shared Property FolderDownloadConfirmBytes As Long = 2L * 1024 * 1024 * 1024

        ''' <summary>"Herunterladen" im Kontextmenue an GENAU EINER Ordnerkachel: der Ordner als
        ''' ZIP (<see cref="DownloadFolderAsync"/>). Liefert, ob das Menue damit erledigt ist; bei
        ''' jeder anderen Auswahl gehen die Dateien den gewohnten Weg.</summary>
        Public Function DownloadContextFolder() As Boolean
            Dim items = ContextItems
            If items Is Nothing OrElse items.Count <> 1 Then Return False
            Dim item = items(0)
            If item Is Nothing OrElse Not item.IsFolder OrElse Not item.IsFolderDownloadable Then Return False
            Dim ignored = DownloadFolderAsync(item.FilePath)
            Return True
        End Function

        ''' <summary>Laedt einen Ordner samt Unterordnern als EIN ZIP mit seinem Namen herunter
        ''' (BrowserBridge.OfferFolderDownload, sofortiger Start). Vorher wird im Hintergrund
        ''' gezaehlt, aber nur bis zu einer Grenze: liegt nichts darin, kommt eine Meldung statt
        ''' eines leeren ZIP; sind es mehr als <see cref="FolderDownloadConfirmFiles"/> Dateien oder
        ''' mehr als <see cref="FolderDownloadConfirmBytes"/>, fragt eine Rueckfrage mit dem, was
        ''' bis dahin gezaehlt ist. Liefert die Kennung des Angebots oder Nothing.</summary>
        Public Async Function DownloadFolderAsync(folder As String) As Task(Of String)
            Try
                If Not BrowserBridge.IsActive OrElse Not BrowserBridge.CanDownloadFolder(folder) Then Return Nothing
                Dim maxFiles = FolderDownloadConfirmFiles
                Dim maxBytes = FolderDownloadConfirmBytes
                Dim survey = Await Task.Run(Function() BrowserBridge.SurveyDownloadFolder(folder, maxFiles, maxBytes))
                Dim name = BrowserBridge.FolderDisplayName(folder)
                If survey.Files = 0 Then
                    If _mainVm IsNot Nothing Then
                        Await _mainVm.ShowMessageAsync(LocalizationService.T("Herunterladen"),
                            String.Format(LocalizationService.T("In diesem Ordner liegen keine Dateien zum Herunterladen: {0}"), name))
                    End If
                    Return Nothing
                End If
                If survey.Exceeded Then
                    ' Ohne Dialog keine Rueckfrage, und ohne Antwort kein grosser Download.
                    If _mainVm Is Nothing Then Return Nothing
                    Dim confirmed = Await _mainVm.ShowConfirmAsync(LocalizationService.T("Ordner herunterladen?"),
                        String.Format(LocalizationService.T("Der Ordner {0} enthält mindestens {1} Dateien mit zusammen {2}. Er wird als ein ZIP heruntergeladen, das kann lange dauern."),
                                      name, survey.Files, CardImportService.FormatBytes(survey.Bytes)),
                        "Herunterladen", "Abbrechen")
                    If Not confirmed Then Return Nothing
                End If
                Return BrowserBridge.OfferFolderDownload(folder, startNow:=True)
            Catch ex As Exception
                ' Absicherung: der Aufruf kommt aus Klick und Menue ohne Await.
                DiagnosticLogService.LogException("Gallery.DownloadFolder", ex)
                Return Nothing
            End Try
        End Function
    End Class
End Namespace
