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

        ''' <summary>Kennung der Scan-FASSUNG. Sie steht in jedem Vermerk mit drin.
        '''
        ''' Die Buchfuehrung in ScannedImage haelt fest, WOMIT ein Bild zuletzt durchsucht wurde -
        ''' bisher allein die Aenderungszeit der Datei. Aendert sich aber der Scan selbst, passen die
        ''' alten Zeilen nicht mehr zu dem, was heute herauskaeme, und der Vermerk verhindert genau
        ''' den Lauf, der das richtigstellen wuerde. Steht die Kennung im Vermerk, faellt der ganze
        ''' Altbestand beim naechsten Vergleich durch und wird von selbst neu durchsucht.
        '''
        ''' Wer den Scan wieder aendert, zaehlt hier hoch und schreibt den Grund dazu:
        ''' fs2 - gesucht wird im EXIF-GEDREHTEN Bild, die gespeicherten Boxen liegen damit im
        ''' gedrehten Raum. Vermerke aus der Zeit davor meinen den ungedrehten und sind wertlos.</summary>
        Public Const ScanVersion As String = "fs2"

        ' LAUFZUSTAND.
        ' Es laeuft hoechstens EIN Durchlauf, und andere Teile der Anwendung muessen ihn anhalten
        ' koennen: wer die Personenerkennung abschaltet, wirft die Tabellen weg (siehe
        ' LibraryService.ClearAllFaces), und ein Lauf, der davon nichts mitbekommt, schreibt danach
        ' munter weiter - die Merkmale waeren wieder da, obwohl der Benutzer sie gerade loswerden
        ' wollte. Deshalb der oeffentliche Weg RequestCancel/IsRunning/RequestStopAndWait.
        Private Shared ReadOnly _runLock As New Object()
        Private Shared _stopSource As CancellationTokenSource
        Private Shared _running As Boolean

        ''' <summary>Laeuft gerade ein Durchlauf? Fuer alles, was nicht mitten hinein darf.</summary>
        Public Shared ReadOnly Property IsRunning As Boolean
            Get
                SyncLock _runLock
                    Return _running
                End SyncLock
            End Get
        End Property

        ''' <summary>Bittet einen laufenden Durchlauf aufzuhoeren. Kehrt sofort zurueck; was bis
        ''' dahin gespeichert wurde, bleibt stehen. Ohne laufenden Durchlauf passiert nichts.</summary>
        Public Shared Sub RequestCancel()
            SyncLock _runLock
                ' Innerhalb der Sperre: draussen koennte die Quelle zwischen Lesen und Cancel
                ' bereits freigegeben sein.
                If _stopSource IsNot Nothing Then
                    Try
                        _stopSource.Cancel()
                    Catch ex As ObjectDisposedException
                        ' Der Lauf war in derselben Sekunde von selbst fertig - dann ist nichts
                        ' mehr abzubrechen.
                    End Try
                End If
            End SyncLock
        End Sub

        ''' <summary>Bittet um Abbruch und wartet, bis der Durchlauf wirklich steht.
        '''
        ''' Gebraucht von jedem, der die Personentabellen leeren will: geschieht das mitten in einem
        ''' Lauf, schreibt der danach weiter, und die Tabellen sind hinterher nicht leer, sondern
        ''' halb gefuellt. Der Lauf endet nach dem Bild, an dem er gerade sitzt - laenger als ein
        ''' paar Sekunden dauert das nicht.</summary>
        ''' <returns>True, wenn nichts mehr laeuft. False nur, wenn die Wartezeit nicht reichte -
        ''' dann sollte der Aufrufer die Tabellen NICHT anfassen.</returns>
        Public Shared Function RequestStopAndWait(Optional timeoutMilliseconds As Integer = 10000) As Boolean
            RequestCancel()
            Dim waited = 0
            While IsRunning AndAlso waited < timeoutMilliseconds
                Thread.Sleep(50)
                waited += 50
            End While
            Return Not IsRunning
        End Function

        ''' <summary>Legt die EXIF-Drehung auf und gibt die ungedrehte Fassung frei.
        '''
        ''' HIER und nicht bei jedem Aufrufer einzeln, weil die Gesichtsboxen in genau dem Raum
        ''' liegen, den diese Funktion herstellt. Wer Ausschnitte daraus schneidet
        ''' (<see cref="FacePanelService"/>), muss durch dieselbe Tuer - sonst sitzt der Ausschnitt
        ''' quer zum gespeicherten Rechteck.</summary>
        Friend Shared Function ApplyOrientationOwned(source As SKBitmap, origin As SKEncodedOrigin) As SKBitmap
            If source Is Nothing Then Return Nothing
            Dim corrected = ImageOrientationService.ApplyOrientation(source, origin)
            ' ApplyOrientation gibt bei TopLeft DIESELBE Instanz zurueck - dann gibt es nichts
            ' freizugeben, sonst waere gerade das Ergebnis weggeworfen.
            If Not Object.ReferenceEquals(corrected, source) Then source.Dispose()
            Return corrected
        End Function

        ''' <summary>Laeuft ueber die Bilder und meldet nach jedem, wie weit er ist.
        ''' <paramref name="progress"/> bekommt (fertig, gesamt, Dateiname).</summary>
        ''' <param name="force">Auch Bilder erneut durchsuchen, die schon einmal dran waren.
        ''' Gebraucht, wenn der Benutzer AUSDRUECKLICH neu suchen laesst, obwohl sich nichts geaendert
        ''' hat - etwa nach einem umgestellten Mindestmass. Eine geaenderte Erkennung braucht das
        ''' nicht mehr: dafuer steht die Fassung im Vermerk (siehe <see cref="ScanVersion"/>), und der
        ''' Altbestand faellt von selbst durch. Von Hand gesetzte Zuordnungen bleiben in beiden
        ''' Faellen stehen (siehe LibraryService.SaveFaces).</param>
        ''' <param name="token">Bricht den Lauf ab. Was bis dahin gespeichert wurde, bleibt stehen.
        ''' Zusaetzlich haelt <see cref="RequestCancel"/> denselben Lauf von aussen an.</param>
        Public Shared Async Function RunAsync(paths As IReadOnlyList(Of String),
                                              Optional progress As IProgress(Of (Done As Integer, Total As Integer, File As String)) = Nothing,
                                              Optional token As CancellationToken = Nothing,
                                              Optional force As Boolean = False) As Task(Of FaceScanResult)
            Dim result As New FaceScanResult()
            If paths Is Nothing OrElse paths.Count = 0 Then Return result
            If Not FaceDetectionService.Enabled Then Return result

            ' HOECHSTENS EIN DURCHLAUF. Ein zweiter waere nicht nur doppelte Arbeit: die Zuordnung zu
            ' Gruppen muss der Reihe nach laufen (siehe Klassenkommentar), zwei Laeufe legten
            ' dieselbe unbekannte Person zweimal an.
            Dim stopSource As CancellationTokenSource = Nothing
            SyncLock _runLock
                If _running Then Return result
                stopSource = New CancellationTokenSource()
                _stopSource = stopSource
                _running = True
            End SyncLock

            Try
                ' Der eigene Abbruchweg (RequestCancel) und der des Aufrufers zusammengefuehrt:
                ' aufgehoert wird, sobald EINER von beiden es verlangt.
                Using linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopSource.Token)
                    Dim runToken = linked.Token
                    Await Task.Run(Sub() ScanLoop(paths, result, progress, runToken, force), runToken).ConfigureAwait(False)

                    ' Die erkannten Personen zurueck nach Immich, sobald sie einen Namen haben. Das
                    ' laeuft NACH dem Durchlauf und nicht darin: benannt wird von Hand, und beim
                    ' ersten Durchgang heisst noch keine Gruppe irgendwie. Wer spaeter benennt,
                    ' stoesst es erneut an.
                    If Not result.Cancelled Then
                        result.TagsWritten = Await WritePeopleBackToImmichAsync(paths, runToken).ConfigureAwait(False)
                    End If
                End Using
            Catch ex As OperationCanceledException
                ' Task.Run wirft das, wenn der Abbruch schon vor dem Start kam. Was bis dahin
                ' gespeichert wurde, bleibt stehen - genau das ist mit Abbruch gemeint.
                result.Cancelled = True
            Finally
                SyncLock _runLock
                    _running = False
                    _stopSource = Nothing
                    stopSource.Dispose()
                End SyncLock
            End Try

            DiagnosticLogService.LogAlways("Gesichter.Durchlauf",
                $"{result.Scanned} gescannt, {result.Skipped} uebersprungen, " &
                $"{result.FacesFound} Gesichter, {result.Failed} fehlgeschlagen" &
                If(result.Cancelled, ", abgebrochen", ""))
            Return result
        End Function

        ''' <summary>Der eigentliche Gang ueber die Bilder. Eigene Methode und keine lange Lambda,
        ''' damit der Rahmen darueber - nur ein Lauf, Abbruch, Rueckschreiben nach Immich - auf einen
        ''' Blick lesbar bleibt.</summary>
        Private Shared Sub ScanLoop(paths As IReadOnlyList(Of String),
                                    result As FaceScanResult,
                                    progress As IProgress(Of (Done As Integer, Total As Integer, File As String)),
                                    token As CancellationToken,
                                    force As Boolean)
            Dim library = LibraryService.Instance
            Dim index = 0
            For Each filePath In paths
                index += 1
                If token.IsCancellationRequested Then
                    result.Cancelled = True
                    Return
                End If

                ' DER SCHALTER KANN WAEHREND DES LAUFS FALLEN. Wer die Personenerkennung abschaltet,
                ' wirft die Tabellen weg - ein Lauf, der davon nichts mitbekommt, fuellt sie danach
                ' wieder, und der Benutzer haette die Merkmale nicht los. Deshalb VOR jedem Bild
                ' erneut gefragt; die Einstellung liegt gepuffert und kostet nichts.
                If Not FaceDetectionService.Enabled Then
                    result.Cancelled = True
                    Return
                End If

                Try
                    If String.IsNullOrWhiteSpace(filePath) Then
                        result.Skipped += 1
                        Continue For
                    End If

                    ' Immich-Elemente tragen einen Pseudo-Pfad und liegen nicht im Dateisystem. Sie
                    ' werden trotzdem durchsucht - ueber die VORSCHAU vom Server, denn die Erkennung
                    ' rechnet ohnehin auf 640 Punkten und das Original zu holen braechte nichts
                    ' ausser Wartezeit. Als Stempel dient die Asset-Id: eine Aenderungszeit gibt es
                    ' hier nicht, und ein Asset in Immich wird ersetzt statt geaendert.
                    Dim isImmich = filePath.StartsWith("immich://", StringComparison.OrdinalIgnoreCase)
                    ' Ohne eingerichteten Server ist ein Immich-Element nicht erreichbar - das ist
                    ' kein FEHLER, sondern schlicht nichts zu tun. Als Fehlschlag gezaehlt saehe es
                    ' aus, als waere etwas kaputt.
                    If isImmich AndAlso Not ImmichService.IsConfigured Then
                        result.Skipped += 1
                        Continue For
                    End If
                    If Not isImmich AndAlso Not File.Exists(filePath) Then
                        result.Skipped += 1
                        Continue For
                    End If

                    ' Die Scan-Fassung gehoert MIT in den Vermerk, sonst gilt ein Bild als erledigt,
                    ' das eine aeltere Fassung anders gesehen hat - siehe ScanVersion.
                    Dim stamp = ScanVersion & "|" &
                                If(isImmich, "immich",
                                   File.GetLastWriteTimeUtc(filePath).ToString("o", CultureInfo.InvariantCulture))
                    If Not force AndAlso Not library.NeedsFaceScan(filePath, stamp) Then
                        result.Skipped += 1
                        Continue For
                    End If

                    ' DURCH DIE DECODE-SCHLEUSE, und nur der Decode. Es laeuft immer nur einer in der
                    ' ganzen Anwendung (siehe DecodeGate); ohne die Schleuse liefe hier einer neben
                    ' dem Bild im Betrachter und neben den Kacheln. Die Suche selbst bleibt DRAUSSEN:
                    ' ein Ordner braucht Minuten, und solange stuende sonst jede andere Anzeige.
                    Using bitmap = DecodeGate.Run(Function() DecodeForScan(filePath, isImmich))
                        If bitmap Is Nothing Then
                            ' Nicht lesbar: trotzdem als gescannt vermerken, sonst versucht es jeder
                            ' weitere Lauf erneut.
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
        End Sub

        ''' <summary>Dekodiert ein Bild fuer die Suche - AUFRECHT.
        '''
        ''' SKBitmap.Decode liefert die Bildpunkte so, wie sie in der Datei liegen, und laesst das
        ''' EXIF-Feld fuer die Drehung unbeachtet. Ein Hochformat vom Handy wurde damit LIEGEND
        ''' durchsucht; die Erkennung sucht aufrecht stehende Gesichter und fand darauf schlicht
        ''' keine - ohne Fehler, ohne Meldung, das Bild galt danach als erledigt. Deshalb dieselbe
        ''' Korrektur wie ueberall sonst in der Anwendung (ImageOrientationService).
        '''
        ''' Folge: die gespeicherten Boxen liegen im GEDREHTEN Bild. Wer sie wieder benutzt, muss
        ''' genauso dekodieren (siehe FacePanelService), und Vermerke aelterer Laeufe sind wertlos
        ''' geworden - dafuer steht die Fassung im Stempel (siehe ScanVersion).</summary>
        Private Shared Function DecodeForScan(filePath As String, isImmich As Boolean) As SKBitmap
            If isImmich Then Return DecodeImmichPreview(filePath)
            Dim decoded = SKBitmap.Decode(filePath)
            If decoded Is Nothing Then Return Nothing
            Return ApplyOrientationOwned(decoded, ImageOrientationService.ReadOrigin(filePath))
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
                ' Auch die Vorschau vom Server kann ein Drehfeld tragen, und dann liegen ihre
                ' Bildpunkte quer - dieselbe Falle wie bei einer Datei von der Platte.
                Dim origin As SKEncodedOrigin
                Using probe As New MemoryStream(bytes)
                    origin = ImageOrientationService.ReadOrigin(probe)
                End Using
                Return ApplyOrientationOwned(SKBitmap.Decode(bytes), origin)
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
