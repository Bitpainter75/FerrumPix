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
        Public Enum ProbeResult
            Untested = 0
            Running = 1
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

        ''' <summary>Der Stand der echten Probe.</summary>
        Public Shared ReadOnly Property ProbeState As ProbeResult
            Get
                Return _probe
            End Get
        End Property

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

        ''' <summary>Soll das naechste Modell auf der Karte laufen? Beides muss stimmen:
        ''' eingeschaltet UND vorhanden. Ob das jeweilige MODELL darf, entscheidet
        ''' <see cref="AiModelService.IsGpuAllowed"/>.</summary>
        Public Shared ReadOnly Property ShouldUse As Boolean
            Get
                Return Enabled AndAlso Available
            End Get
        End Property

        ''' <summary>Die Karte an eine Sitzungsoption haengen. False heisst: es bleibt beim
        ''' Prozessor, und der Aufrufer soll ohne sie weitermachen.</summary>
        Public Shared Function TryApply(options As SessionOptions) As Boolean
            If options Is Nothing Then Return False
            EnsureActive()
            If _devices.Count = 0 Then Return False
            Try
                options.AppendExecutionProvider(OrtEnv.Instance(), _devices, New Dictionary(Of String, String)())
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Grafik", $"Beschleunigung laesst sich nicht anhaengen: {ex.Message}")
                Return False
            End Try
        End Function

        ''' <summary>Ein Modell ist wirklich auf der Karte angelaufen.</summary>
        Public Shared Sub NoteSuccess()
            _probe = ProbeResult.Works
            _probeError = ""
        End Sub

        ''' <summary>Ein Modell ist auf der Karte NICHT angelaufen. Der Text ist die Meldung der
        ''' Laufzeit; sie wird gekuerzt, weil sie mehrere Zeilen Dateipfade aus dem Bauverzeichnis
        ''' der Laufzeit mitbringt, die niemandem helfen.</summary>
        Public Shared Sub NoteFailure(message As String)
            _probe = ProbeResult.Failed
            _probeError = ShortMessage(message)
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

        ''' <summary>Wirklich nachsehen, ob ein Modell auf der Karte laeuft.
        '''
        ''' Dafuer wird die kleinste vorhandene Modelldatei genommen und eine Sitzung damit gebaut -
        ''' der Punkt, an dem die Laufzeit die Karte tatsaechlich anfordert. Die Sitzung wird sofort
        ''' wieder geschlossen; sie soll nichts, ausser die Frage zu beantworten. GERECHNET wird
        ''' dabei nichts: welches Modell auf der Karte rechnen darf, steht in der Positivliste, und
        ''' fuer die Frage nach dem Treiber ist gleichgueltig, welche Datei dafuer herhaelt.
        '''
        ''' Ist keine Modelldatei da, bleibt die Frage offen: ohne Modell gibt es auch nichts zu
        ''' beschleunigen.
        '''
        ''' Braucht Zeit - gehoert also NICHT auf den Oberflaechenfaden.</summary>
        Public Shared Function Probe() As ProbeResult
            EnsureActive()
            If _devices.Count = 0 Then
                _probe = ProbeResult.Failed
                _probeError = ""
                Return _probe
            End If

            Dim file = SmallestModelFile()
            If String.IsNullOrEmpty(file) Then
                _probe = ProbeResult.Untested
                _probeError = ""
                Return _probe
            End If

            _probe = ProbeResult.Running
            Try
                Using options = New SessionOptions()
                    options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                    If Not TryApply(options) Then
                        _probe = ProbeResult.Failed
                        _probeError = ""
                        Return _probe
                    End If
                    Using session = New InferenceSession(file, options)
                    End Using
                End Using
                NoteSuccess()
                DiagnosticLogService.LogAlways("Grafik", $"Probe bestanden auf {VendorName} {DeviceName} ({BackendName})")
            Catch ex As Exception
                NoteFailure(ex.Message)
                DiagnosticLogService.LogAlways("Grafik", $"Probe gescheitert: {_probeError}")
            End Try
            Return _probe
        End Function

        ''' <summary>Die kleinste Modelldatei, die vorliegt - die Probe soll den Rechner nicht
        ''' minutenlang beschaeftigen. Die Ortstabelle faellt heraus, sie ist kein Modell.</summary>
        Private Shared Function SmallestModelFile() As String
            Try
                For Each entry In AiModelService.KnownEntries.
                        Where(Function(e) e.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)).
                        OrderBy(Function(e) e.Bytes)
                    Dim fileName = AiModelService.BestFile(entry.Key)
                    If String.IsNullOrEmpty(fileName) Then Continue For
                    Dim path = AiModelService.ModelPath(fileName)
                    If Not String.IsNullOrEmpty(path) Then Return path
                Next
            Catch
            End Try
            Return ""
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
                    ' Andere Karte, alte Probe wertlos.
                    _probe = ProbeResult.Untested
                    _probeError = ""
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

                    _found = OrtEnv.Instance().GetEpDevices().
                        Where(Function(d) String.Equals(d.EpName, WebGpuEp.GetEpName(), StringComparison.Ordinal)).
                        ToList()
                    _infos = _found.Select(AddressOf FactsOf).ToList()
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
            Catch
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
                        Dim rest = line.Substring(1)
                        If Not rest.StartsWith(deviceTag, StringComparison.OrdinalIgnoreCase) Then Continue For
                        Dim name = rest.Substring(deviceTag.Length).Trim()
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
