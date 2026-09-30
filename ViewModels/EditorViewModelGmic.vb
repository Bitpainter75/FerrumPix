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

        Private _editLayersWithGmicCommand As ICommand

        ''' <summary>Das Kontextmenue der Ebene: die markierten Ebenen gehen gerastert an G'MIC, das
        ''' Ergebnis kommt als neue Ebene darueber. Der Eintrag der Buehne und der Fussleiste schickt
        ''' dagegen immer das ganze Bild.</summary>
        Public ReadOnly Property EditLayersWithGmicCommand As ICommand
            Get
                If _editLayersWithGmicCommand Is Nothing Then
                    _editLayersWithGmicCommand = New DelegateCommand(Sub()
                                                                         Dim ignored = EditLayersWithGmicAsync()
                                                                     End Sub)
                End If
                Return _editLayersWithGmicCommand
            End Get
        End Property

        ''' <summary>Gibt es Ebenen mit Pixeln oder Inhalt, die hinausgehen koennten, und G'MIC dazu?
        ''' Eine mitmarkierte Korrekturebene sperrt den Eintrag: sie traegt keine Pixel, nur eine
        ''' Wirkung auf das Darunter, und ginge sonst still verloren, waehrend die Beschriftung von
        ''' allen markierten Ebenen spricht.</summary>
        Public ReadOnly Property CanEditLayersWithGmic As Boolean
            Get
                If SelectedAdjustmentLayers.Count > 0 Then Return False
                Return GmicService.IsAvailable AndAlso SelectedAnnotations.Any(Function(a) a IsNot Nothing)
            End Get
        End Property

        ''' <summary>Das GANZE Bild: derselbe Voll-Render wie beim Kopieren des ganzen Bildes
        ''' (Quellaufloesung, alle bestaetigten Anpassungen, Masken und Objekte), das Ergebnis
        ''' deckungsgleich ueber das ganze Bild.</summary>
        Private Async Function EditWithGmicAsync() As Task
            Dim sourcePath = RenderSourcePath
            If String.IsNullOrWhiteSpace(sourcePath) Then Return
            Dim adjustments = GetCurrentAdjustments()
            Await RunGmicRoundTripAsync(
                Function(inputPath) Task.Run(Function() ImageProcessor.SaveImage(sourcePath, inputPath, adjustments, 100,
                                                                                preserveMetadata:=False,
                                                                                workingFull:=CloneWorkingFullForRender())),
                Sub(outputPath)
                    ' Ueber das ganze Bild, wie eine an Ort und Stelle eingefuegte Auswahl. Liefert ein
                    ' Filter ein anderes Seitenverhaeltnis, wird das Ergebnis auf das Bild gezogen.
                    AddSelectionImageAnnotationAt(outputPath, 0, 0, 100, 100, "G'MIC")
                    NameHistoryStep(LocalizationService.T("G'MIC angewendet"))
                    StatusText = LocalizationService.T("Das G'MIC-Ergebnis liegt als Ebene über dem Bild")
                End Sub)
        End Function

        ''' <summary>Die markierten EBENEN: allein gerechnet, auf durchsichtigem Grund und auf ihr
        ''' gemeinsames Rechteck beschnitten, derselbe Weg wie beim Zusammenlegen. Text, Formen und SVG
        ''' sind damit gerastert, Maske, Deckkraft, Mischmethode, Drehung und eigene Anpassungen
        ''' stecken in den Pixeln. Das Ergebnis kommt als NEUE Ebene direkt ueber die oberste der
        ''' markierten, an dasselbe Rechteck, und startet neutral: alles andere zoege dieselbe Wirkung
        ''' ein zweites Mal ein. Die Quellen bleiben unveraendert liegen.</summary>
        Private Async Function EditLayersWithGmicAsync() As Task
            If Not CanEditLayersWithGmic Then Return
            Dim targets = SelectedAnnotations.Where(Function(a) a IsNot Nothing).
                OrderBy(Function(a) _annotations.IndexOf(a)).ToList()
            If targets.Count = 0 Then Return
            Dim job = PrepareAnnotationRender(targets)
            If job Is Nothing Then Return
            Dim rect = job.Rect
            Await RunGmicRoundTripAsync(
                Function(inputPath) Task.Run(Function() DrawAnnotationRenderToPng(job, inputPath)),
                Sub(outputPath)
                    ' Ueber der obersten Quelle, die es noch gibt; sonst ganz oben.
                    Dim present = targets.Where(Function(a) _annotations.Contains(a)).ToList()
                    Dim insertAt = If(present.Count = 0, _annotations.Count,
                                      present.Select(Function(a) _annotations.IndexOf(a)).Max() + 1)
                    PushUndo()
                    ClearSelectionForNewLayer()
                    Dim result = New ImageAnnotation With {
                        .Kind = "SelectionImage",
                        .Text = "G'MIC",
                        .ImagePath = outputPath,
                        .XPixels = rect.Left,
                        .YPixels = rect.Top,
                        .WidthPixels = rect.Width,
                        .HeightPixels = rect.Height,
                        .FillColor = "#00FFFFFF",
                        .StrokeColor = "#00000000",
                        .StrokeWidth = 0,
                        .Opacity = 100,
                        .BlendMode = "Normal",
                        .IsVisible = True
                    }
                    _annotations.Insert(Math.Min(insertAt, _annotations.Count), result)
                    _extraSelectedAnnotations.Clear()
                    SelectedAnnotationIndex = _annotations.IndexOf(result)
                    RaiseMultiSelectionChanged()
                    RebuildLayerRows()
                    _hasChanges = True
                    RaiseResetButtonStateChanged()
                    NameHistoryStep(LocalizationService.T("G'MIC angewendet"))
                    StatusText = LocalizationService.T("Das G'MIC-Ergebnis liegt als Ebene über der Ebene")
                    RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(result))
                End Sub)
        End Function

        ''' <summary>Der Rundweg, gemeinsam fuer ganzes Bild und Ebenen: Eingabe schreiben lassen,
        ''' gmic_qt starten und abwarten, aufraeumen, und das Ergebnis nur dann ablegen lassen, wenn
        ''' es zum noch offenen Dokument gehoert.</summary>
        ''' <param name="writeInput">Schreibt das Eingangsbild an den uebergebenen Pfad. False: es ging nicht.</param>
        ''' <param name="placeResult">Legt die fertige Ausgabe ab, auf dem UI-Faden.</param>
        Private Async Function RunGmicRoundTripAsync(writeInput As Func(Of String, Task(Of Boolean)),
                                                     placeResult As Action(Of String)) As Task
            ' Zwei offene G'MIC-Fenster auf demselben Dokument brächten zwei Ebenen, von denen
            ' keine weiss, dass es die andere gibt.
            If _gmicRunning Then
                StatusText = LocalizationService.T("G'MIC ist bereits geöffnet")
                Return
            End If
            Dim documentPath = _currentImagePath
            If String.IsNullOrWhiteSpace(documentPath) Then Return
            If Not GmicService.IsAvailable Then
                StatusText = LocalizationService.T("G'MIC wurde nicht gefunden")
                Return
            End If

            _gmicRunning = True
            Try
                Dim inputPath = CreateSelectionAssetTempPath("gmic-input")
                Dim outputPath = CreateSelectionAssetTempPath("gmic")
                StatusText = LocalizationService.T("Bild wird für G'MIC vorbereitet")
                Dim rendered As Boolean
                Try
                    rendered = Await writeInput(inputPath)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Editor.GmicInput", ex)
                    rendered = False
                End Try
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
                placeResult(outputPath)
            Finally
                _gmicRunning = False
            End Try
        End Function

    End Class

End Namespace
