Imports System
Imports System.Diagnostics
Imports System.IO

Namespace Services

    ''' <summary>Brücke zum optional installierten Programm dnglab. DNG ist ein RAW-Container
    ''' und kann nicht durch die normale gerenderte Bildausgabe erzeugt werden.</summary>
    Public NotInheritable Class DngConverterService

        Private Sub New()
        End Sub

        Public Shared Function IsAvailable() As Boolean
            Try
                Dim startInfo As New ProcessStartInfo With {
                    .FileName = "dnglab", .UseShellExecute = False,
                    .RedirectStandardOutput = False, .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                startInfo.ArgumentList.Add("--version")
                Using process As Process = Process.Start(startInfo)
                    If process Is Nothing Then Return False
                    process.WaitForExit(3000)
                    If Not process.HasExited Then
                        Try
                            process.Kill(entireProcessTree:=True)
                        Catch
                        End Try
                        Return False
                    End If
                    Return process.ExitCode = 0
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Wandelt eine RAW-Datei in ein DNG. Laeuft NUR im Hintergrundfaden: der Aufruf
        ''' wartet auf einen fremden Prozess, der je Bild Sekunden braucht.
        '''
        ''' Der Abbruchknopf des Stapels muss auch hier greifen. Ohne die Warteschleife unten
        ''' liefe die begonnene Datei nach dem Abbruch noch zu Ende - bei einem Stapel aus
        ''' zweihundert RAWs ist das der Unterschied zwischen Sekunden und Minuten.</summary>
        Public Shared Function ConvertRawToDng(sourcePath As String, targetPath As String,
                                               Optional cancel As Threading.CancellationToken = Nothing) As Boolean
            If String.IsNullOrWhiteSpace(sourcePath) OrElse String.IsNullOrWhiteSpace(targetPath) Then Return False
            If Not RawPreviewService.IsSupportedRaw(sourcePath) OrElse
               String.Equals(Path.GetExtension(sourcePath), ".dng", StringComparison.OrdinalIgnoreCase) Then Return False
            ' DER KONVERTER SCHREIBT NIE INS ZIEL, sondern in eine versteckte Nachbardatei im selben
            ' Ordner, und erst nach einem sauberen Ende wird sie umgehaengt. Frueher bekam dnglab das
            ' bestaetigte Ziel samt --override: ein Abbruch oder Fehler konnte ein vorhandenes DNG
            ' halb ueberschrieben zuruecklassen. Die Endung bleibt .dng, weil der Konverter das
            ' Ausgabeformat daran erkennt; der Punkt vorn haelt sie aus der Galerie heraus.
            Dim targetDirectory = Path.GetDirectoryName(targetPath)
            Dim stagePath = Path.Combine(If(targetDirectory, ""),
                                         "." & Path.GetFileNameWithoutExtension(targetPath) &
                                         ".fpconvert-" & Guid.NewGuid().ToString("N") & ".dng")
            Try
                If Not String.IsNullOrWhiteSpace(targetDirectory) Then Directory.CreateDirectory(targetDirectory)
                Dim startInfo As New ProcessStartInfo With {
                    .FileName = "dnglab", .UseShellExecute = False,
                    .RedirectStandardOutput = False, .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                startInfo.ArgumentList.Add("convert")
                ' Jeder Auftrag bekommt einen eigenen Namen; eine vorhandene Datei darf der
                ' Konverter weder ersetzen noch fuer den eigenen Erfolg halten.
                startInfo.ArgumentList.Add(sourcePath)
                startInfo.ArgumentList.Add(stagePath)
                Using process As Process = Process.Start(startInfo)
                    If process Is Nothing Then Return False
                    ' Nebenlaeufig lesen, nicht mit ReadToEnd blockieren: sonst haengt der Faden bis
                    ' zum Ende des fremden Prozesses und sieht den Abbruch nie.
                    Dim errorReader = process.StandardError.ReadToEndAsync()
                    Do Until process.WaitForExit(200)
                        If Not cancel.IsCancellationRequested Then Continue Do
                        Try
                            process.Kill(entireProcessTree:=True)
                        Catch
                        End Try
                        process.WaitForExit(2000)
                        Exit Do
                    Loop
                    Dim standardError = If(errorReader.Wait(2000), errorReader.Result, "")
                    If Not cancel.IsCancellationRequested AndAlso process.HasExited AndAlso
                       process.ExitCode = 0 AndAlso File.Exists(stagePath) Then
                        File.Move(stagePath, targetPath, overwrite:=True)
                        Return True
                    End If
                    If Not String.IsNullOrWhiteSpace(standardError) Then DiagnosticLogService.LogAlways("DngConverter", standardError.Trim())
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("DngConverter", ex)
            End Try
            ' Ein abgebrochener oder gescheiterter Lauf laesst ein halbes DNG liegen. Es sieht wie
            ' ein Bild aus, ist aber keins; weg muss nur die Nachbardatei, das Ziel ist unberuehrt.
            Try
                If File.Exists(stagePath) Then File.Delete(stagePath)
            Catch
            End Try
            Return False
        End Function
    End Class
End Namespace
