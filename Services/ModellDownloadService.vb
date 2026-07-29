Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Holt Modelldateien auf ausdrueckliche Anforderung.
    '''
    ''' BEWUSST ein eigener Dienst, getrennt von KiModellService. Der darf nichts aus dem Netz holen
    ''' koennen, und ein Waechter haelt das fest - waere der Netzzugriff dort eingebaut, waere die
    ''' Zusage "laedt nie von selbst" nur noch ein Vorsatz. Hier gibt es Netzzugriff, und er hat
    ''' genau einen Aufrufer: den Knopf in den Einstellungen.
    '''
    ''' Zwei Dinge, die nicht verhandelbar sind:
    '''
    ''' Die PRUEFSUMME wird geprueft, und sie steht in der Anwendung. Ein Modell wird einer nativen
    ''' Laufzeit zum Fressen gegeben; wer die Datei unterschiebt, bestimmt, was dort laeuft. Eine
    ''' Pruefsumme, die mit der Datei kaeme, wuerde derselbe Angreifer mitliefern. Stimmt sie nicht,
    ''' wird die Datei geloescht statt benutzt.
    '''
    ''' Geschrieben wird ATOMAR: erst in eine Nachbardatei, dann umbenannt. Ein abgebrochener
    ''' Download darf keine halbe Datei am Zielort hinterlassen, die beim naechsten Start als
    ''' vorhandenes Modell gilt.</summary>
    Public NotInheritable Class ModellDownloadService

        Private Sub New()
        End Sub

        Private Shared ReadOnly _klient As New Lazy(Of HttpClient)(
            Function()
                Dim k = New HttpClient()
                k.Timeout = TimeSpan.FromMinutes(30)
                ' Kein Kennzeichen, das ueber die Anwendung hinaus etwas verraet.
                k.DefaultRequestHeaders.UserAgent.ParseAdd("FerrumPix")
                Return k
            End Function)

        Public Enum Ergebnis
            Fertig
            SchonDa
            Netzfehler
            PruefsummeFalsch
            Abgebrochen
        End Enum

        ''' <summary>Ein Modell holen. <paramref name="fortschritt"/> bekommt 0 bis 1.</summary>
        Public Shared Async Function HoleAsync(eintrag As KiModellService.ModellEintrag,
                                               Optional fortschritt As IProgress(Of Double) = Nothing,
                                               Optional abbruch As CancellationToken = Nothing) As Task(Of Ergebnis)
            If eintrag Is Nothing Then Return Ergebnis.Netzfehler
            If KiModellService.IstUnversehrt(eintrag) Then Return Ergebnis.SchonDa

            Dim ordner = KiModellService.ModellOrdner
            Dim ziel = Path.Combine(ordner, eintrag.Datei)
            ' Die Nachbardatei liegt im SELBEN Ordner - nur dann ist das Umbenennen ein
            ' Verzeichniseintrag und keine Kopie ueber Dateisystemgrenzen hinweg.
            Dim halb = ziel & ".unvollstaendig"
            Try
                Directory.CreateDirectory(ordner)
                If File.Exists(halb) Then File.Delete(halb)

                Using antwort = Await _klient.Value.GetAsync(eintrag.Adresse,
                                                             HttpCompletionOption.ResponseHeadersRead, abbruch)
                    If Not antwort.IsSuccessStatusCode Then Return Ergebnis.Netzfehler
                    Dim gesamt = If(antwort.Content.Headers.ContentLength.HasValue,
                                    antwort.Content.Headers.ContentLength.Value, eintrag.Bytes)
                    Using quelle = Await antwort.Content.ReadAsStreamAsync(abbruch)
                        Using senke = New FileStream(halb, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            Dim puffer(81919) As Byte
                            Dim gelesen As Integer
                            Dim summe As Long = 0
                            Do
                                gelesen = Await quelle.ReadAsync(puffer, 0, puffer.Length, abbruch)
                                If gelesen <= 0 Then Exit Do
                                Await senke.WriteAsync(puffer, 0, gelesen, abbruch)
                                summe += gelesen
                                If fortschritt IsNot Nothing AndAlso gesamt > 0 Then
                                    fortschritt.Report(Math.Min(1.0, summe / CDbl(gesamt)))
                                End If
                            Loop
                        End Using
                    End Using
                End Using

                ' ERST pruefen, DANN an den Zielort. Eine Datei, die den Namen des Modells traegt,
                ' soll nie einen Augenblick lang ungeprueft dort liegen.
                Dim summeIst = KiModellService.PruefsummeVon(halb)
                If Not String.Equals(summeIst, eintrag.Sha256, StringComparison.OrdinalIgnoreCase) Then
                    Try
                        File.Delete(halb)
                    Catch
                    End Try
                    DiagnosticLogService.LogAlways("ModellDownload",
                        $"{eintrag.Datei}: Pruefsumme {summeIst} statt {eintrag.Sha256}")
                    Return Ergebnis.PruefsummeFalsch
                End If

                If File.Exists(ziel) Then File.Delete(ziel)
                File.Move(halb, ziel)
                KiModellService.ErneutPruefen()
                Return Ergebnis.Fertig
            Catch ex As OperationCanceledException
                Try
                    If File.Exists(halb) Then File.Delete(halb)
                Catch
                End Try
                Return Ergebnis.Abgebrochen
            Catch ex As Exception
                Try
                    If File.Exists(halb) Then File.Delete(halb)
                Catch
                End Try
                DiagnosticLogService.LogAlways("ModellDownload", eintrag.Datei & ": " & ex.Message)
                Return Ergebnis.Netzfehler
            End Try
        End Function

    End Class

End Namespace
