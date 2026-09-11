Imports System
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>G'MIC als Rundweg aus dem Editor: das gerenderte Bild geht als PNG an gmic_qt, das
    ''' Ergebnis kommt deckungsgleich als Bildebene zurueck. Ein eigenes Vorhaben und NICHT Teil von
    ''' "Öffnen mit" - dort gibt es entschieden keinen Rueckweg.
    '''
    ''' Beide Dateien liegen im Asset-Ordner dieser Editor-Sitzung, ausserhalb des Fotobestands.
    ''' Das ist kein Schreibvorgang dort, und die Ebene behaelt ihre Datei bis zum Dokumentwechsel,
    ''' genau wie eine eingefuegte Auswahl. In den Bestand kommt das Ergebnis erst durch Speichern.
    '''
    ''' Das gerenderte Bild enthaelt alles, was man sieht, auch die Objekte. Die Ebene darueber
    ''' zeigt sie also eingebacken, darunter bleiben sie veraenderbar liegen.</summary>
    Partial Public Class EditorViewModel

        Private _editWithGmicCommand As ICommand
        Private _gmicRunning As Boolean

        Public ReadOnly Property EditWithGmicCommand As ICommand
            Get
                If _editWithGmicCommand Is Nothing Then
                    _editWithGmicCommand = New DelegateCommand(Sub()
                                                                   Dim ignored = EditWithGmicAsync()
                                                               End Sub)
                End If
                Return _editWithGmicCommand
            End Get
        End Property

        Private Async Function EditWithGmicAsync() As Task
            ' Zwei offene G'MIC-Fenster auf demselben Dokument brächten zwei Ebenen, von denen
            ' keine weiss, dass es die andere gibt.
            If _gmicRunning Then
                StatusText = LocalizationService.T("G'MIC ist bereits geöffnet")
                Return
            End If
            Dim documentPath = _currentImagePath
            Dim sourcePath = RenderSourcePath
            If String.IsNullOrWhiteSpace(documentPath) OrElse String.IsNullOrWhiteSpace(sourcePath) Then Return
            If Not GmicService.IsAvailable Then
                StatusText = LocalizationService.T("G'MIC wurde nicht gefunden")
                Return
            End If

            _gmicRunning = True
            Try
                Dim inputPath = CreateSelectionAssetTempPath("gmic-input")
                Dim outputPath = CreateSelectionAssetTempPath("gmic")
                Dim adjustments = GetCurrentAdjustments()
                StatusText = LocalizationService.T("Bild wird für G'MIC vorbereitet")
                ' Derselbe Voll-Render wie beim Kopieren des ganzen Bildes: Quellaufloesung, alle
                ' bestaetigten Anpassungen, Masken und Objekte.
                Dim rendered = Await Task.Run(Function() ImageProcessor.SaveImage(sourcePath, inputPath, adjustments, 100,
                                                                                  preserveMetadata:=False,
                                                                                  workingFull:=CloneWorkingFullForRender()))
                If Not rendered Then
                    StatusText = LocalizationService.T("Das Bild konnte nicht an G'MIC übergeben werden")
                    Return
                End If

                StatusText = LocalizationService.T("G'MIC ist geöffnet")
                Dim started = Await GmicService.RunAsync(inputPath, outputPath)
                Try
                    If File.Exists(inputPath) Then File.Delete(inputPath)
                Catch
                End Try
                If Not started Then
                    StatusText = LocalizationService.T("G'MIC konnte nicht gestartet werden")
                    Return
                End If

                ' Waehrend das Fenster offen war, kann ein anderes Bild geoeffnet worden sein. Das
                ' Ergebnis gehoert zum alten und wird nicht auf das neue gelegt.
                If Not String.Equals(documentPath, _currentImagePath, StringComparison.Ordinal) Then
                    StatusText = LocalizationService.T("Das G'MIC-Ergebnis wurde verworfen, inzwischen ist ein anderes Bild geöffnet")
                    Return
                End If
                If Not File.Exists(outputPath) Then
                    StatusText = LocalizationService.T("G'MIC wurde ohne Ergebnis beendet")
                    Return
                End If

                ' Ueber das ganze Bild, wie eine an Ort und Stelle eingefuegte Auswahl. Liefert ein
                ' Filter ein anderes Seitenverhaeltnis, wird das Ergebnis auf das Bild gezogen.
                AddSelectionImageAnnotationAt(outputPath, 0, 0, 100, 100, "G'MIC")
                NameHistoryStep(LocalizationService.T("G'MIC angewendet"))
                StatusText = LocalizationService.T("Das G'MIC-Ergebnis liegt als Ebene über dem Bild")
            Finally
                _gmicRunning = False
            End Try
        End Function

    End Class

End Namespace
