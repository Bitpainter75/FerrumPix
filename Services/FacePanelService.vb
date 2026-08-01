Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Models

Namespace Services

    ''' <summary>Baut die Gesichtszeilen fuer die Infoleiste: Ausschnitt, Name, Ids.
    '''
    ''' EINE Stelle fuer alle drei Leisten. Galerie, Betrachter und Editor zeigen dieselbe
    ''' Infoleiste, haben aber je ein eigenes ViewModel - stuende das Schneiden in einem davon,
    ''' muessten die anderen beiden es nachbauen, und drei Fassungen desselben Zuschnitts driften
    ''' auseinander.
    '''
    ''' Ohne eingeschaltete Erkennung oder ohne Modelle kommt eine leere Liste zurueck; die Leisten
    ''' blenden den Abschnitt dann ganz aus.</summary>
    Public NotInheritable Class FacePanelService

        Private Sub New()
        End Sub

        ''' <summary>Kantenlaenge des Ausschnitts. Das Feld in der Leiste ist 44 Punkte hoch; das
        ''' Doppelte reicht auch auf einem feinen Bildschirm und kostet nichts.</summary>
        Private Const CropEdge As Integer = 88

        ''' <summary>Fuellt die Vorschlagsliste am Namensfeld mit den schon vergebenen Namen.
        '''
        ''' HIER und nicht dreimal einzeln: Galerie, Betrachter und Editor zeigen dieselbe Leiste
        ''' und brauchen dieselbe Liste. Gerufen wird das jedesmal beim Aufbauen der Zeilen - der
        ''' Bestand aendert sich beim Benennen, und ein Vorschlag, den es noch nicht gibt, hilft
        ''' niemandem.</summary>
        Public Shared Sub FillNameSuggestions(target As ObservableCollection(Of String))
            If target Is Nothing Then Return
            target.Clear()
            Try
                For Each name In LibraryService.Instance.GetPersonNames()
                    target.Add(name)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("FacePanel.FillNameSuggestions", ex)
            End Try
        End Sub

        ''' <summary>Die Zeilen zu einem Bild, OHNE Ausschnitte - nur die Datenbankabfrage.
        '''
        ''' Getrennt vom Bild, weil beides verschieden teuer ist: die Abfrage kostet nichts und darf
        ''' im Vordergrund laufen, das Dekodieren kostet richtig und gehoert in den Hintergrund
        ''' (siehe <see cref="LoadThumbnails"/>). Die Namen stehen damit sofort da, die Gesichter
        ''' kommen kurz danach nach.</summary>
        Public Shared Function BuildEntries(filePath As String) As List(Of PersonFaceEntry)
            Dim result As New List(Of PersonFaceEntry)()
            Try
                If Not FaceDetectionService.Enabled Then Return result
                If String.IsNullOrWhiteSpace(filePath) Then Return result

                For Each face In LibraryService.Instance.GetFacesForImage(filePath)
                    Dim entry As New PersonFaceEntry(face.FaceId, face.PersonId, face.Name)
                    entry.SetBox(face.X, face.Y, face.Width, face.Height)
                    result.Add(entry)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("FacePanel.BuildEntries", ex)
            End Try
            Return result
        End Function

        ''' <summary>Holt die Gesichtsausschnitte im Hintergrund nach.
        '''
        ''' DURCH DIE DECODE-SCHLEUSE, und das ist keine Formsache: es laeuft immer nur ein Decode in
        ''' der ganzen Anwendung. Ohne die Schleuse liefe hier einer neben dem Histogramm und, im
        ''' Betrachter, neben dem Bild selbst - und ein Blaettern durch zehn Fotos haette zehn
        ''' Volldecodes nebeneinander gestapelt.
        '''
        ''' EIN Decode fuer ALLE Gesichter des Bildes, nicht einer je Gesicht: auf einem Gruppenfoto
        ''' sind das schnell zehn.
        '''
        ''' Vor dem teuren Teil eine kurze Wartezeit und ERST DANACH die Gueltigkeitspruefung: wer
        ''' schnell durch den Filmstreifen blaettert, soll die Bilder dazwischen gar nicht erst
        ''' dekodieren. Stuende die Pruefung hinter der Rechnung, waere nur das Ergebnis verworfen
        ''' und die Last schon da.</summary>
        ''' <param name="stillCurrent">Sagt, ob das Ergebnis noch zum gezeigten Bild gehoert. Ohne
        ''' das setzt ein spaet zurueckkommender Lauf die Gesichter des VORHERIGEN Bildes ein.</param>
        Public Shared Sub LoadThumbnails(entries As IReadOnlyList(Of PersonFaceEntry), filePath As String,
                                         stillCurrent As Func(Of Boolean))
            If entries Is Nothing OrElse entries.Count = 0 Then Return
            If String.IsNullOrWhiteSpace(filePath) Then Return

            Dim pending = entries.ToList()
            Task.Run(Async Function()
                         Try
                             Await Task.Delay(150).ConfigureAwait(False)
                             If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                             If Not IO.File.Exists(filePath) Then Return

                             Dim crops = DecodeGate.Run(Function() CropAll(pending, filePath))
                             If crops Is Nothing Then Return

                             Dispatcher.UIThread.Post(Sub()
                                                          If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                                                          For i = 0 To pending.Count - 1
                                                              pending(i).Thumbnail = crops(i)
                                                          Next
                                                      End Sub)
                         Catch ex As Exception
                             DiagnosticLogService.LogException("FacePanel.LoadThumbnails", ex)
                         End Try
                     End Function)
        End Sub

        ''' <summary>Holt die Aushaengeschilder der Personenverwaltung nach.
        '''
        ''' Je Person EIN Bild, und jedes aus einer ANDEREN Datei - anders als im Infopanel, wo alle
        ''' Gesichter aus demselben Foto kommen. Bei hundert Gruppen waeren das hundert Decodes;
        ''' deshalb wird VERKLEINERT dekodiert (siehe <see cref="DecodeForFace"/>) und der Reihe
        ''' nach, damit die Schleuse nicht dauerhaft belegt ist und die Kacheln von oben nach unten
        ''' auftauchen statt alle auf einmal.</summary>
        Public Shared Sub LoadCovers(entries As IReadOnlyList(Of PersonListEntry), stillCurrent As Func(Of Boolean))
            If entries Is Nothing OrElse entries.Count = 0 Then Return
            Dim pending = entries.ToList()
            Task.Run(Sub()
                         Try
                             For Each entry In pending
                                 If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                                 ' Wer sein Bild schon hat, braucht keinen zweiten Decode. Ohne diese
                                 ' Zeile las das Zurueckblaettern jede Datei der Seite erneut - die
                                 ' Kacheln waren laengst da, und trotzdem lief die Wand nochmal an.
                                 If entry.Cover IsNot Nothing Then Continue For
                                 If String.IsNullOrWhiteSpace(entry.CoverPath) OrElse Not IO.File.Exists(entry.CoverPath) Then Continue For

                                 Dim bild = DecodeGate.Run(Function() CropFromFile(entry.CoverPath,
                                                                                  entry.BoxX, entry.BoxY,
                                                                                  entry.BoxWidth, entry.BoxHeight))
                                 If bild Is Nothing Then Continue For
                                 Dim ziel = entry
                                 Dispatcher.UIThread.Post(Sub()
                                                              If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                                                              ziel.Cover = bild
                                                          End Sub)
                             Next
                         Catch ex As Exception
                             DiagnosticLogService.LogException("FacePanel.LoadCovers", ex)
                         End Try
                     End Sub)
        End Sub

        ''' <summary>Dasselbe fuer die Gesichter EINER Person - auch die liegen in verschiedenen
        ''' Dateien.</summary>
        Public Shared Sub LoadFacesFromManyFiles(entries As IReadOnlyList(Of PersonFaceEntry),
                                                 paths As IReadOnlyList(Of String),
                                                 stillCurrent As Func(Of Boolean))
            If entries Is Nothing OrElse entries.Count = 0 Then Return
            Dim pending = entries.ToList()
            Dim pendingPaths = paths.ToList()
            Task.Run(Sub()
                         Try
                             For i = 0 To Math.Min(pending.Count, pendingPaths.Count) - 1
                                 If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                                 Dim path = pendingPaths(i)
                                 If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) Then Continue For
                                 Dim entry = pending(i)
                                 Dim bild = DecodeGate.Run(Function() CropFromFile(path, entry.BoxX, entry.BoxY,
                                                                                  entry.BoxWidth, entry.BoxHeight))
                                 If bild Is Nothing Then Continue For
                                 Dispatcher.UIThread.Post(Sub()
                                                              If stillCurrent IsNot Nothing AndAlso Not stillCurrent() Then Return
                                                              entry.Thumbnail = bild
                                                          End Sub)
                             Next
                         Catch ex As Exception
                             DiagnosticLogService.LogException("FacePanel.LoadFacesFromManyFiles", ex)
                         End Try
                     End Sub)
        End Sub

        Private Shared Function CropFromFile(path As String, x As Double, y As Double,
                                             w As Double, h As Double) As Bitmap
            Dim source As SkiaSharp.SKBitmap = Nothing
            Try
                Dim scale As Double = 1
                source = DecodeForFace(path, Math.Max(w, h), scale)
                If source Is Nothing Then Return Nothing
                Return CropFace(source, x * scale, y * scale, w * scale, h * scale)
            Catch ex As Exception
                DiagnosticLogService.LogException("FacePanel.CropFromFile", ex)
                Return Nothing
            Finally
                source?.Dispose()
            End Try
        End Function

        ''' <summary>Dekodiert nur so gross, wie fuer EIN Gesicht noetig ist.
        '''
        ''' Ein Foto mit 24 Megapixeln vollstaendig zu dekodieren, um daraus ein Feld von 88 Punkten
        ''' zu schneiden, ist die teuerste Art, an ein kleines Bild zu kommen. JPEG kann beim
        ''' Dekodieren selbst herunterrechnen (halb, viertel, achtel); wir bestellen die kleinste
        ''' Stufe, in der das Gesicht noch das Doppelte der Zielgroesse hat - darunter wuerde der
        ''' Ausschnitt matschig.
        '''
        ''' Der Massstab kommt als Nebenausgabe zurueck, denn die Lage des Gesichts steht in
        ''' Bildpunkten des ORIGINALS und muss mitwandern.</summary>
        Private Shared Function DecodeForFace(path As String, faceSize As Double, ByRef scale As Double) As SkiaSharp.SKBitmap
            scale = 1
            Using codec = SkiaSharp.SKCodec.Create(path)
                If codec Is Nothing Then Return SkiaSharp.SKBitmap.Decode(path)
                Dim wanted = CropEdge * 2.0
                If faceSize <= wanted Then Return SkiaSharp.SKBitmap.Decode(codec)

                Dim gewuenscht = CSng(wanted / faceSize)
                Dim masse = codec.GetScaledDimensions(gewuenscht)
                If masse.Width <= 0 OrElse masse.Height <= 0 Then Return SkiaSharp.SKBitmap.Decode(codec)

                Dim info = codec.Info.WithSize(masse.Width, masse.Height)
                Dim bitmap = New SkiaSharp.SKBitmap(info)
                If codec.GetPixels(info, bitmap.GetPixels()) <> SkiaSharp.SKCodecResult.Success Then
                    bitmap.Dispose()
                    Return SkiaSharp.SKBitmap.Decode(path)
                End If
                scale = masse.Width / CDbl(codec.Info.Width)
                Return bitmap
            End Using
        End Function

        ''' <summary>Alle Ausschnitte eines Bildes aus EINEM Decode. Ist die Datei nicht lesbar - ein
        ''' RAW etwa dekodiert Skia nicht -, kommt eine Liste voller Nothing zurueck: die Zeile bleibt
        ''' dann ohne Bild stehen, was immer noch besser ist als gar keine Zeile.</summary>
        Private Shared Function CropAll(entries As IReadOnlyList(Of PersonFaceEntry), filePath As String) As List(Of Bitmap)
            Dim result As New List(Of Bitmap)()
            Dim source As SkiaSharp.SKBitmap = Nothing
            Try
                source = SkiaSharp.SKBitmap.Decode(filePath)
                For Each entry In entries
                    result.Add(If(source Is Nothing, Nothing,
                                  CropFace(source, entry.BoxX, entry.BoxY, entry.BoxWidth, entry.BoxHeight)))
                Next
            Finally
                source?.Dispose()
            End Try
            Return result
        End Function

        ''' <summary>Schneidet ein Gesicht als kleines Vorschaubild heraus, mit etwas Rand darum -
        ''' ein Gesicht ohne Stirn und Kinn erkennt man schlechter wieder.</summary>
        Private Shared Function CropFace(source As SkiaSharp.SKBitmap,
                                         x As Double, y As Double, w As Double, h As Double) As Bitmap
            Try
                ' NICHT "left"/"top": beides verdeckt in VB gleichnamige Funktionen bzw. Eigenschaften.
                Dim margin = Math.Max(w, h) * 0.25
                Dim cropX = CInt(Math.Round(Math.Max(0, x - margin)))
                Dim cropY = CInt(Math.Round(Math.Max(0, y - margin)))
                Dim cropWidth = CInt(Math.Round(Math.Min(source.Width - cropX, w + margin * 2)))
                Dim cropHeight = CInt(Math.Round(Math.Min(source.Height - cropY, h + margin * 2)))
                If cropWidth < 8 OrElse cropHeight < 8 Then Return Nothing

                Using target = New SkiaSharp.SKBitmap(CropEdge, CropEdge, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul)
                    Using canvas = New SkiaSharp.SKCanvas(target)
                        canvas.Clear(SkiaSharp.SKColors.Black)
                        canvas.DrawBitmap(source,
                                          New SkiaSharp.SKRect(cropX, cropY, cropX + cropWidth, cropY + cropHeight),
                                          New SkiaSharp.SKRect(0, 0, CropEdge, CropEdge),
                                          New SkiaSharp.SKPaint With {.FilterQuality = SkiaSharp.SKFilterQuality.Medium})
                    End Using
                    Using image = SkiaSharp.SKImage.FromBitmap(target)
                        Using data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90)
                            Using stream = New IO.MemoryStream(data.ToArray())
                                Return New Bitmap(stream)
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("FacePanel.CropFace", ex)
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
