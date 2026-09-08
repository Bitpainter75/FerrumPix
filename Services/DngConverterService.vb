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
            ' Nur eine Datei, die es vorher NICHT gab, darf nach einem Fehlschlag wieder
            ' verschwinden. Ein vorhandenes Ziel gehoert dem Anwender, auch als Bruchstueck.
            Dim targetExisted = File.Exists(targetPath)
            Try
                Dim targetDirectory = Path.GetDirectoryName(targetPath)
                If Not String.IsNullOrWhiteSpace(targetDirectory) Then Directory.CreateDirectory(targetDirectory)
                Dim startInfo As New ProcessStartInfo With {
                    .FileName = "dnglab", .UseShellExecute = False,
                    .RedirectStandardOutput = False, .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                startInfo.ArgumentList.Add("convert")
                ' Ueber das vorhandene Ziel schreiben darf der Konverter nur, weil die Konfliktfrage
                ' im Stapel VORHER gestellt wurde: wer dort nicht ueberschreiben wollte, bekommt
                ' einen anderen Namen oder wird uebersprungen, und hier kommt gar kein Ziel an, das
                ' schon belegt ist. Ohne dieses Wort weist dnglab die vorhandene Datei ab, und der
                ' Lauf zaehlte eine Datei weniger, ohne zu sagen warum.
                startInfo.ArgumentList.Add("--override")
                startInfo.ArgumentList.Add(sourcePath)
                startInfo.ArgumentList.Add(targetPath)
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
                       process.ExitCode = 0 AndAlso File.Exists(targetPath) Then Return True
                    If Not String.IsNullOrWhiteSpace(standardError) Then DiagnosticLogService.LogAlways("DngConverter", standardError.Trim())
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("DngConverter", ex)
            End Try
            ' Ein abgebrochener oder gescheiterter Lauf laesst ein halbes DNG liegen. Es sieht wie
            ' ein Bild aus, ist aber keins.
            If Not targetExisted Then
                Try
                    If File.Exists(targetPath) Then File.Delete(targetPath)
                Catch
                End Try
            End If
            Return False
        End Function
    End Class
End Namespace
