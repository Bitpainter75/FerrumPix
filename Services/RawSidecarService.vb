Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>
    ''' Rezept-Begleitdatei fuer NUR-LESBARE Bildformate ("foto.cr2" -> "foto.cr2.fpxmp",
    ''' "foto.psd" -> "foto.psd.fpxmp"): am XMP-Sidecar-Konzept
    ''' orientiert (kleine XML-Datei NEBEN dem Original, wird beim Oeffnen automatisch geladen und
    ''' beim Verlassen aktualisiert), aber ein EIGENES Format - die Reglermodelle sind nicht
    ''' Adobe-kompatibel, ein echtes XMP wuerde nur so tun als ob.
    '''
    ''' Inhalt: eine XML-Huelle (Version, Quelldatei, Zeitstempel) mit dem Rezept-JSON des
    ''' .fpx-Formats als CDATA. Bewusst KEINE eigene XML-Abbildung aller Regler: die JSON-Regeln
    ''' aus FpxService sind konstruktorbasiert erprobt (VB kann keine JsonConverter schreiben),
    ''' und beide Formate koennen so nie auseinanderdriften.
    '''
    ''' Grenzen (bewusst): gebackene Pixel-Bearbeitungen (Pinsel/Radierer/Retusche/gerasterte
    ''' Ebenen) stecken im ARBEITSBILD, nicht im Rezept - dafuer bleibt die .fpx zustaendig; der
    ''' Editor schreibt den Sidecar nur, solange nichts gebacken wurde. Bild-Objekte referenzieren
    ''' ihre Dateien mit absolutem Pfad (keine Einbettung wie im .fpx-Buendel).
    '''
    ''' Der Sidecar ist NICHT abschaltbar (die Einstellung dafuer ist am 2026-07-20 entfallen): fuer
    ''' RAW und PSD ist er der EINZIGE Weg, eine Bearbeitung zu behalten, ohne die Quelldatei zu
    ''' zerstoeren - beide Formate koennen wir nicht schreiben. Ein Schalter dafuer hiess in der
    ''' Praxis "Bearbeitung stillschweigend wegwerfen", und er liess Viewer und Editor
    ''' auseinanderlaufen: der Viewer las den Sidecar bedingungslos, der Editor nur bei aktiver
    ''' Einstellung, womit dieselbe Datei je nach Ansicht anders gedreht aussah.
    ''' </summary>
    Public NotInheritable Class RawSidecarService

        Private Sub New()
        End Sub

        ''' <summary>Formate, deren Bearbeitung in den Sidecar geht statt in die Datei: RAW und
        ''' PSD/PSB. Beide sind nur-lesend (siehe die Schreibsperre in ImageProcessor.SaveImage),
        ''' beide sollen trotzdem Reglerwerte behalten koennen.</summary>
        Public Shared Function IsSidecarFormat(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return RawPreviewService.IsSupportedRaw(path) OrElse PsdPreviewService.IsSupportedPsd(path)
        End Function

        Public Const Extension As String = ".fpxmp"
        Private Const FormatVersion As Integer = 1
        Private Shared ReadOnly Ns As XNamespace = "https://github.com/Bitpainter75/FerrumPix/ns/recipe/1.0"

        ''' <summary>"foto.cr2" -> "foto.cr2.fpxmp" (voller Name bleibt erhalten, damit
        ''' foto.cr2 und foto.dng nie um denselben Sidecar konkurrieren).</summary>
        Public Shared Function SidecarPathFor(rawPath As String) As String
            Return rawPath & Extension
        End Function

        Public Shared Function Exists(rawPath As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(rawPath) AndAlso File.Exists(SidecarPathFor(rawPath))
        End Function

        ''' <summary>Schreibt das Rezept neben die RAW-Datei (atomar via Temp+Move). False bei Fehler -
        ''' der Aufrufer behaelt dann sein normales "ungespeicherte Aenderungen"-Verhalten.</summary>
        Public Shared Function TryWrite(rawPath As String, adjustments As ImageAdjustments) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            Try
                Dim json = FpxService.SerializeAdjustments(adjustments)
                Dim doc = New XDocument(
                    New XDeclaration("1.0", "utf-8", Nothing),
                    New XElement(Ns + "recipe",
                        New XAttribute("version", FormatVersion),
                        New XAttribute("generator", "FerrumPix"),
                        New XElement(Ns + "source",
                            New XAttribute("fileName", Path.GetFileName(rawPath))),
                        New XElement(Ns + "savedUtc", DateTime.UtcNow.ToString("O")),
                        New XElement(Ns + "adjustments",
                            New XAttribute("format", "fpx-json"),
                            New XCData(json))))

                Dim target = SidecarPathFor(rawPath)
                Dim temp = target & ".tmp"
                Using writer = XmlWriter.Create(temp, New XmlWriterSettings With {.Indent = True})
                    doc.Save(writer)
                End Using
                File.Move(temp, target, overwrite:=True)
                ' Den gemerkten Drehwinkel verwerfen, statt auf einen geaenderten Zeitstempel zu
                ' hoffen: zwei Schreibvorgaenge kurz hintereinander koennen auf grob aufloesenden
                ' Dateisystemen dieselbe mtime tragen.
                Dim ignored As CachedRotation = Nothing
                _rotationCache.TryRemove(target, ignored)
                Return True
            Catch
                Return False
            End Try
        End Function

        ' ── Mitwandern bei Dateioperationen ──────────────────────────────────────
        ' Wie XMP-Sidecars in Lightroom: Verschieben/Kopieren/Umbenennen/Loeschen der RAW-Datei
        ' nimmt die Begleitdatei mit. Immer best effort - ein Fehler an der Begleitdatei darf die
        ' Hauptoperation nie scheitern lassen (deshalb schlucken alle drei Methoden Ausnahmen).

        Public Shared Sub AccompanyMove(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Move(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch
            End Try
        End Sub

        Public Shared Sub AccompanyCopy(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Copy(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch
            End Try
        End Sub

        ''' <summary>Beim Papierkorb wandert die Begleitdatei ebenfalls in den Papierkorb (die
        ''' RAW-Datei laesst sich so mitsamt Rezept wiederherstellen), sonst wird sie geloescht.</summary>
        Public Shared Sub AccompanyDelete(rawPath As String, useTrash As Boolean)
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return
                If useTrash Then
                    TrashService.MoveToTrash(sidecar)
                Else
                    File.Delete(sidecar)
                End If
            Catch
            End Try
        End Sub

        ''' <summary>Nur die Drehung aus dem Sidecar (0/90/180/270; 0 wenn keiner da ist). Der
        ''' ANZEIGE-Weg braucht ausschliesslich diesen einen Wert: Viewer, Filmstreifen und Kacheln
        ''' zeigen die schnelle eingebettete RAW-Vorschau, nicht die entwickelte Datei - Belichtung
        ''' und Farbe aus dem Rezept wirken dort bewusst nicht, die Geometrie muss aber stimmen,
        ''' sonst haette das Drehen im Viewer sichtbar keine Wirkung.
        '''
        ''' Ergebnisse werden je Sidecar-Zeitstempel gemerkt: der Aufruf sitzt im Thumbnail-Pfad und
        ''' liefe sonst pro Kachel durch einen XML-Parse.</summary>
        Public Shared Function ReadRotationDegrees(rawPath As String) As Integer
            If String.IsNullOrWhiteSpace(rawPath) Then Return 0
            Dim sidecar = SidecarPathFor(rawPath)
            Dim stampTicks As Long
            Try
                If Not File.Exists(sidecar) Then Return 0
                stampTicks = File.GetLastWriteTimeUtc(sidecar).Ticks
            Catch
                Return 0
            End Try

            Dim cached As CachedRotation = Nothing
            If _rotationCache.TryGetValue(sidecar, cached) AndAlso cached.StampTicks = stampTicks Then Return cached.Degrees

            Dim adjustments = TryRead(rawPath)
            Dim degrees = If(adjustments Is Nothing, 0, ImageOrientationService.NormalizeQuarterTurn(adjustments.RotationDegrees))
            _rotationCache(sidecar) = New CachedRotation(stampTicks, degrees)
            Return degrees
        End Function

        Private NotInheritable Class CachedRotation
            Public ReadOnly StampTicks As Long
            Public ReadOnly Degrees As Integer
            Public Sub New(stampTicks As Long, degrees As Integer)
                Me.StampTicks = stampTicks
                Me.Degrees = degrees
            End Sub
        End Class

        Private Shared ReadOnly _rotationCache As New ConcurrentDictionary(Of String, CachedRotation)(PathIdentity.Comparer)

        ''' <summary>Liest das Rezept aus dem Sidecar. Nothing, wenn keiner da ist, die Version
        ''' unbekannt oder die Datei defekt ist - der Editor startet dann wie ohne Sidecar.</summary>
        Public Shared Function TryRead(rawPath As String) As ImageAdjustments
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Dim doc = XDocument.Load(sidecar)
                Dim root = doc.Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim version = CInt(root.Attribute("version")?.Value)
                If version < 1 OrElse version > FormatVersion Then Return Nothing
                Dim adjustmentsNode = root.Element(Ns + "adjustments")
                If adjustmentsNode Is Nothing Then Return Nothing
                Return FpxService.DeserializeAdjustments(adjustmentsNode.Value)
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
