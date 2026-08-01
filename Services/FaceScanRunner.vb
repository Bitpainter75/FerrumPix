Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>Was ein Durchlauf gebracht hat.</summary>
    Public Class FaceScanResult
        Public Property Scanned As Integer
        Public Property Skipped As Integer
        Public Property FacesFound As Integer
        Public Property Failed As Integer
        Public Property Cancelled As Boolean
        ''' <summary>Wie viele Personen-Stichworte nach Immich zurueckgeschrieben wurden.</summary>
        Public Property TagsWritten As Integer
    End Class

    ''' <summary>Sucht Gesichter in einer Liste von Bildern und schreibt sie in die Bibliothek.
    '''
    ''' ORDNERWEISE AUSGELOEST, GLOBAL VERGLICHEN. Der Durchlauf bekommt genau die Bilder, die der
    ''' Benutzer gerade sieht - aber die Zuordnung zu einer Person geschieht gegen den GANZEN
    ''' Bestand (siehe LibraryService.SaveFaces). Sonst waere dieselbe Person je Ordner eine andere,
    ''' und weil bewusst ordnerweise ausgeloest wird, waere das der Regelfall statt der Ausnahme.
    '''
    ''' EINFAEDIG, nicht parallel ueber Bilder. Zwei Gruende: die Modelle nutzen ihre Kerne bereits
    ''' selbst (halbe Kernzahl je Sitzung), und die Zuordnung muss der Reihe nach laufen - zwei
    ''' Bilder mit derselben unbekannten Person gleichzeitig wuerden zwei Gruppen anlegen statt einer.
    '''
    ''' Gemessen rund 135 ms je Gesicht plus etwa 60 ms Suche je Bild. Ein Ordner mit 500 Fotos
    ''' laeuft damit in gut einer Minute durch; das ist eine Hintergrundarbeit mit Fortschritt und
    ''' kein Klick, der sofort fertig ist.</summary>
    Public NotInheritable Class FaceScanRunner

        Private Sub New()
        End Sub

        ''' <summary>Laeuft ueber die Bilder und meldet nach jedem, wie weit er ist.
        ''' <paramref name="progress"/> bekommt (fertig, gesamt, Dateiname).</summary>
        ''' <param name="force">Auch Bilder erneut durchsuchen, die schon einmal dran waren.
        ''' Gebraucht, sobald sich die Erkennung selbst geaendert hat: die Buchfuehrung merkt sich nur
        ''' DASS ein Bild gescannt wurde, nicht WOMIT - ohne diesen Weg bliebe ein Bestand fuer immer
        ''' auf dem Stand der Fassung, mit der er einmal durchlief. Von Hand gesetzte Zuordnungen
        ''' bleiben dabei stehen (siehe LibraryService.SaveFaces).</param>
        Public Shared Async Function RunAsync(paths As IReadOnlyList(Of String),
                                              Optional progress As IProgress(Of (Done As Integer, Total As Integer, File As String)) = Nothing,
                                              Optional token As CancellationToken = Nothing,
                                              Optional force As Boolean = False) As Task(Of FaceScanResult)
            Dim result As New FaceScanResult()
            If paths Is Nothing OrElse paths.Count = 0 Then Return result
            If Not FaceDetectionService.Enabled Then Return result

            Await Task.Run(
                Sub()
                    Dim library = LibraryService.Instance
                    Dim index = 0
                    For Each filePath In paths
                        index += 1
                        If token.IsCancellationRequested Then
                            result.Cancelled = True
                            Return
                        End If

                        Try
                            If String.IsNullOrWhiteSpace(filePath) Then
                                result.Skipped += 1
                                Continue For
                            End If

                            ' Immich-Elemente tragen einen Pseudo-Pfad und liegen nicht im
                            ' Dateisystem. Sie werden trotzdem durchsucht - ueber die VORSCHAU vom
                            ' Server, denn die Erkennung rechnet ohnehin auf 640 Punkten und das
                            ' Original zu holen braechte nichts ausser Wartezeit. Als Stempel dient
                            ' die Asset-Id: eine Aenderungszeit gibt es hier nicht, und ein Asset in
                            ' Immich wird ersetzt statt geaendert.
                            Dim isImmich = filePath.StartsWith("immich://", StringComparison.OrdinalIgnoreCase)
                            ' Ohne eingerichteten Server ist ein Immich-Element nicht erreichbar -
                            ' das ist kein FEHLER, sondern schlicht nichts zu tun. Als Fehlschlag
                            ' gezaehlt saehe es aus, als waere etwas kaputt.
                            If isImmich AndAlso Not ImmichService.IsConfigured Then
                                result.Skipped += 1
                                Continue For
                            End If
                            If Not isImmich AndAlso Not File.Exists(filePath) Then
                                result.Skipped += 1
                                Continue For
                            End If

                            Dim stamp = If(isImmich, "immich",
                                           File.GetLastWriteTimeUtc(filePath).ToString("o", CultureInfo.InvariantCulture))
                            If Not force AndAlso Not library.NeedsFaceScan(filePath, stamp) Then
                                result.Skipped += 1
                                Continue For
                            End If

                            Using bitmap = If(isImmich, DecodeImmichPreview(filePath), SKBitmap.Decode(filePath))
                                If bitmap Is Nothing Then
                                    ' Nicht lesbar: trotzdem als gescannt vermerken, sonst versucht
                                    ' es jeder weitere Lauf erneut.
                                    library.SaveFaces(filePath, stamp, New List(Of DetectedFace)())
                                    result.Failed += 1
                                    Continue For
                                End If

                                Dim faces = FilterBySize(FaceDetectionService.Detect(bitmap), bitmap)
                                library.SaveFaces(filePath, stamp, faces)
                                result.Scanned += 1
                                result.FacesFound += faces.Count
                            End Using
                        Catch ex As Exception
                            DiagnosticLogService.LogException("Gesichter.Durchlauf", ex)
                            result.Failed += 1
                        Finally
                            progress?.Report((index, paths.Count, Path.GetFileName(If(filePath, ""))))
                        End Try
                    Next
                End Sub, token).ConfigureAwait(False)

            ' Die erkannten Personen zurueck nach Immich, sobald sie einen Namen haben. Das laeuft
            ' NACH dem Durchlauf und nicht darin: benannt wird von Hand, und beim ersten Durchgang
            ' heisst noch keine Gruppe irgendwie. Wer spaeter benennt, stoesst es erneut an.
            If Not result.Cancelled Then
                result.TagsWritten = Await WritePeopleBackToImmichAsync(paths, token).ConfigureAwait(False)
            End If

            DiagnosticLogService.LogAlways("Gesichter.Durchlauf",
                $"{result.Scanned} gescannt, {result.Skipped} uebersprungen, " &
                $"{result.FacesFound} Gesichter, {result.Failed} fehlgeschlagen" &
                If(result.Cancelled, ", abgebrochen", ""))
            Return result
        End Function

        ''' <summary>Wirft zu kleine Gesichter weg, bevor sie ueberhaupt in die Bibliothek kommen.
        '''
        ''' Gemessen wird an der KUERZEREN Bildkante, nicht in Bildpunkten: 80 Punkte sind auf einem
        ''' 24-Megapixel-Foto ein Kopf in der dritten Reihe und auf einem Handyschnappschuss ein
        ''' Portraet. Die Prozentzahl meint auf jedem Bild dasselbe.
        '''
        ''' WARUM HIER und nicht in der Erkennung: <see cref="FaceDetectionService.Detect"/> soll
        ''' sagen, was im Bild IST. Was davon in die Bibliothek gehoert, ist eine Frage des
        ''' Geschmacks - wer auf einem Stadtfest die Umstehenden nicht in seiner Personenliste haben
        ''' will, stellt das ein, und die Erkennung bleibt davon unberuehrt.
        '''
        ''' Ab Werk 3 (gemessen, siehe AppSettingsService). Auf 0 gestellt bleibt alles, was die
        ''' Erkennung findet.</summary>
        Private Shared Function FilterBySize(faces As List(Of DetectedFace), source As SKBitmap) As List(Of DetectedFace)
            If faces Is Nothing OrElse faces.Count = 0 Then Return faces
            Dim prozent = AppSettingsService.Load().FaceMinimumSizePercent
            If prozent <= 0 OrElse source Is Nothing Then Return faces

            Dim kante = Math.Min(source.Width, source.Height)
            Dim grenze = kante * prozent / 100.0
            If grenze <= 0 Then Return faces
            Return faces.Where(Function(f) Math.Max(f.Width, f.Height) >= grenze).ToList()
        End Function

        ''' <summary>Holt die Vorschau eines Immich-Assets und dekodiert sie. Nothing, wenn der
        ''' Server nichts liefert - das Asset gilt dann als gescannt, damit nicht jeder Lauf erneut
        ''' danach fragt.</summary>
        Private Shared Function DecodeImmichPreview(pseudoPath As String) As SKBitmap
            Try
                Dim assetId As String = Nothing, fileName As String = Nothing
                If Not ImmichService.TryParsePseudoPath(pseudoPath, assetId, fileName) Then Return Nothing
                Dim bytes = ImmichService.GetPreviewBytesAsync(assetId).GetAwaiter().GetResult()
                If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
                Return SKBitmap.Decode(bytes)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gesichter.ImmichVorschau", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Schreibt die BENANNTEN Personen der behandelten Immich-Assets als Stichworte
        ''' zurueck. Unbenannte Gruppen bleiben draussen - "Person 7" waere fuer niemanden ein
        ''' brauchbares Stichwort, und einmal geschrieben liesse es sich nur von Hand wieder los.
        '''
        ''' NUR AUF ANSAGE (Einstellung "Erkannte Personen nach Immich schreiben", ab Werk aus).
        ''' Der Server gehoert dem Benutzer: dort etwas hinzuschreiben aendert Daten ausserhalb
        ''' dieses Programms, und wer wen auf einem Foto erkannt hat, soll nicht als Nebenwirkung
        ''' eines Suchlaufs den Rechner verlassen.</summary>
        Private Shared Async Function WritePeopleBackToImmichAsync(paths As IReadOnlyList(Of String),
                                                                   token As CancellationToken) As Task(Of Integer)
            If Not ImmichService.IsConfigured Then Return 0
            If Not AppSettingsService.Load().ImmichWritePeopleTags Then Return 0
            Dim written = 0
            Try
                Dim library = LibraryService.Instance
                For Each filePath In paths
                    If token.IsCancellationRequested Then Exit For
                    If String.IsNullOrWhiteSpace(filePath) Then Continue For
                    If Not filePath.StartsWith("immich://", StringComparison.OrdinalIgnoreCase) Then Continue For

                    Dim assetId As String = Nothing, fileName As String = Nothing
                    If Not ImmichService.TryParsePseudoPath(filePath, assetId, fileName) Then Continue For

                    Dim names = library.GetPeopleForImage(filePath).
                                Where(Function(p) p.IsNamed).
                                Select(Function(p) p.Name).ToList()
                    If names.Count = 0 Then Continue For

                    written += Await ImmichService.WritePeopleAsTagsAsync(assetId, names, token).ConfigureAwait(False)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gesichter.NachImmich", ex)
            End Try
            Return written
        End Function

    End Class

End Namespace
