Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.EP.WebGpu

Namespace Services

    ''' <summary>Die Grafikkarte fuer die gelernten Modelle.
    '''
    ''' Bis hierher liefen alle Modelle auf dem Prozessor, und das aus einem guten Grund: der Weg
    ''' ueber eine Grafikkarte verlangte bisher je nach Hersteller ein eigenes Paket - eines fuer
    ''' Nvidia, eines fuer AMD, eines fuer Windows - und jedes mit eigenen Fehlerbildern. Fuer eine
    ''' Anwendung, die auf allen drei Systemen aus demselben Quelltext gebaut wird, war das kein
    ''' Handel.
    '''
    ''' Der Zusatz, der hier benutzt wird, aendert genau das: EINE Bibliothek je System, die
    ''' darunter das nimmt, was das System hat - Vulkan unter Linux, Direct3D unter Windows, Metal
    ''' unter macOS. Sie fragt den Hersteller der Karte nichts und braucht von ihm nur den
    ''' gewoehnlichen Grafiktreiber, den ein Rechner ohnehin hat. Der Name der Bibliothek traegt
    ''' "WebGPU", und das fuehrt in die Irre: es ist kein Browser beteiligt und nichts aus dem Netz.
    ''' Der Name kommt daher, dass die Schnittstelle urspruenglich fuer Browser entworfen wurde;
    ''' inzwischen ist sie eine gewoehnliche native Bibliothek.
    '''
    ''' AB WERK AUS. Nicht aus Vorsicht vor dem Ergebnis - gemessen rechnet dieselbe Datei auf der
    ''' Grafikkarte Bild fuer Bild dasselbe wie auf dem Prozessor, die Abweichung liegt im Bereich
    ''' der Rundung von Fliesskommazahlen. Sondern wegen des Speichers: eine eingebaute
    ''' Grafikeinheit mit knapp bemessenem Grafikspeicher faengt bei grossen Modellen an,
    ''' Speicherseiten hin und her zu schieben, und dann wird nicht nur das Modell langsamer,
    ''' sondern die ganze Oberflaeche zaeh. Wer weiss, was seine Karte kann, schaltet es ein; wer
    ''' nicht, verliert nichts.
    '''
    ''' WELCHES Modell auf die Karte darf, entscheidet NICHT dieser Dienst, sondern eine gemessene
    ''' Positivliste am Register-Eintrag (<c>AiModelService.ModelEntry.GpuAllowed</c>). Es gibt
    ''' Modelle, die dort langsamer rechnen, und eines, das die Laufzeit zum Absturz bringt.
    '''
    ''' Bricht irgendetwas davon, faellt der Weg auf den Prozessor zurueck. Eine fehlende
    ''' Bibliothek, ein Treiber ohne Vulkan, eine Karte, die das Modell nicht haelt: keiner dieser
    ''' Faelle darf eine Funktion kosten, er darf sie nur langsamer machen.</summary>
    Public NotInheritable Class GpuAccelerationService

        Private Sub New()
        End Sub

        ''' <summary>Wie eine echte Probe ausgegangen ist. Gefunden zu werden und zu laufen sind
        ''' zwei verschiedene Dinge: die Karte wird ueber die Geraeteliste des Systems gefunden,
        ''' auch wenn kein brauchbarer Treiber dahintersteht. Erst der Versuch, wirklich ein Modell
        ''' zu laden, beantwortet die zweite Frage.</summary>
        ''' Dass gerade geprobt wird, ist KEIN Wert hiervon (siehe _probeRunning): bis zum Ergebnis
        ''' gilt der letzte gueltige Stand weiter, sonst gaebe eine laufende Probe eine als
        ''' untauglich bekannte Karte schon frei.
        Public Enum ProbeResult
            Untested = 0
            Works = 2
            Failed = 3
        End Enum

        ''' <summary>Eine gefundene Karte, so wie sie in der Auswahl steht.
        '''
        ''' Der SCHLUESSEL ist das, was in den Einstellungen liegen bleibt. Er darf sich zwischen
        ''' zwei Programmstarts nicht aendern, sonst zeigt die gespeicherte Wahl irgendwann ins
        ''' Leere. Deshalb steckt darin, was das Geraet AUSMACHT - Hersteller, Modell und der
        ''' Steckplatz -, und ausdruecklich NICHT die Stelle in der Liste: eine zweite Karte, die
        ''' dazukommt, verschoebe sonst die Bedeutung der ersten.</summary>
        Public NotInheritable Class GpuDeviceInfo
            Public Property Key As String = ""
            Public Property VendorName As String = ""
            Public Property DeviceName As String = ""
            Public Property DeviceIdText As String = ""
            Public Property IsDiscrete As Boolean = False
            Public Property IsKindKnown As Boolean = False
        End Class

        ''' <summary>Der Name, unter dem die Bibliothek bei der Laufzeit angemeldet ist. Nur einmal
        ''' je Programmlauf; ein zweites Anmelden desselben Namens ist ein Fehler.</summary>
        Private Const RegistrationName As String = "ferrumpix_webgpu"

        Private Shared ReadOnly _lock As New Object()
        Private Shared _checked As Boolean = False
        Private Shared _found As New List(Of OrtEpDevice)()
        Private Shared _infos As New List(Of GpuDeviceInfo)()
        Private Shared _devices As IReadOnlyList(Of OrtEpDevice) = New List(Of OrtEpDevice)()
        Private Shared _active As GpuDeviceInfo = Nothing
        ''' Die zuletzt angewandte Einstellung. Nothing heisst "noch nie angewandt" - und ist damit
        ''' von der leeren Einstellung ("automatisch") unterscheidbar.
        Private Shared _appliedChoice As String = Nothing
        Private Shared _libraryMissing As Boolean = False
        Private Shared _setupError As String = ""
        Private Shared _probe As ProbeResult = ProbeResult.Untested
        ''' Laeuft gerade eine Probe? Getrennt vom Stand, siehe ProbeResult.
        Private Shared _probeRunning As Boolean
        ''' Warum die LETZTE Probe ohne Ergebnis blieb, sonst leer. Ein Hinweis, kein Stand: der
        ''' bisherige Stand bleibt, aber der Benutzer soll sehen, dass die Pruefung nichts belegt hat.
        Private Shared _probeNote As String = ""
        ''' Zu welcher Karte der Hinweis gehoert. Er erscheint nur bei ihr.
        Private Shared _probeNoteKey As String = ""
        Private Shared _probeError As String = ""

        ''' <summary>Steht eine Karte bereit, die die Laufzeit ansprechen kann?</summary>
        Public Shared ReadOnly Property Available As Boolean
            Get
                EnsureActive()
                Return _devices.Count > 0
            End Get
        End Property

        ''' <summary>Alle gefundenen Karten. Bei mehr als einer darf der Benutzer waehlen - welche
        ''' die richtige ist, weiss nur er: in einem Rechner mit eingebauter UND eigener Karte
        ''' haengt die Antwort daran, was sonst noch darauf laeuft.</summary>
        Public Shared ReadOnly Property Devices As IReadOnlyList(Of GpuDeviceInfo)
            Get
                CheckOnce()
                Return _infos
            End Get
        End Property

        ''' <summary>Die Karte, die gerade benutzt wuerde. Nothing, wenn es keine gibt.</summary>
        Public Shared ReadOnly Property ActiveDevice As GpuDeviceInfo
            Get
                EnsureActive()
                Return _active
            End Get
        End Property

        ''' <summary>Der Schluessel der benutzten Karte, oder leer. Er geht in die Sitzungsliste
        ''' von <see cref="AiModelService"/>: wird eine andere Karte gewaehlt, sind die offenen
        ''' Sitzungen fuer die alte gebaut und muessen neu entstehen.</summary>
        Public Shared ReadOnly Property ActiveDeviceKey As String
            Get
                EnsureActive()
                Return If(_active?.Key, "")
            End Get
        End Property

        ''' <summary>Fehlt die Bibliothek fuer dieses System? Trifft die Bauformen, fuer die es sie
        ''' nicht gibt - dort ist das keine Stoerung, sondern eine Tatsache.</summary>
        Public Shared ReadOnly Property LibraryMissing As Boolean
            Get
                CheckOnce()
                Return _libraryMissing
            End Get
        End Property

        ''' <summary>Warum das Anmelden nicht geklappt hat, sonst leer.</summary>
        Public Shared ReadOnly Property SetupError As String
            Get
                CheckOnce()
                Return _setupError
            End Get
        End Property

        ''' <summary>Der Hersteller der benutzten Karte, so gut er sich feststellen laesst.</summary>
        Public Shared ReadOnly Property VendorName As String
            Get
                Return If(ActiveDevice?.VendorName, "")
            End Get
        End Property

        ''' <summary>Der Modellname der benutzten Karte, wenn er sich feststellen laesst.</summary>
        Public Shared ReadOnly Property DeviceName As String
            Get
                Return If(ActiveDevice?.DeviceName, "")
            End Get
        End Property

        ''' <summary>Die Kennung der benutzten Karte. Kein Klarname, aber genug, um zwei Karten
        ''' auseinanderzuhalten.</summary>
        Public Shared ReadOnly Property DeviceIdText As String
            Get
                Return If(ActiveDevice?.DeviceIdText, "")
            End Get
        End Property

        ''' <summary>Wieviel eigenen Speicher die benutzte Karte hat, in Mebibyte. 0 heisst
        ''' unbekannt - dann darf niemand daraus etwas ableiten.
        '''
        ''' Die Zahl kommt von Vulkan (<see cref="VulkanMemoryService"/>), weil sie sonst nirgends
        ''' steht: die Laufzeit nennt sie nicht, Skia kennt nur seinen eigenen Zwischenspeicher, und
        ''' unter .NET gibt es sie gar nicht. Gebraucht wird sie dort, wo die Rechnung in Kacheln
        ''' zerlegt wird - eine grosse Kachel ist schneller, aber auf einer kleinen Karte geht ihr
        ''' der Speicher aus, und zwar mit einem Abbruch des Prozesses statt einer Meldung.
        '''
        ''' Unter macOS bleibt es bei 0: dort rechnet die Laufzeit ueber Metal, und dort tritt der
        ''' Fall nicht auf (geteilter Arbeitsspeicher, weit ueber dem Bedarf).</summary>
        Public Shared ReadOnly Property DeviceMemoryMiB As Integer
            Get
                Return DeviceMemoryMiBFor(ActiveDeviceKey)
            End Get
        End Property

        ''' <summary>Der eigene Speicher EINER BESTIMMTEN Karte, in Mebibyte. 0 heisst unbekannt.
        '''
        ''' Wer einen laufenden Auftrag bemisst, fragt mit dem Schluessel, unter dem dessen Sitzung
        ''' gebaut wurde, und nicht nach der gerade aktiven Karte: zwischen dem Bau der Sitzung und
        ''' der Rechnung kann eine andere gewaehlt worden sein, und dann gehoerte die Zahl zur
        ''' falschen Karte.</summary>
        Public Shared Function DeviceMemoryMiBFor(deviceKey As String) As Integer
            If String.IsNullOrEmpty(deviceKey) Then Return 0
            Return VulkanMemoryService.DeviceLocalMiBFor(deviceKey)
        End Function

        ''' <summary>Der Klarname EINER BESTIMMTEN Karte, fuer das Protokoll.
        '''
        ''' Der Schluessel taugt dafuer nicht: "10de:1287:0000:01:00.0" sagt einem Melder nichts,
        ''' und genau dorthin geht diese Zeile. Ist der Schluessel leer, wurde auf dem Prozessor
        ''' gerechnet; ist er unbekannt, steht er selbst da - besser als gar nichts.</summary>
        Public Shared Function DeviceLabelFor(deviceKey As String) As String
            If String.IsNullOrEmpty(deviceKey) Then Return "Prozessor"
            Dim info As GpuDeviceInfo = Nothing
            SyncLock _lock
                info = _infos.FirstOrDefault(Function(i) String.Equals(i.Key, deviceKey, StringComparison.Ordinal))
            End SyncLock
            If info Is Nothing Then Return deviceKey
            Dim name = $"{info.VendorName} {info.DeviceName}".Trim()
            Return If(name.Length = 0, deviceKey, name)
        End Function

        ''' <summary>Steckt die benutzte Karte fuer sich, oder sitzt sie im Prozessor? Das ist die
        ''' Unterscheidung, an der haengt, ob sich das Einschalten lohnt.</summary>
        Public Shared ReadOnly Property IsDiscrete As Boolean
            Get
                Return If(ActiveDevice?.IsDiscrete, False)
            End Get
        End Property

        ''' <summary>Ist ueberhaupt bekannt, welche der beiden Bauformen vorliegt?</summary>
        Public Shared ReadOnly Property IsKindKnown As Boolean
            Get
                Return If(ActiveDevice?.IsKindKnown, False)
            End Get
        End Property

        ''' <summary>Worueber die Karte angesprochen wird. Ein technischer Name, der nicht uebersetzt
        ''' wird - er steht so in jeder Treiberdokumentation.</summary>
        Public Shared ReadOnly Property BackendName As String
            Get
                If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then Return "Direct3D 12"
                If RuntimeInformation.IsOSPlatform(OSPlatform.OSX) Then Return "Metal"
                Return "Vulkan"
            End Get
        End Property

        ''' <summary>Der Stand der echten Probe. Ist in diesem Programmlauf noch nicht geprobt worden,
        ''' gilt, was fuer diese Karte gemerkt ist (AppSettings.GpuProbeResults).</summary>
        Public Shared ReadOnly Property ProbeState As ProbeResult
            Get
                If _probe = ProbeResult.Untested Then RestoreRememberedProbe()
                Return _probe
            End Get
        End Property

        ''' <summary>Den gemerkten Ausgang der Probe fuer die aktive Karte holen. Bleibt Untested, wenn
        ''' fuer sie noch nichts gemerkt ist.</summary>
        Private Shared Sub RestoreRememberedProbe()
            ' Unter dem Schloss des Kartenwechsels, aus demselben Grund wie in NoteProbe: Karte
            ' lesen und Stand setzen gehoeren zusammen.
            SyncLock _lock
                Dim key = _active?.Key
                If String.IsNullOrEmpty(key) OrElse _probe <> ProbeResult.Untested Then Return
                Dim stored As String = Nothing
                Try
                    Dim results = AppSettingsService.Load().GpuProbeResults
                    If results Is Nothing OrElse Not results.TryGetValue(key, stored) Then Return
                Catch
                    Return
                End Try
                If String.Equals(stored, "works", StringComparison.Ordinal) Then
                    _probe = ProbeResult.Works
                    _probeError = ""
                ElseIf stored IsNot Nothing AndAlso stored.StartsWith("failed", StringComparison.Ordinal) Then
                    _probe = ProbeResult.Failed
                    _probeError = If(stored.Length > 7, stored.Substring(7), "")
                End If
            End SyncLock
        End Sub

        Private Shared Sub RememberProbe(deviceKey As String, result As String)
            If String.IsNullOrEmpty(deviceKey) Then Return
            Try
                AppSettingsService.SaveGpuProbeResult(deviceKey, result)
            Catch ex As Exception
                DiagnosticLogService.LogException("Grafik.RememberProbe", ex)
            End Try
        End Sub

        ''' <summary>Warum die letzte Probe ohne Ergebnis blieb, schon uebersetzt; sonst leer.</summary>
        Public Shared ReadOnly Property ProbeNote As String
            Get
                SyncLock _lock
                    If Not String.Equals(_probeNoteKey, _active?.Key, StringComparison.Ordinal) Then Return ""
                    Return _probeNote
                End SyncLock
            End Get
        End Property

        Private Shared Sub SetProbeNote(deviceKey As String, note As String)
            SyncLock _lock
                _probeNote = If(note, "")
                _probeNoteKey = If(deviceKey, "")
            End SyncLock
        End Sub

        ''' <summary>Ist diese Karte als untauglich bekannt, im Speicher (aktive Karte) oder gemerkt?</summary>
        Public Shared Function IsCardKnownFailed(deviceKey As String) As Boolean
            If String.IsNullOrEmpty(deviceKey) Then Return False
            SyncLock _lock
                If String.Equals(deviceKey, _active?.Key, StringComparison.Ordinal) AndAlso _probe <> ProbeResult.Untested Then
                    Return _probe = ProbeResult.Failed
                End If
                Try
                    Dim stored As String = Nothing
                    Dim results = AppSettingsService.Load().GpuProbeResults
                    Return results IsNot Nothing AndAlso results.TryGetValue(deviceKey, stored) AndAlso
                           stored IsNot Nothing AndAlso stored.StartsWith("failed", StringComparison.Ordinal)
                Catch
                    Return False
                End Try
            End SyncLock
        End Function

        ''' <summary>Woran die Probe gescheitert ist, sonst leer.</summary>
        Public Shared ReadOnly Property ProbeError As String
            Get
                Return _probeError
            End Get
        End Property

        ''' <summary>Hat der Benutzer die Beschleunigung eingeschaltet?</summary>
        Public Shared ReadOnly Property Enabled As Boolean
            Get
                Try
                    Return AppSettingsService.Load().GpuAccelerationEnabled
                Catch
                    Return False
                End Try
            End Get
        End Property

        ''' <summary>Soll das naechste Modell auf der Karte laufen? Eingeschaltet, vorhanden, und die
        ''' Karte ist nicht als untauglich bekannt. Ob das jeweilige MODELL darf, entscheidet
        ''' <see cref="AiModelService.IsGpuAllowed"/>.
        '''
        ''' DER GEMERKTE FEHLER ZAEHLT MIT. Ohne ihn baute jeder Modellzugriff nach einem
        ''' gescheiterten Anhaengen erneut: die Prozessor-Sitzung passte nicht zum gewuenschten
        ''' Geraet, und mit dem Neuaufbau wurde auch die Karte wieder versucht, bei jeder Nutzung.
        ''' Freigegeben wird sie durch eine neue Probe (Knopf "Pruefen" in den Einstellungen).
        '''
        ''' Gefragt wird VOR jedem Schloss in AiModelService (siehe LoadSession); ProbeState nimmt
        ''' das Schloss dieses Dienstes, und ein Rueckweg in umgekehrter Reihenfolge waere die
        ''' Verklemmung aus FALLEN_UND_ENTSCHEIDUNGEN.</summary>
        Public Shared ReadOnly Property ShouldUse As Boolean
            Get
                Return Enabled AndAlso Available AndAlso ProbeState <> ProbeResult.Failed
            End Get
        End Property

        ''' <summary>Die Karte an eine Sitzungsoption haengen. False heisst: es bleibt beim
        ''' Prozessor, und der Aufrufer soll ohne sie weitermachen.
        '''
        ''' <paramref name="deviceKey"/> nennt die Karte, die WIRKLICH angehaengt wurde. Der
        ''' Aufrufer darf sie NICHT vorher selbst bestimmen und danach als gesetzt annehmen: hier
        ''' laeuft <see cref="EnsureActive"/>, und das liest die Auswahl des Benutzers neu. Wird
        ''' zwischen den beiden Zeitpunkten eine andere Karte gewaehlt, entsteht die Sitzung auf
        ''' Karte B, waehrend der Aufrufer noch Karte A im Kopf hat - und wer daran seine
        ''' Kachelgroesse bemisst, bekommt die grosse Kachel auf der kleinen Karte, also genau den
        ''' Absturz, der vermieden werden soll.
        '''
        ''' Deshalb wird die Geraeteliste unter dem Schloss als MOMENTAUFNAHME genommen und der
        ''' Schluessel aus derselben Aufnahme zurueckgegeben. Was danach gewaehlt wird, betrifft
        ''' erst die naechste Sitzung.</summary>
        Public Shared Function TryApply(options As SessionOptions, ByRef deviceKey As String) As Boolean
            Dim attempted As String = ""
            Return TryApply(options, deviceKey, attempted)
        End Function

        ''' <summary>Wie oben, und <paramref name="attemptedKey"/> nennt die Karte, an die
        ''' angehaengt werden SOLLTE, auch wenn das Anhaengen scheitert. Die Probe braucht das: ihr
        ''' Scheitern gehoert zu dieser Karte und soll dort gemerkt werden, sonst gaelte sie nach
        ''' einem Neustart wieder als ungeprueft und wuerde erneut angeboten.</summary>
        Public Shared Function TryApply(options As SessionOptions, ByRef deviceKey As String, ByRef attemptedKey As String) As Boolean
            deviceKey = ""
            attemptedKey = ""
            If options Is Nothing Then Return False
            EnsureActive()
            Dim devices As List(Of OrtEpDevice)
            Dim applied As String
            SyncLock _lock
                If _devices.Count = 0 OrElse _active Is Nothing Then Return False
                devices = New List(Of OrtEpDevice)(_devices)
                applied = _active.Key
            End SyncLock
            attemptedKey = applied
            Try
                options.AppendExecutionProvider(OrtEnv.Instance(), devices, New Dictionary(Of String, String)())
                deviceKey = applied
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Grafik", $"Beschleunigung laesst sich nicht anhaengen: {ex.Message}")
                Return False
            End Try
        End Function

        ''' <summary>Ein Modell ist wirklich auf der Karte angelaufen.</summary>
        ''' <param name="deviceKey">Die Karte, an die die Sitzung WIRKLICH gehaengt wurde (aus
        ''' <see cref="TryApply"/>), nicht die gerade aktive. Zwischen Anhaengen und Ergebnis kann
        ''' in den Einstellungen eine andere gewaehlt worden sein; das Ergebnis gehoerte sonst
        ''' dauerhaft zur falschen.</param>
        Public Shared Sub NoteSuccess(deviceKey As String)
            NoteProbe(deviceKey, ProbeResult.Works, "")
        End Sub

        ''' <summary>Ein Modell ist auf der Karte NICHT angelaufen. Der Text ist die Meldung der
        ''' Laufzeit; sie wird gekuerzt, weil sie mehrere Zeilen Dateipfade aus dem Bauverzeichnis
        ''' der Laufzeit mitbringt, die niemandem helfen.</summary>
        Public Shared Sub NoteFailure(deviceKey As String, message As String)
            NoteProbe(deviceKey, ProbeResult.Failed, ShortMessage(message))
        End Sub

        ''' <summary>Merken immer unter der Karte, auf der geprobt wurde. Der Stand im Speicher
        ''' gilt dagegen der AKTIVEN Karte und wird nur gesetzt, wenn es dieselbe ist.
        '''
        ''' Vergleich und Setzen stehen unter DEMSELBEN Schloss wie der Kartenwechsel
        ''' (EnsureActive): wechselte die Karte genau dazwischen, bekaeme die neue den Stand der
        ''' alten.</summary>
        Private Shared Sub NoteProbe(deviceKey As String, result As ProbeResult, message As String)
            ' Speichern und Setzen in EINER Reihenfolge: liefen zwei Sitzungsaufbauten auf derselben
            ' Karte gleichzeitig zu Ende, stuende sonst im Programm "laeuft" und in der Datei
            ' "gescheitert", und nach dem Neustart gaelte die Datei.
            ' OHNE KARTE gehoert der Fehler keiner: gescheitert, bevor eine angehaengt werden sollte
            ' (etwa beim Anlegen der Optionen). Er darf dann auch nicht bei der gerade aktiven landen,
            ' die womoeglich eine andere ist. Das "Pruefung laeuft" der Probe loest Probe selbst auf.
            If String.IsNullOrEmpty(deviceKey) Then Return
            SyncLock _lock
                RememberProbe(deviceKey, If(result = ProbeResult.Works, "works", "failed:" & message))
                If Not String.Equals(deviceKey, _active?.Key, StringComparison.Ordinal) Then Return
                _probe = result
                _probeError = message
            End SyncLock
        End Sub

        ''' <summary>Die Meldung auf das kuerzen, was einen Menschen angeht: den Satz hinter den
        ''' Pfadangaben aus dem Quelltext der Laufzeit.</summary>
        Private Shared Function ShortMessage(message As String) As String
            If String.IsNullOrWhiteSpace(message) Then Return ""
            Dim text = message.Replace(vbCr, " ").Replace(vbLf, " ").Trim()
            Dim marker = text.LastIndexOf(" was false. ", StringComparison.Ordinal)
            If marker >= 0 Then text = text.Substring(marker + 12)
            If text.Length > 200 Then text = text.Substring(0, 200)
            Return text.Trim()
        End Function

        ''' <summary>Proben laufen NACHEINANDER. Einstellungen und Modelldialog starten sie
        ''' unabhaengig; parallel teilten sie sich Laufmarkierung und Stand, die erste fertige setzte
        ''' die Markierung der anderen zurueck, und eine konnte das Ergebnis der anderen zurueckgeben.
        ''' Wer wartet, probt danach selbst und bekommt SEIN Ergebnis.</summary>
        Private Shared ReadOnly _probeGate As New Object()

        ''' <summary>Wirklich nachsehen, ob ein Modell auf der Karte laeuft.
        '''
        ''' Dafuer wird die kleinste vorhandene freigegebene Modelldatei genommen und eine Sitzung
        ''' damit gebaut - der Punkt, an dem die Laufzeit die Karte tatsaechlich anfordert. Die
        ''' Sitzung wird sofort wieder geschlossen; sie soll nichts, ausser die Frage zu beantworten.
        ''' GERECHNET wird dabei nichts.
        '''
        ''' KARTE UND MODELL SIND ZWEI FEHLER, wie beim normalen Modellaufbau. Scheitert das
        ''' Anhaengen, taugt die Karte nicht. Scheitert nach erfolgreichem Anhaengen die Sitzung,
        ''' kann es am Modell liegen (beschaedigte Datei, Speicher reicht gerade fuer dieses nicht):
        ''' dann wird nur dieses Modell auf dieser Karte gesperrt und das naechste versucht. Laeuft
        ''' keines an, belegt auch das KEINEN Kartenfehler (es kann das einzige vorhandene Modell
        ''' sein, und dessen Datei beschaedigt): die Probe endet ohne Ergebnis und aendert nichts.
        ''' Laeuft eines an, wird seine Sperre aufgehoben, denn die Probe hat es gerade bestaetigt.
        '''
        ''' ALLE VERSUCHE GEHOEREN EINER KARTE, der des ersten Anhaengens. Wird waehrend der Probe
        ''' umgewaehlt und haengt ein spaeterer Versuch an einer anderen an, endet die Probe ohne
        ''' Ergebnis, statt Versuche zweier Karten zu einem Urteil zu vermischen.
        '''
        ''' Ist keine Modelldatei da, bleibt die Frage offen: ohne Modell gibt es auch nichts zu
        ''' beschleunigen.
        '''
        ''' Braucht Zeit - gehoert also NICHT auf den Oberflaechenfaden.</summary>
        ''' <returns>Das Ergebnis DIESER Probe, nicht der gemeinsame Stand.</returns>
        Public Shared Function Probe() As ProbeResult
            SyncLock _probeGate
                Return ProbeCore()
            End SyncLock
        End Function

        Private Shared Function ProbeCore() As ProbeResult
            EnsureActive()
            ' Jede neue Probe loescht den Hinweis der vorigen, auch wenn sie gleich wieder umkehrt.
            SetProbeNote("", "")
            If _devices.Count = 0 Then
                SyncLock _lock
                    _probe = ProbeResult.Failed
                    _probeError = ""
                End SyncLock
                Return ProbeResult.Failed
            End If

            Dim files = ProbeModelFiles()
            If files.Count = 0 Then
                SyncLock _lock
                    _probe = ProbeResult.Untested
                    _probeError = ""
                End SyncLock
                Return ProbeResult.Untested
            End If

            ' Der Stand bleibt bis zum Ergebnis, was er war; nur die Markierung sagt, dass geprobt wird.
            _probeRunning = True
            ' Fuer welche Karte diese Probe laeuft. Scheitert sie, bevor eine Karte feststeht, gilt
            ' ihr Scheitern nur fuer DIESE (ResolveProbeWithoutCard).
            Dim startedFor = ActiveDeviceKey
            Try
                Dim lastModelError = ""
                Dim attachedOn = ""
                For Each file In files
                    ' Vor dem Try: auch das Scheitern gehoert zu der Karte, auf der geprobt wurde.
                    ' Scheitert schon das Anhaengen, ist das die Karte, an die angehaengt werden SOLLTE.
                    Dim probedOn As String = ""
                    Dim attemptedOn As String = ""
                    Try
                        Using options = New SessionOptions()
                            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                            If Not TryApply(options, probedOn, attemptedOn) Then
                                ' Das Anhaengen selbst: ein Fehler der KARTE.
                                If attemptedOn.Length > 0 Then
                                    NoteFailure(attemptedOn, "")
                                Else
                                    ResolveProbeWithoutCard(startedFor, "")
                                End If
                                Return ProbeResult.Failed
                            End If
                            If attachedOn.Length > 0 AndAlso Not String.Equals(attachedOn, probedOn, StringComparison.Ordinal) Then
                                DiagnosticLogService.LogAlways("Grafik", "Probe abgebrochen: waehrend der Probe wurde eine andere Karte gewaehlt")
                                SetProbeNote(startedFor, LocalizationService.T("Die letzte Prüfung blieb ohne Ergebnis, weil währenddessen eine andere Karte gewählt wurde."))
                                Return ProbeResult.Untested
                            End If
                            attachedOn = probedOn
                            Using session = New InferenceSession(file, options)
                            End Using
                        End Using
                        AiModelService.ClearGpuFailedModel(Path.GetFileName(file), probedOn)
                        NoteSuccess(probedOn)
                        DiagnosticLogService.LogAlways("Grafik", $"Probe bestanden auf {VendorName} {DeviceName} ({BackendName}) mit {Path.GetFileName(file)}")
                        Return ProbeResult.Works
                    Catch ex As Exception
                        If probedOn.Length = 0 Then
                            ' Gescheitert, bevor eine Karte angehaengt war (etwa beim Anlegen der
                            ' Optionen): keiner Karte zuzuordnen.
                            Dim key = attemptedOn
                            If key.Length > 0 Then
                                NoteFailure(key, ex.Message)
                            Else
                                ResolveProbeWithoutCard(startedFor, ShortMessage(ex.Message))
                            End If
                            DiagnosticLogService.LogAlways("Grafik", $"Probe gescheitert: {ShortMessage(ex.Message)}")
                            Return ProbeResult.Failed
                        End If
                        ' Angehaengt, aber die Sitzung mit DIESEM Modell scheiterte: nur das Modell
                        ' sperren und das naechste versuchen. Laeuft das Modell aber schon auf dieser
                        ' Karte, belegt ein Fehlschlag der ZUSAETZLICHEN Probesitzung nichts gegen es
                        ' (der Speicher kann von der laufenden belegt sein), und die laufende Sitzung
                        ' bliebe sonst beim naechsten Zugriff auf der Strecke.
                        lastModelError = ShortMessage(ex.Message)
                        AiModelService.NoteGpuFailedModelUnlessRunning(Path.GetFileName(file), probedOn)
                        DiagnosticLogService.LogAlways("Grafik",
                            $"Probe mit {Path.GetFileName(file)} gescheitert, naechstes Modell: {lastModelError}")
                    End Try
                Next
                ' Kein Modell lief, obwohl die Karte jedes Mal angehaengt war. Das liegt an den
                ' Modellen oder ist ungeklaert, ein Kartenfehler ist es nicht belegt: nichts aendern.
                DiagnosticLogService.LogAlways("Grafik", $"Probe ohne Ergebnis, kein Modell lief auf der Karte: {lastModelError}")
                SetProbeNote(attachedOn, LocalizationService.T("Die letzte Prüfung blieb ohne Ergebnis: auf der Karte lief keines der vorhandenen Modelle an. Das muss nicht an der Karte liegen."))
                Return ProbeResult.Untested
            Finally
                _probeRunning = False
            End Try
        End Function

        ''' <summary>Die Probe ist gescheitert, bevor eine Karte feststand. Das gilt als ihr Ergebnis,
        ''' aber nur, wenn die Karte, fuer die sie gestartet wurde, noch die aktive ist; sonst bleibt
        ''' der Stand der neuen, wie er ist. Gemerkt wird nichts: ohne Karte gibt es keinen
        ''' Schluessel, unter dem es stehen koennte.</summary>
        Private Shared Sub ResolveProbeWithoutCard(startedFor As String, message As String)
            SyncLock _lock
                If Not _probeRunning Then Return
                If Not String.Equals(startedFor, _active?.Key, StringComparison.Ordinal) Then Return
                _probe = ProbeResult.Failed
                _probeError = message
            End SyncLock
        End Sub

        ''' <summary>Die kleinste Modelldatei, die vorliegt UND auf der Karte rechnen darf - die Probe
        ''' soll den Rechner nicht minutenlang beschaeftigen. Die Ortstabelle faellt heraus, sie ist
        ''' kein Modell. Ein Modell ausserhalb der Positivliste (GpuAllowed) wird NIE auf die Karte
        ''' gegeben, auch nicht zur Probe: der MobileSAM-Dekodierer etwa ist ausdruecklich nicht
        ''' freigegeben, und lagen nur die beiden Dateien der Objektauswahl vor, war er die kleinste.</summary>
        Private Shared Function SmallestModelFile() As String
            Dim files = ProbeModelFiles()
            Return If(files.Count > 0, files(0), "")
        End Function

        ''' <summary>Alle vorhandenen freigegebenen Modelldateien, kleinste zuerst. Die Probe geht sie
        ''' der Reihe nach durch, falls eine an sich selbst scheitert.</summary>
        Private Shared Function ProbeModelFiles() As List(Of String)
            Dim result As New List(Of String)()
            Try
                For Each entry In AiModelService.KnownEntries.
                        Where(Function(e) e.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) AndAlso e.GpuAllowed).
                        OrderBy(Function(e) e.Bytes)
                    Dim fileName = AiModelService.BestFile(entry.Key)
                    If String.IsNullOrEmpty(fileName) Then Continue For
                    Dim path = AiModelService.ModelPath(fileName)
                    If Not String.IsNullOrEmpty(path) Then result.Add(path)
                Next
            Catch ex As Exception
                ' Ohne Spur waere hinterher nicht zu unterscheiden, ob keine Modelldatei vorliegt
                ' oder ob das Nachsehen selbst gescheitert ist - und die Probe meldete beides als
                ' "ungeprueft".
                DiagnosticLogService.LogAlways("Grafik",
                    $"Modelldateien fuer die Probe lassen sich nicht bestimmen: {ex.Message}")
            End Try
            Return result
        End Function

        ''' <summary>Welche Karte hat der Benutzer gewaehlt? Leer heisst "such dir eine aus".</summary>
        Private Shared Function ChosenKey() As String
            Try
                Return If(AppSettingsService.Load().GpuAccelerationDevice, "").Trim()
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Die Wahl anwenden, falls sie sich geaendert hat.
        '''
        ''' Zeigt die gespeicherte Wahl ins Leere - die Karte wurde ausgebaut, der Rechner ist ein
        ''' anderer -, wird die Vorauswahl genommen statt gar keine. Eine Einstellung, die auf ein
        ''' verschwundenes Geraet zeigt, darf die Funktion nicht abschalten.</summary>
        Private Shared Sub EnsureActive()
            CheckOnce()
            SyncLock _lock
                If _found.Count = 0 Then Return
                Dim wanted = ChosenKey()
                If _appliedChoice IsNot Nothing AndAlso _appliedChoice = wanted AndAlso _active IsNot Nothing Then Return
                _appliedChoice = wanted

                Dim index = -1
                If wanted.Length > 0 Then
                    index = _infos.FindIndex(Function(i) String.Equals(i.Key, wanted, StringComparison.Ordinal))
                End If
                If index < 0 Then
                    ' Vorauswahl: eine eigene Karte hat ihren eigenen Speicher und faellt der
                    ' Oberflaeche nicht in den Ruecken.
                    index = _infos.FindIndex(Function(i) i.IsDiscrete)
                    If index < 0 Then index = 0
                End If

                Dim previousKey = If(_active?.Key, "")
                _active = _infos(index)
                _devices = New List(Of OrtEpDevice) From {_found(index)}
                If previousKey <> _active.Key Then
                    ' Andere Karte, alte Probe wertlos, und ihr Hinweis mit ihr.
                    _probe = ProbeResult.Untested
                    _probeError = ""
                    _probeNote = ""
                    _probeNoteKey = ""
                    DiagnosticLogService.LogAlways("Grafik",
                        $"benutzt {_active.VendorName} {_active.DeviceName} {_active.DeviceIdText}")
                End If
            End SyncLock
        End Sub

        ''' <summary>Einmal nachsehen, was es an Karten gibt. Kostet wenige Millisekunden - die
        ''' Bibliothek wird dabei angemeldet und die Geraeteliste gelesen, aber noch keine Karte
        ''' angefordert.</summary>
        Private Shared Sub CheckOnce()
            SyncLock _lock
                If _checked Then Return
                _checked = True
                Try
                    ' Erst die Laufzeit selbst. Sie schaltet dabei ihre Telemetrie ab, und das soll
                    ' geschehen sein, bevor hier irgendetwas anderes sie benutzt.
                    If Not AiModelService.RuntimeAvailable Then
                        _setupError = AiModelService.RuntimeError
                        Return
                    End If

                    Dim path As String = ""
                    Try
                        path = WebGpuEp.GetLibraryPath()
                    Catch
                        path = ""
                    End Try
                    If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then
                        ' Fuer diese Bauform gibt es die Bibliothek nicht. Kein Fehler.
                        _libraryMissing = True
                        DiagnosticLogService.LogAlways("Grafik", "Beschleunigung fuer diese Plattform nicht dabei")
                        Return
                    End If

                    OrtEnv.Instance().RegisterExecutionProviderLibrary(RegistrationName, path)

                    Dim gemeldet = OrtEnv.Instance().GetEpDevices().
                        Where(Function(d) String.Equals(d.EpName, WebGpuEp.GetEpName(), StringComparison.Ordinal)).
                        ToList()
                    Dim daten = gemeldet.Select(AddressOf FactsOf).ToList()

                    ' EIN SOFTWARE-RENDERER IST KEINE KARTE. Vulkan meldet auf vielen Linux-Systemen
                    ' auch dann ein Geraet, wenn gar keine Grafikkarte antwortet: Mesa bringt
                    ' lavapipe/llvmpipe mit, und das rechnet auf dem Prozessor. Es als Karte zu
                    ' nehmen kehrt den Zweck um - es ist LANGSAMER als der gewoehnliche Weg ueber
                    ' den Prozessor, und der Speicher, den es meldet, ist Arbeitsspeicher. Daran
                    ' bemisst sich die Kachelgroesse, und die faellt dann viel zu gross aus.
                    _found = New List(Of OrtEpDevice)()
                    _infos = New List(Of GpuDeviceInfo)()
                    Dim skipped = New List(Of String)()
                    For i = 0 To gemeldet.Count - 1
                        If IsSoftwareRenderer(daten(i)) Then
                            skipped.Add($"{daten(i).VendorName} {daten(i).DeviceName}".Trim())
                        Else
                            _found.Add(gemeldet(i))
                            _infos.Add(daten(i))
                        End If
                    Next
                    If skipped.Count > 0 Then
                        DiagnosticLogService.LogAlways("Grafik",
                            "uebergangen, weil auf dem Prozessor gerechnet wuerde: " & String.Join(", ", skipped))
                    End If
                    If _found.Count = 0 Then
                        DiagnosticLogService.LogAlways("Grafik", "keine geeignete Grafikkarte gefunden")
                        Return
                    End If

                    DiagnosticLogService.LogAlways("Grafik",
                        $"{_found.Count} Grafikkarte(n) ueber {BackendName}: " &
                        String.Join(", ", _infos.Select(Function(i) $"{i.VendorName} {i.DeviceName} {i.DeviceIdText}".Trim())))
                Catch ex As Exception
                    _setupError = ex.GetType().Name & ": " & ShortMessage(ex.Message)
                    _found = New List(Of OrtEpDevice)()
                    _infos = New List(Of GpuDeviceInfo)()
                    _devices = New List(Of OrtEpDevice)()
                    DiagnosticLogService.LogAlways("Grafik", $"Beschleunigung steht nicht bereit: {_setupError}")
                End Try
            End SyncLock
        End Sub

        ''' <summary>Rechnet dieses "Geraet" in Wahrheit auf dem Prozessor?
        '''
        ''' ERKANNT AM NAMEN, und das ist hier die richtige Form: die Software-Rasterisierer heissen
        ''' seit Jahren so, es sind fremde Namen und keine eigenen, und ein Merkmal in den Metadaten
        ''' gibt es nicht - "nicht diskret" trifft auch jede eingebaute Grafikeinheit, und die ist
        ''' eine echte.
        '''
        ''' Der Fall ist nicht selten: eine Linux-Installation ohne passenden Grafiktreiber (etwa
        ''' eine aeltere NVIDIA-Karte unter Nouveau, das fuer sie kein Vulkan hat) liefert genau
        ''' das - Mesa haelt lavapipe als Rueckfall bereit, und dann steht in der Auswahl eine
        ''' "Karte", die keine ist.</summary>
        Friend Shared Function IsSoftwareRenderer(info As GpuDeviceInfo) As Boolean
            If info Is Nothing Then Return False
            Dim name = $"{info.VendorName} {info.DeviceName}".ToLowerInvariant()
            If name.Trim().Length = 0 Then Return False
            For Each marke In SoftwareRendererNames
                If name.Contains(marke) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Die Namen, unter denen Software-Rasterisierer auftreten: Mesa (lavapipe/
        ''' llvmpipe), Googles SwiftShader und der Rueckfall von Windows.</summary>
        Private Shared ReadOnly SoftwareRendererNames As String() =
            {"lavapipe", "llvmpipe", "swiftshader", "software rasterizer", "basic render driver"}

        ''' <summary>Was sich ueber eine Karte sagen laesst.</summary>
        Private Shared Function FactsOf(device As OrtEpDevice) As GpuDeviceInfo
            Dim info = New GpuDeviceInfo()
            Try
                Dim hardware = device.HardwareDevice
                info.VendorName = If(String.IsNullOrWhiteSpace(hardware.Vendor),
                                     VendorNameFor(hardware.VendorId), hardware.Vendor.Trim())
                info.DeviceIdText = "0x" & hardware.DeviceId.ToString("X4")
                info.DeviceName = LookupDeviceName(hardware.VendorId, hardware.DeviceId)
                Dim value As String = Nothing
                If hardware.Metadata.Entries.TryGetValue("Discrete", value) Then
                    info.IsKindKnown = True
                    info.IsDiscrete = (value = "1")
                End If
                ' Der Steckplatz macht zwei baugleiche Karten unterscheidbar. Fehlt er, muss die
                ' Nummer des Geraetes genuegen.
                Dim slot As String = Nothing
                If Not hardware.Metadata.Entries.TryGetValue("pci_bus_id", slot) Then
                    hardware.Metadata.Entries.TryGetValue("card_idx", slot)
                End If
                info.Key = $"{hardware.VendorId:x4}:{hardware.DeviceId:x4}:{If(slot, "")}"
            Catch ex As Exception
                ' Zurueck kommt ein leerer Eintrag - und der sieht in der Auswahl genauso aus wie
                ' "keine Karte gefunden". Ohne diese Zeile waere die Diagnose "es steht nichts da"
                ' nicht von "die Geraetedaten liessen sich nicht lesen" zu unterscheiden.
                DiagnosticLogService.LogAlways("Grafik",
                    $"Geraetedaten nicht lesbar: {ex.GetType().Name}: {ShortMessage(ex.Message)}")
            End Try
            Return info
        End Function

        ''' <summary>Den Modellnamen zu Hersteller- und Geraetenummer nachschlagen.
        '''
        ''' Die Geraeteliste der Laufzeit gibt ihn NICHT her; sie kennt nur die Nummern, mit denen
        ''' sich Hersteller und Modell auf dem Bus melden. Unter Linux steht die Zuordnung dieser
        ''' Nummern zu Klarnamen in einer Datei, die zu den Systemdaten gehoert - dort nachzusehen
        ''' ist der Unterschied zwischen "0x2D05" und "GeForce RTX 5060". Fehlt die Datei, bleibt es
        ''' bei der Nummer; das ist eine schwaechere Auskunft, aber keine falsche.
        '''
        ''' Die Datei ist nach Herstellern gegliedert: eine Zeile ohne Einrueckung nennt den
        ''' Hersteller, die eingerueckten darunter seine Geraete, und eine weitere Ebene darunter
        ''' die Varianten einzelner Kartenhersteller. Gesucht wird also der Herstellerblock und
        ''' darin die Zeile mit EINEM Tabulator.
        '''
        ''' Der Klarname steht dort oft in der Form "GB206 [GeForce RTX 5060]": vorn der Chip, in
        ''' Klammern der Name, unter dem die Karte verkauft wird. Genommen wird der Name in der
        ''' Klammer - danach sucht ein Mensch.
        '''
        ''' Zeilenweise gelesen und beim Fund abgebrochen: die Datei ist ueber ein Megabyte gross,
        ''' und sie ganz in den Speicher zu ziehen, um eine Zeile zu finden, waere Verschwendung.</summary>
        Private Shared Function LookupDeviceName(vendorId As UInteger, deviceId As UInteger) As String
            Dim vendorTag = vendorId.ToString("x4")
            Dim deviceTag = deviceId.ToString("x4")
            For Each path In New String() {"/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids"}
                Try
                    If Not File.Exists(path) Then Continue For
                    Dim inVendorBlock = False
                    For Each line In File.ReadLines(path)
                        If line.Length = 0 OrElse line(0) = "#"c Then Continue For
                        If line(0) <> vbTab Then
                            ' Eine Herstellerzeile. Sie beendet den vorigen Block.
                            If inVendorBlock Then Return ""
                            inVendorBlock = line.StartsWith(vendorTag, StringComparison.OrdinalIgnoreCase)
                            Continue For
                        End If
                        If Not inVendorBlock Then Continue For
                        ' Zwei Tabulatoren sind eine Variante eines Kartenherstellers, nicht das Geraet.
                        If line.Length > 1 AndAlso line(1) = vbTab Then Continue For
                        Dim remainder = line.Substring(1)
                        If Not remainder.StartsWith(deviceTag, StringComparison.OrdinalIgnoreCase) Then Continue For
                        Dim name = remainder.Substring(deviceTag.Length).Trim()
                        Dim bracketStart = name.IndexOf("["c)
                        Dim bracketEnd = name.LastIndexOf("]"c)
                        If bracketStart >= 0 AndAlso bracketEnd > bracketStart Then
                            name = name.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim()
                        End If
                        Return name
                    Next
                Catch
                    ' Nicht lesbar oder unerwartet aufgebaut: dann bleibt es bei der Nummer.
                End Try
            Next
            Return ""
        End Function

        ''' <summary>Der Hersteller aus der Kennung, die er auf dem Bus fuehrt. Diese Nummern sind
        ''' seit Jahrzehnten dieselben; die Geraeteliste liefert den Klarnamen nicht bei jedem
        ''' Treiber mit.</summary>
        Private Shared Function VendorNameFor(vendorId As UInteger) As String
            Select Case vendorId
                Case &H10DEUI : Return "NVIDIA"
                Case &H1002UI, &H1022UI : Return "AMD"
                Case &H8086UI : Return "Intel"
                Case &H13B5UI : Return "ARM"
                Case &H5143UI : Return "Qualcomm"
                Case &H106BUI : Return "Apple"
                Case &H1414UI : Return "Microsoft"
                Case &H14E4UI : Return "Broadcom"
                Case &H1AF4UI : Return "Virtio"
                Case Else : Return ""
            End Select
        End Function

    End Class

End Namespace
