Imports System
Imports System.Diagnostics

Namespace Services

    ''' <summary>Öffnet eine Adresse, eine Datei oder einen Ordner mit dem Programm, das das System
    ''' dafür vorsieht. EINE Stelle für alles, was FerrumPix nach draußen gibt: Karte im Browser,
    ''' PDF im Betrachter, Ordner im Dateimanager, Verweise aus den Einstellungen.
    '''
    ''' <para>Warum eigens dafür ein Dienst: derselbe Dreizeiler stand an sieben Stellen, und am
    ''' Öffnen hängt mehr als der Aufruf. Unter Wayland entscheidet der Fenstermanager, ob das
    ''' neue Fenster nach vorn darf; er lässt es nur zu, wenn ein AKTIVIERUNGS-TOKEN mitkommt. Das
    ''' wird hier weitergereicht, sofern die Sitzung eines vergeben hat, und danach verworfen - ein
    ''' Token gilt für genau einen Start.</para>
    '''
    ''' <para>WAS DAS NICHT LÖST, gemessen unter KDE auf Wayland: läuft das Zielprogramm bereits,
    ''' reicht der Systemöffner die Adresse an dessen laufende Instanz weiter, und ob die ihr
    ''' Fenster hervorholt, entscheidet sie selbst. Bei einem Chromium-Browser tut sie es nicht -
    ''' der Tab öffnet sich im Hintergrund. Ein noch nicht laufendes Programm kommt dagegen nach
    ''' vorn (mit gwenview gegengeprüft). Das ist Sache des Zielprogramms und des Fenstermanagers,
    ''' nicht dieses Dienstes; ein anderer Öffner (kde-open, gio open) verhält sich genauso.</para>
    '''
    ''' <para>FerrumPix läuft als X11-Anwendung (siehe Program.vb), unter Wayland also über
    ''' XWayland. Ein eigenes Token beim Compositor anzufordern ist von dort aus nicht möglich -
    ''' erreichbar ist nur, was in der Umgebung steht.</para></summary>
    Public NotInheritable Class ShellOpenService

        Private Sub New()
        End Sub

        ''' <summary>Der Name der Umgebungsvariablen, über die ein Compositor die Erlaubnis zum
        ''' Fokuswechsel weitergibt (Wayland). Auf X11 heißt das Gegenstück DESKTOP_STARTUP_ID.</summary>
        Private Const ActivationTokenVariable As String = "XDG_ACTIVATION_TOKEN"
        Private Const StartupIdVariable As String = "DESKTOP_STARTUP_ID"

        ''' <summary>Öffnet das Ziel. True, wenn der Start gelang - nicht, ob der Nutzer danach etwas
        ''' sieht: was das Zielprogramm tut, liegt außerhalb.</summary>
        ''' <param name="target">Adresse, Datei oder Ordner.</param>
        ''' <param name="source">Für das Protokoll, wenn es schiefgeht.</param>
        Public Shared Function Open(target As String, source As String) As Boolean
            If String.IsNullOrWhiteSpace(target) Then Return False
            Try
                Dim info As New ProcessStartInfo() With {
                    .FileName = target,
                    .UseShellExecute = True
                }

                ' Das Token gilt für GENAU EINEN Start und wird vom Compositor danach verworfen.
                ' Es wird deshalb weitergereicht und aus der eigenen Umgebung genommen, damit ein
                ' zweiter Aufruf nicht mit einem verbrauchten Token dasteht.
                Dim token = Environment.GetEnvironmentVariable(ActivationTokenVariable)
                If Not String.IsNullOrEmpty(token) Then
                    info.Environment(ActivationTokenVariable) = token
                    info.Environment(StartupIdVariable) = token
                    Environment.SetEnvironmentVariable(ActivationTokenVariable, Nothing)
                End If

                Process.Start(info)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException(source, ex)
                Return False
            End Try
        End Function

    End Class

End Namespace
