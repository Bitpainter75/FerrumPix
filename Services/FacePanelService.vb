Imports System
Imports System.Collections.Generic
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
