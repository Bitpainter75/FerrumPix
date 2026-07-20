Imports System
Imports System.IO
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>
    ''' Rezept-Begleitdatei fuer RAW-Bilder ("foto.cr2" -> "foto.cr2.fpxmp"): am XMP-Sidecar-Konzept
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
    ''' </summary>
    Public NotInheritable Class RawSidecarService

        Private Sub New()
        End Sub

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
