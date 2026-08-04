Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors

Namespace Services

    ''' <summary>Die gemeinsame Laufzeit fuer alle gelernten Modelle.
    '''
    ''' EINE Stelle, die Modelle findet, laedt und offen haelt. Jedes Modell hier hat dieselbe
    ''' Bauform: eine Datei im Modellordner, ein Name, eine Sitzung. Was ein Modell RECHNET, steht
    ''' nicht hier, sondern beim jeweiligen Dienst - hier steht nur, wie es geladen wird.
    '''
    ''' Zwei Entscheidungen, die den Rest bestimmen:
    '''
    ''' Erstens werden Modelle NICHT mitgeliefert und NICHT von selbst heruntergeladen. Sie sind
    ''' zwischen 10 und mehreren hundert Megabyte gross; sie ins Paket zu legen vervielfachte dessen
    ''' Groesse fuer eine Funktion, die nicht jeder braucht. Und von selbst etwas aus dem Netz zu
    ''' holen, ohne dass jemand danach gefragt hat, ist keine Eigenschaft, die eine Fotoanwendung
    ''' haben sollte. Der Nutzer legt die Datei in den Modellordner; ist sie da, erscheint die
    ''' Funktion, sonst nicht.
    '''
    ''' Zweitens laufen sie ab Werk auf der CPU. Der CPU-Weg laeuft ueberall, und fuer eine Maske
    ''' reicht er: eine Maske braucht keine volle Aufloesung. Wer eine Grafikkarte hat, kann sie in
    ''' den Einstellungen dazunehmen - was das bringt und was es kostet, steht bei
    ''' <see cref="GpuAccelerationService"/>. Ob eine Sitzung auf der Karte oder auf dem Prozessor
    ''' laeuft, entscheidet allein diese Klasse hier; die einzelnen Dienste merken davon nichts.</summary>
    Public NotInheritable Class AiModelService

        Private Sub New()
        End Sub

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _sessions As New Dictionary(Of String, LoadedSession)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Ein Schloss JE MODELLDATEI - siehe <see cref="BuildLockFor"/>.</summary>
        Private Shared ReadOnly _buildLocks As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        Private Shared _runtimeChecked As Boolean = False
        Private Shared _runtimeReady As Boolean = False
        Private Shared _runtimeError As String = ""
        Private Shared _telemetryOff As Boolean = False

        ''' <summary>Der Ordner des NUTZERS. Was hier liegt, gewinnt: wer ein Modell selbst
        ''' exportiert oder gegen ein neueres tauscht, soll das ohne Schreibrechte im System
        ''' koennen.</summary>
        Public Shared ReadOnly Property ModelFolder As String
            Get
                Return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FerrumPix", "Modelle")
            End Get
        End Property

        ''' <summary>Alle Orte, an denen ein Modell liegen kann, in der Reihenfolge, in der gesucht
        ''' wird.
        '''
        ''' Diese Reihenfolge ist der Grund, warum es KEINE zweite Uebersetzung des Programms
        ''' braucht, um eine Fassung mit und eine ohne Modelle auszuliefern: der Code ist derselbe,
        ''' und ob eine Funktion erscheint, entscheidet allein, ob die Datei da ist. Ein Paket, das
        ''' die Modelle mitbringt, legt sie neben die Anwendung; ein Paket ohne sie laesst den Platz
        ''' leer. Beide bauen aus demselben Quelltext, und der Pruefstand laeuft nur einmal.</summary>
        Public Shared ReadOnly Iterator Property SearchPaths As IEnumerable(Of String)
            Get
                Yield ModelFolder
                ' Neben der Anwendung - so liefert ein Paket sie aus.
                Dim neben = AppContext.BaseDirectory
                If Not String.IsNullOrEmpty(neben) Then Yield Path.Combine(neben, "Modelle")
                ' Und der uebliche Ort fuer unveraenderliche Programmdaten auf Systemen, die
                ' Programm und Daten trennen.
                For Each fest In New String() {"/usr/share/ferrumpix/modelle", "/usr/share/ferrumpix/models"}
                    Yield fest
                Next
            End Get
        End Property

        ''' <summary>Laeuft die Laufzeit auf diesem Rechner ueberhaupt? Einmal geprueft und gemerkt.
        '''
        ''' Die native Bibliothek der Laufzeit kann fehlen oder zur Plattform nicht passen. Das darf
        ''' die Anwendung NICHT mitreissen - ohne sie fehlt eine Funktion, alles andere geht weiter.
        ''' Deshalb wird hier einmal wirklich etwas gebaut statt nur eine Datei gesucht.</summary>
        Public Shared ReadOnly Property RuntimeAvailable As Boolean
            Get
                CheckRuntime()
                Return _runtimeReady
            End Get
        End Property

        ''' <summary>Warum die Laufzeit nicht laeuft, sonst leer. Fuer die Technologie-Anzeige.</summary>
        Public Shared ReadOnly Property RuntimeError As String
            Get
                CheckRuntime()
                Return _runtimeError
            End Get
        End Property

        Private Shared Sub CheckRuntime()
            SyncLock _lock
                If _runtimeChecked Then Return
                _runtimeChecked = True
                Try
                    ' TELEMETRIE AUS, und zwar als ERSTES - bevor irgendetwas anderes die Laufzeit
                    ' benutzt. Die Laufzeit meldet unter Windows ueber den Ereignisdienst des
                    ' Betriebssystems an ihren Hersteller; unter Linux und macOS tut sie es nicht,
                    ' aber die Abschaltung steht trotzdem plattformunabhaengig hier, damit sie nicht
                    ' davon abhaengt, wo gebaut wird. Was ein Nutzer mit seinen Fotos tut, geht
                    ' niemanden ausser ihm etwas an.
                    OrtEnv.Instance().DisableTelemetryEvents()
                    _telemetryOff = True

                    ' Eine Sitzungsoption zu bauen laedt die native Bibliothek - genau das, woran es
                    ' scheitern kann. Ein blosser Typzugriff wuerde noch nichts laden.
                    Using o = New SessionOptions()
                        _runtimeReady = True
                    End Using
                Catch ex As Exception
                    _runtimeReady = False
                    _runtimeError = ex.GetType().Name & ": " & ex.Message
                End Try
            End SyncLock
        End Sub

        ''' <summary>Wurde die Telemetrie der Laufzeit wirklich abgeschaltet? Nicht Kosmetik: der
        ''' Aufruf steht in demselben Try wie das Laden, und ein Fehlschlag beim Laden duerfte nicht
        ''' unbemerkt dazu fuehren, dass er uebersprungen wird.</summary>
        Public Shared ReadOnly Property TelemetryDisabled As Boolean
            Get
                CheckRuntime()
                Return _telemetryOff
            End Get
        End Property

        ''' <summary>Was beim Start gefunden wurde: Dateiname auf Fundort. EINMAL erhoben, danach
        ''' nur noch gelesen.
        '''
        ''' Warum gemerkt: an jeder Bindung haengt die Frage "gibt es diese Funktion?", und die wird
        ''' bei jedem Neuzeichnen ausgewertet. Bei jedem Mal in drei Ordnern nach Dateien zu suchen
        ''' waere Plattenzugriff im Zeichenpfad - genau die Sorte Kosten, die man erst bemerkt, wenn
        ''' der Ordner auf einem Netzlaufwerk liegt.</summary>
        Private Shared _inventory As Dictionary(Of String, String) = Nothing

        ''' <summary>Ein bekanntes Modell: woher es kommt, wie es heisst und woran man erkennt, dass
        ''' es unversehrt ist.
        '''
        ''' Die VERSION steckt im Dateinamen, nicht in einer Nebenangabe. Ein Modelltausch ist damit
        ''' ein neuer Name plus neue Pruefsumme: die alte Datei liegt harmlos daneben, statt heimlich
        ''' etwas anderes zu rechnen. Ohne Version im Namen koennte ein Wechsel eine Datei erwischen,
        ''' die andere Ein- und Ausgabeformen hat - und das ist ein Absturz, kein schlechteres
        ''' Ergebnis.
        '''
        ''' Die PRUEFSUMME steht in der Anwendung, nicht neben der Datei. Ein Modell wird einer
        ''' nativen Laufzeit zum Fressen gegeben; wer die Datei unterschiebt, bestimmt, was dort
        ''' laeuft. Eine Pruefsumme, die mit der Datei kommt, wuerde derselbe Angreifer mitliefern.</summary>
        Public NotInheritable Class ModelEntry
            ''' <summary>Der bestaendige Name des Modells, OHNE Version. Danach fragen die Dienste -
            ''' welche Datei das gerade ist, entscheidet dieser Dienst.</summary>
            Public Property Key As String = ""
            ''' <summary>Die AKTUELLE Datei. Ihr Name traegt die Version.</summary>
            Public Property FileName As String = ""
            ''' <summary>Aeltere Fassungen, die weiterhin laufen. Wer nicht aktualisiert hat, soll
            ''' weiterarbeiten koennen - ein Modellwechsel darf niemandem sein Werkzeug wegnehmen,
            ''' nur weil er den Knopf noch nicht gedrueckt hat.</summary>
            ''' <summary>Aeltere Dateinamen, mit denen sich weiterarbeiten laesst.
            ''' 
            ''' NUR, solange der Vertrag derselbe bleibt. Bei "lama" steht hier bewusst nichts: die
            ''' erste Fassung hatte zwei getrennte Eingaenge und gab 0 bis 255 aus, die zweite hat
            ''' einen Eingang mit vier Kanaelen und gibt 0 bis 1. Ein Rueckfall auf die alte Datei
            ''' ergaebe kein aelteres Ergebnis, sondern Unsinn.</summary>
            Public Property Predecessors As String() = New String() {}
            Public Property Purpose As String = ""
            Public Property Bytes As Long = 0
            Public Property Sha256 As String = ""
            ''' <summary>Womit diese Datei im Einstellungsdialog unter EINEM Eintrag steht.
            '''
            ''' Zwei verschiedene Dinge stehen hier zusammen, und der Unterschied ist wichtig:
            '''
            ''' BAUSTEINE einer Funktion (Objektauswahl: Kodierer und Dekodierer, Personen: Finden
            ''' und Vergleichen). Dort gilt "erst wenn alle vorliegen, gibt es die Funktion" - ein
            ''' halbes Modell ist keins.
            '''
            ''' WAHLMOEGLICHKEITEN derselben Funktion (Entrauschen: zwei, Hochskalieren: acht).
            ''' Dort ist jede Datei fuer sich benutzbar, und die Dienste fragen auch einzeln nach
            ''' ihr. Zusammen stehen sie, weil ein Dialog mit fuenf Zeilen fuer eine Funktion den
            ''' Blick auf die anderen verstellt.
            '''
            ''' Der Dialog nennt in beiden Faellen den Teilstand ("3 von 8 vorhanden"), und der
            ''' Knopf holt die ganze Gruppe. Wer nur eines der Wahlmodelle will, legt seine Datei
            ''' selbst in den Modellordner.</summary>
            Public Property Group As String = ""
            ''' <summary>Darf dieses Modell auf die Grafikkarte? Eine POSITIVLISTE, und zwar aus
            ''' Absicht: was hier nicht steht, rechnet auf dem Prozessor.
            '''
            ''' Der Grund ist nicht Vorsicht, sondern gemessen (Zahlen im Audit, RENDERPIPELINE):
            '''
            ''' Der MASKENDEKODIERER bringt die Laufzeit auf der Karte zum ABBRUCH - nicht zu einer
            ''' Ausnahme, die sich abfangen liesse, sondern zu einem harten Ende des Prozesses aus
            ''' nativem Code heraus. Ein Klick mit der Objektauswahl haette die Anwendung beendet.
            ''' Ein Fehler dieser Art ist der Grund, warum hier eine Positivliste steht und keine
            ''' Ausschlussliste: ein neues Modell, das noch niemand gemessen hat, darf nicht von
            ''' selbst auf die Karte geraten.
            '''
            ''' OBJEKT ENTFERNEN, GESICHTER FINDEN und GESICHTER VERGLEICHEN laufen auf der Karte
            ''' LANGSAMER als auf dem Prozessor - bei den beiden kleinen Modellen, weil die Fahrt
            ''' zur Karte und zurueck mehr kostet als die Rechnung selbst, beim Fuellmodell auch bei
            ''' voller Groesse (1024 Punkte: 7,3 Sekunden auf dem Prozessor gegen 11,3 auf der
            ''' Karte). Sie hier einzutragen waere kein Gewinn, sondern ein Verlust.</summary>
            Public Property GpuAllowed As Boolean = False

            ''' <summary>Eigene Adresse, falls dieses Modell nicht bei den anderen liegt. Leer heisst:
            ''' <see cref="HerkunftBasis"/> plus Dateiname.
            '''
            ''' VORSICHT, hier steht NICHT die Urquelle, sondern die Abholadresse. Was dort liegt,
            ''' muss BIT FUER BIT unsere Datei sein - Groesse und Pruefsumme werden nach dem Holen
            ''' geprueft, und wo sie nicht passen, bleibt die Funktion aus. Das ist einmal passiert:
            ''' hier stand die Fundstelle des ONNX-Exports, und dort liegt nur der Graph mit den
            ''' Gewichten in einer NEBENdatei (3,8 MB statt 76,9 MB). Der Knopf haette gelaedt und
            ''' waere trotzdem nie fertig geworden. Wer diesen Wert setzt, laedt die Adresse einmal
            ''' selbst herunter und vergleicht die Pruefsumme mit dem Eintrag.</summary>
            Public Property Herkunft As String = ""

            ''' <summary>Die Adresse, von der diese Datei geholt wird.</summary>
            Public ReadOnly Property Address As String
                Get
                    If Not String.IsNullOrWhiteSpace(Herkunft) Then Return Herkunft
                    Return HerkunftBasis & FileName
                End Get
            End Property
        End Class

        ''' <summary>Alle Modelle, die die Anwendung kennt. Wer eines hinzufuegt, traegt es hier ein;
        ''' sonst wird es beim Start nicht gesucht und die Funktion bliebe unsichtbar.
        '''
        ''' Die Gruppe "Personen" braucht BEIDE Dateien: die eine findet die Gesichter, die andere
        ''' macht aus einem Gesicht eine vergleichbare Zahlenreihe. Mit nur der ersten wuesste man,
        ''' DASS jemand im Bild ist, aber nie, ob es dieselbe Person ist wie nebenan - deshalb ist
        ''' die Gruppe erst vollstaendig etwas wert (siehe <c>Group</c>: ein halbes Modell ist keins).
        '''
        ''' Die Gruppe "Orte" ist KEIN gelerntes Modell, sondern eine Nachschlagetabelle: 170540 Orte
        ''' mit Koordinaten. Sie laeuft trotzdem hier mit, weil sie dieselben Fragen stellt - hole
        ''' sie auf Knopfdruck, pruefe die Pruefsumme, blende die Funktion aus, wenn sie fehlt. Ein
        ''' zweiter Mechanismus daneben haette nichts gekonnt, was dieser nicht kann.
        '''
        ''' WARUM NICHT EINGEBETTET: 12,6 MB in jeder Auslieferung, auch fuer alle, die keine
        ''' Ortsnamen brauchen - und eine Ortstabelle veraltet, waehrend das Programm gleich bleibt.
        ''' Als eigene Datei laesst sie sich austauschen, ohne eine neue Programmversion zu bauen.
        '''
        ''' KEIN Kommentar INNERHALB der Liste: VB bricht dort die implizite Zeilenfortsetzung nach
        ''' dem Komma ab, und der Initialisierer laesst sich nicht mehr uebersetzen.
        '''
        ''' WOHER EINE DATEI STAMMT, steht NICHT hier. <c>Herkunft</c> ist die Adresse, von der wir
        ''' sie holen, und das ist unser eigenes Modell-Repo - nicht die Urquelle. Die vollstaendige
        ''' Kette je Datei (Originalprojekt, Gewichte, ONNX-Export, Pruefsumme, Modellvertrag) liegt
        ''' dort in <c>licenses/NOTICE-*.txt</c>. Wer hier nach der Urquelle sucht, sucht am
        ''' falschen Ort - genau das ist einmal passiert.</summary>
        Public Shared ReadOnly Property KnownEntries As IReadOnlyList(Of ModelEntry) =
            New List(Of ModelEntry) From {
                New ModelEntry With {.Key = "mobilesam-encoder",
                                        .FileName = "mobilesam-encoder-v1.onnx", .Group = "Objektauswahl",
                                        .Purpose = "Bildkodierer", .Bytes = 27982937, .GpuAllowed = True,
                                        .Sha256 = "fec144aeb820a5a2f45ff4d6f3c46362ebd09227fdab1e6e42ae569ffa7cc3d6"},
                New ModelEntry With {.Key = "mobilesam-decoder",
                                        .FileName = "mobilesam-decoder-v1.onnx", .Group = "Objektauswahl",
                                        .Purpose = "Maskendekodierer", .Bytes = 16496934,
                                        .Sha256 = "a21b65b6e1b75e2c6265b36835747a0ab9169ec1ed725139a78ce90297f95126"},
                New ModelEntry With {.Key = "midas-small",
                                        .FileName = "midas-small-v1.onnx", .Group = "Tiefe",
                                        .Purpose = "Tiefenkarte", .Bytes = 66339845, .GpuAllowed = True,
                                        .Sha256 = "007d73146ac82eb424d7306fb2e9d15fb4d2702d5129040d9e68adeb28bc384e"},
                New ModelEntry With {.Key = "lama",
                                        .FileName = "lama-v1.onnx", .Group = "Objekt entfernen",
                                        .Purpose = "Lücken füllen", .Bytes = 110513159,
                                        .Sha256 = "11ba60a0e23344f7d42d2aba31cf9a599e9d1b3bb265b41b68595e2a2d72df16"},
                New ModelEntry With {.Key = "scunet",
                                        .FileName = "scunet-v1.onnx", .Group = "Entrauschen",
                                        .Purpose = "Rauschen entfernen", .Bytes = 76890542, .GpuAllowed = True,
                                        .Sha256 = "cae2172b8d2f2c08e5904a46b84b50ff26035c0fafdf8a86d5e52bd4e9cdba2d"},
                New ModelEntry With {.Key = "nafnet",
                                        .FileName = "nafnet-v1.onnx", .Group = "Entrauschen",
                                        .Purpose = "Rauschen entfernen, deutlich schneller", .Bytes = 118995025, .GpuAllowed = True,
                                        .Sha256 = "052c9238b420e8f232c4030e583426a9ce2bff36d2305fb2484509361cbe395d"},
                New ModelEntry With {.Key = "realesrgan-x4",
                                        .FileName = "realesrgan-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, gründlich", .Bytes = 67051642, .GpuAllowed = True,
                                        .Sha256 = "4e455536c3e41c8f04a77b455165b01e11ab4217fd86212bdfd69d707023aece"},
                New ModelEntry With {.Key = "realesrgan-x2",
                                        .FileName = "realesrgan-x2-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Zweifach, gründlich", .Bytes = 67075997, .GpuAllowed = True,
                                        .Sha256 = "c37a9de8d7b92e4fb3705b5d305080d10a2ada8fd2dd8ce25e37c26cf9d89042"},
                New ModelEntry With {.Key = "realesrgan-fast-x4",
                                        .FileName = "realesrgan-fast-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, zügig", .Bytes = 4866420,
                                        .Sha256 = "d92d4628a6f570a8686fa9f8b0180c712fa36b04209ce0752949fac3cd760242"},
                New ModelEntry With {.Key = "realesrgan-fast-wdn-x4",
                                        .FileName = "realesrgan-fast-wdn-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, zügig und entrauschend", .Bytes = 4866420,
                                        .Sha256 = "abc25fa980e4cc60ddef472f37e61b5e788831f01a8087cc4af596b4c73d5a40"},
                New ModelEntry With {.Key = "nomos8ksc-x4",
                                        .FileName = "nomos8ksc-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, für Aufnahmen aus dem Netz", .Bytes = 66938066, .GpuAllowed = True,
                                        .Sha256 = "f376e1adc0d16fa05a41828d3049bdf6bf2d65cfa805e27d30e55fd102f2ed80"},
                New ModelEntry With {.Key = "lsdirplusn-x4",
                                        .FileName = "lsdirplusn-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, zurückhaltend", .Bytes = 66938066, .GpuAllowed = True,
                                        .Sha256 = "1eff02d848bf9e6cb2329ce944e1ac225b59c6b596a78cda4dc39b15d1c8aace"},
                New ModelEntry With {.Key = "hfa2k-x4",
                                        .FileName = "hfa2k-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, für Zeichnungen, zweite Wahl", .Bytes = 66938066, .GpuAllowed = True,
                                        .Sha256 = "a34129a023545e05b2b43408edac28296ade92b8926ae8c632a35e555df6eecf"},
                New ModelEntry With {.Key = "realesrgan-anime-x4",
                                        .FileName = "realesrgan-anime-x4-v1.onnx", .Group = "Hochskalieren",
                                        .Purpose = "Vierfach, für Zeichnungen", .Bytes = 17939967, .GpuAllowed = True,
                                        .Sha256 = "9d094b3efa58f18f10e7eec7acd6f9ee534d4ccd50ba265c599cd6e99549bfea"},
                New ModelEntry With {.Key = "yunet",
                                        .FileName = "yunet-v1.onnx", .Group = "Personen",
                                        .Purpose = "Gesichter finden", .Bytes = 232589,
                                        .Sha256 = "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4"},
                New ModelEntry With {.Key = "arcface",
                                        .FileName = "arcface-r100-v1.onnx", .Group = "Personen",
                                        .Purpose = "Gesichter vergleichen", .Bytes = 261036388,
                                        .Sha256 = "f3a6bc281e72f88862f5748b53be3d76b3b48f8f1ab1f4a537941bdc4e1b01da"},
                New ModelEntry With {.Key = "orte",
                                        .FileName = "orte-v1.sqlite", .Group = "Orte",
                                        .Purpose = "Ortsnamen zu Koordinaten", .Bytes = 13201408,
                                        .Sha256 = "95ca2b04fb0703f8cd93e038ca0b3da469bbabf8aa7c9069acfc7105bf75c42a"}}

        ''' <summary>Woher die Dateien kommen: DIREKT von der Ablage, ohne Umweg.
        '''
        ''' Bewusst keine Weiterleitung ueber die Projektseite. Eine Weiterleitung sieht die
        ''' IP-Adresse des Nutzers genauso wie das Ziel - man haette also einen zweiten Mitwisser
        ''' geschaffen, ohne etwas zu gewinnen. Wer ein Modell holt, spricht mit genau einer Stelle.
        '''
        ''' Der Preis: die Adresse steckt in der ausgelieferten Anwendung. Wechselt der Ablageort,
        ''' greifen alte Fassungen ins Leere. Deshalb traegt jeder Eintrag seine eigene Adresse
        ''' tragen KANN - ein neues Modell an einem neuen Ort ist dann ein Registereintrag und keine
        ''' Umstellung.</summary>
        Public Const HerkunftBasis As String =
            "https://github.com/Bitpainter75/FerrumPix-Models/releases/download/modelle-v1/"

        ''' <summary>Der Eintrag zu einem Schluessel, oder Nothing.</summary>
        Public Shared Function EntryFor(key As String) As ModelEntry
            If String.IsNullOrWhiteSpace(key) Then Return Nothing
            Return KnownEntries.FirstOrDefault(
                Function(e) String.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Welche Datei dieses Modells gerade BENUTZT wird: die aktuelle, wenn sie da ist,
        ''' sonst die neueste vorhandene aeltere. Leer, wenn keine da ist.
        '''
        ''' Die Reihenfolge ist der ganze Punkt: wer aktualisiert hat, bekommt die neue; wer nicht,
        ''' arbeitet mit der alten weiter, statt vor einem verschwundenen Werkzeug zu stehen.</summary>
        Public Shared Function BestFile(key As String) As String
            Dim e = EntryFor(key)
            If e Is Nothing Then Return ""
            If ModelPresent(e.FileName) Then Return e.FileName
            If e.Predecessors IsNot Nothing Then
                For Each older In e.Predecessors
                    If ModelPresent(older) Then Return older
                Next
            End If
            Return ""
        End Function

        ''' <summary>Liegt eine AELTERE Fassung vor, waehrend es eine neuere gibt? Dann erscheint der
        ''' Aktualisieren-Knopf - und alles laeuft in der Zwischenzeit weiter.</summary>
        Public Shared Function IsUpdatable(key As String) As Boolean
            Dim e = EntryFor(key)
            If e Is Nothing Then Return False
            If ModelPresent(e.FileName) Then Return False
            Return Not String.IsNullOrEmpty(BestFile(key))
        End Function

        ''' <summary>Die Sitzung zum gerade benutzten Stand eines Modells.</summary>
        Public Shared Function SessionFor(key As String) As InferenceSession
            Dim file = BestFile(key)
            If String.IsNullOrEmpty(file) Then Return Nothing
            Return Session(file)
        End Function

        ''' <summary>Die Pruefsumme einer Datei, oder leer. Kleinbuchstaben, ohne Trenner.</summary>
        Public Shared Function ChecksumOf(path As String) As String
            Try
                Using strom = File.OpenRead(path)
                    Using sha = Security.Cryptography.SHA256.Create()
                        Return BitConverter.ToString(sha.ComputeHash(strom)).Replace("-", "").ToLowerInvariant()
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Stimmt die Datei mit dem ueberein, was die Anwendung erwartet?</summary>
        Public Shared Function IsIntact(entry As ModelEntry) As Boolean
            If entry Is Nothing Then Return False
            Dim path = ModelPath(entry.FileName)
            If String.IsNullOrEmpty(path) Then Return False
            Try
                If New IO.FileInfo(path).Length <> entry.Bytes Then Return False
            Catch
                Return False
            End Try
            Return String.Equals(ChecksumOf(path), entry.Sha256, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Alle Dateinamen, nach denen beim Start gesucht wird - die aktuellen UND die
        ''' aelteren. Nur so kann festgestellt werden, dass jemand mit einer alten Fassung arbeitet.</summary>
        Private Shared ReadOnly Property KnownFiles As String()
            Get
                Return KnownEntries.
                    SelectMany(Function(e) New String() {e.FileName}.Concat(If(e.Predecessors, New String() {}))).
                    Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            End Get
        End Property

        ''' <summary>Einmal beim Start nachsehen, welche Modelle vorliegen. Danach steht fest, welche
        ''' Funktionen es in dieser Sitzung gibt - und die Oberflaeche blendet aus, was fehlt.
        '''
        ''' Bewusst EINMAL und nicht bei jedem Zugriff: eine Funktion, die mitten in der Arbeit
        ''' auftaucht oder verschwindet, weil jemand eine Datei verschoben hat, ist schlimmer als
        ''' eine, die erst nach einem Neustart erscheint. Wer ein Modell nachlegt, kann ueber
        ''' <see cref="CheckAgain"/> auch ohne Neustart nachsehen lassen.</summary>
        Public Shared Sub CheckInventory()
            Dim found = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each name In KnownFiles
                For Each place In SearchPaths
                    If String.IsNullOrEmpty(place) Then Continue For
                    Dim candidate = Path.Combine(place, name)
                    If File.Exists(candidate) Then
                        found(name) = candidate
                        Exit For
                    End If
                Next
            Next
            SyncLock _lock
                _inventory = found
            End SyncLock
            DiagnosticLogService.LogAlways("KiModell",
                $"Modelle: {found.Count} von {KnownEntries.Count} gefunden" &
                If(found.Count = 0, "", " (" & String.Join(", ", found.Keys) & ")"))
        End Sub

        ''' <summary>Noch einmal nachsehen - fuer den Fall, dass jemand waehrend der Sitzung ein
        ''' Modell abgelegt hat. Bereits offene Sitzungen bleiben, sie sind ja gueltig.</summary>
        Public Shared Sub CheckAgain()
            CheckInventory()
        End Sub

        ''' <summary>Vollstaendiger Pfad einer Modelldatei, am ersten Ort, an dem sie liegt. Leer,
        ''' wenn sie nirgends liegt.</summary>
        Public Shared Function ModelPath(fileName As String) As String
            If String.IsNullOrWhiteSpace(fileName) Then Return ""
            Dim table As Dictionary(Of String, String)
            SyncLock _lock
                table = _inventory
            End SyncLock
            ' Noch nie geprueft (Pruefstand, frueher Aufruf): dann jetzt, statt Nichts zu melden.
            If table Is Nothing Then
                CheckInventory()
                SyncLock _lock
                    table = _inventory
                End SyncLock
            End If
            Dim p As String = Nothing
            If table IsNot Nothing AndAlso table.TryGetValue(fileName, p) Then Return p
            Return ""
        End Function

        ''' <summary>Liegt dieses Modell irgendwo vor?</summary>
        Public Shared Function ModelPresent(fileName As String) As Boolean
            Return Not String.IsNullOrEmpty(ModelPath(fileName))
        End Function

        ''' <summary>Darf diese DATEI auf die Grafikkarte? Die Antwort steht am Register-Eintrag,
        ''' gilt aber auch fuer dessen aeltere Fassungen - eine alte Datei rechnet dasselbe.
        '''
        ''' Unbekannte Dateien bekommen ein Nein. Wer eine eigene Datei in den Modellordner legt,
        ''' bekommt den Prozessor, und das ist die richtige Antwort: was sie auf der Karte tut, hat
        ''' niemand gemessen.</summary>
        Public Shared Function IsGpuAllowed(fileName As String) As Boolean
            If String.IsNullOrWhiteSpace(fileName) Then Return False
            For Each entry In KnownEntries
                If String.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase) Then Return entry.GpuAllowed
                If entry.Predecessors IsNot Nothing AndAlso
                   entry.Predecessors.Any(Function(older) String.Equals(older, fileName, StringComparison.OrdinalIgnoreCase)) Then
                    Return entry.GpuAllowed
                End If
            Next
            Return False
        End Function

        ''' <summary>Eine offene Sitzung und die Frage, auf welchem Rechenwerk sie gebaut wurde:
        ''' leer heisst Prozessor, sonst steht dort der Schluessel der Grafikkarte.
        '''
        ''' Ohne diesen Vermerk waere der Schalter in den Einstellungen eine Luege: die Sitzungen
        ''' bleiben offen, und eine bereits geladene bliebe auf dem Prozessor, obwohl die Karte
        ''' inzwischen eingeschaltet ist. Und weil dort der SCHLUESSEL steht und nicht nur ein Ja,
        ''' faellt auch der Wechsel von einer Karte auf eine andere auf.</summary>
        Private NotInheritable Class LoadedSession
            Public Property Session As InferenceSession
            Public Property Accelerator As String = ""
        End Class

        ''' <summary>Die Sitzungsoptionen, mit denen jedes Modell geladen wird.
        '''
        ''' Der Aufrufer gibt sie wieder frei - sie halten ein natives Handle. Das geht, sobald die
        ''' Sitzung steht: die Laufzeit liest die Optionen beim Bauen aus und braucht sie danach
        ''' nicht mehr.</summary>
        Private Shared Function BuildOptions() As SessionOptions
            Dim options = New SessionOptions()
            ' Ein Modell laeuft waehrend der Bearbeitung neben der Vorschau. Alle Kerne zu
            ' belegen liesse die Oberflaeche stehen - die Haelfte, mindestens einer.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount \ 2)
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            ' Nur noch echte Fehler. Beim Optimieren meldet die Laufzeit sonst jede
            ' ungenutzte Groesse im Modell einzeln auf die Fehlerausgabe - hunderte Zeilen,
            ' die niemanden etwas angehen und echte Meldungen zudecken.
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            Return options
        End Function

        ''' <summary>Das Schloss, unter dem GENAU DIESE Modelldatei gebaut wird.
        '''
        ''' Ein Modell zu laden kostet hunderte Millisekunden bis Sekunden. Laege der Bau unter dem
        ''' allgemeinen Schloss, wartete solange jeder andere Modellzugriff mit - die Gesichtssuche
        ''' im Hintergrund etwa, die ein ganz anderes Modell will und mit diesem hier nichts zu tun
        ''' hat. Je Datei ein eigenes Schloss loest beides: zwei verschiedene Modelle bauen
        ''' nebeneinander, dieselbe Datei baut weiterhin nur einmal.
        '''
        ''' Das allgemeine Schloss schuetzt nur noch die beiden Listen und wird immer nur kurz
        ''' gehalten. Die Reihenfolge ist ueberall dieselbe: erst das Schloss der Datei, darunter
        ''' das allgemeine - nie umgekehrt.</summary>
        Private Shared Function BuildLockFor(filePath As String) As Object
            SyncLock _lock
                Dim gate As Object = Nothing
                If Not _buildLocks.TryGetValue(filePath, gate) Then
                    gate = New Object()
                    _buildLocks(filePath) = gate
                End If
                Return gate
            End SyncLock
        End Function

        ''' <summary>Die Sitzung zu einem Modell, oder Nothing.
        '''
        ''' Sitzungen bleiben offen und werden geteilt: das Laden kostet je nach Modell hunderte
        ''' Millisekunden, und ein Klick-fuer-Klick neu geladenes Modell waere unbenutzbar. Der
        ''' Besitz bleibt HIER - der Aufrufer darf sie nicht schliessen.
        '''
        ''' Wurde der Schalter fuer die Grafikkarte seit dem Laden umgelegt, wird die alte Sitzung
        ''' AUS DER LISTE GENOMMEN, aber NICHT geschlossen: es kann in diesem Augenblick jemand
        ''' damit rechnen, und eine geschlossene Sitzung unter laufender Rechnung ist kein Fehler,
        ''' sondern ein Absturz. Der Speicher wird frei, sobald niemand mehr auf sie zeigt.
        '''
        ''' Der Vermerk ueber das Rechenwerk (<see cref="LoadedSession"/>) bleibt dabei die einzige
        ''' Wahrheit: verglichen wird immer gegen das, was JETZT gewuenscht ist, und zwar noch
        ''' einmal unter dem Bau-Schloss - waehrend des Wartens kann ein anderer Faden dieselbe
        ''' Sitzung schon gebaut haben.</summary>
        Public Shared Function Session(fileName As String) As InferenceSession
            If Not RuntimeAvailable Then Return Nothing
            Dim filePath = ModelPath(fileName)
            If String.IsNullOrEmpty(filePath) Then Return Nothing
            ' Die Frage nach der Karte steht bewusst VOR jedem Schloss: sie fuehrt in die
            ' Grafikbeschleunigung, und die fragt von dort aus wieder hier herein.
            Dim onGpu = GpuAccelerationService.ShouldUse AndAlso IsGpuAllowed(fileName)
            ' Leer heisst Prozessor. Steht dort ein Schluessel, ist es DIESE Karte - und eine
            ' andere Karte ist damit genauso ein Wechsel wie das Abschalten.
            Dim target = If(onGpu, GpuAccelerationService.ActiveDeviceKey, "")

            ' Der haeufige Fall: die Sitzung steht schon und passt. Dafuer reicht ein kurzer Blick
            ' in die Liste, ohne irgendjemanden aufzuhalten.
            SyncLock _lock
                Dim ready As LoadedSession = Nothing
                If _sessions.TryGetValue(filePath, ready) AndAlso ready.Accelerator = target Then
                    Return ready.Session
                End If
            End SyncLock

            SyncLock BuildLockFor(filePath)
                SyncLock _lock
                    Dim existing As LoadedSession = Nothing
                    If _sessions.TryGetValue(filePath, existing) Then
                        If existing.Accelerator = target Then Return existing.Session
                        _sessions.Remove(filePath)
                    End If
                End SyncLock

                Dim builtWith = ""
                Dim created As InferenceSession = Nothing
                If onGpu Then
                    Try
                        Using options = BuildOptions()
                            If GpuAccelerationService.TryApply(options) Then
                                created = New InferenceSession(filePath, options)
                                builtWith = target
                                GpuAccelerationService.NoteSuccess()
                            End If
                        End Using
                    Catch ex As Exception
                        ' Die Karte hat das Modell nicht genommen. Das kostet Zeit, aber keine
                        ' Funktion: gleich darunter laeuft derselbe Weg ueber den Prozessor.
                        GpuAccelerationService.NoteFailure(ex.Message)
                        DiagnosticLogService.LogAlways("KiModell",
                            $"nicht auf der Grafikkarte: {Path.GetFileName(filePath)} - {ex.Message}")
                        created = Nothing
                    End Try
                End If
                Try
                    If created Is Nothing Then
                        Using options = BuildOptions()
                            created = New InferenceSession(filePath, options)
                        End Using
                        builtWith = ""
                    End If
                    SyncLock _lock
                        _sessions(filePath) = New LoadedSession With {.Session = created, .Accelerator = builtWith}
                    End SyncLock
                    Return created
                Catch ex As Exception
                    DiagnosticLogService.LogAlways("KiModell", $"laedt nicht: {Path.GetFileName(filePath)} - {ex.Message}")
                    Return Nothing
                End Try
            End SyncLock
        End Function

        ''' <summary>Alle Sitzungen schliessen. Fuer den Prueftstand und das Beenden.</summary>
        ''' <remarks>Die Bau-Schloesser bleiben ABSICHTLICH stehen. Sie wegzuwerfen, waehrend ein
        ''' anderer Faden eines davon haelt, ergaebe zwei Schloesser fuer dieselbe Datei - und dann
        ''' bauen zwei Faeden dasselbe Modell gleichzeitig. Es sind hoechstens so viele Objekte wie
        ''' Modelldateien.</remarks>
        Public Shared Sub ReleaseAll()
            SyncLock _lock
                For Each s In _sessions.Values
                    Try
                        s.Session.Dispose()
                    Catch
                    End Try
                Next
                _sessions.Clear()
            End SyncLock
        End Sub

        ''' <summary>Ein Bild als Tensor in der Form [1, 3, h, w], Kanaele getrennt, Werte 0..1 und
        ''' danach normiert. So erwarten es die allermeisten Bildmodelle.
        '''
        ''' <paramref name="average"/> und <paramref name="spread"/> je Kanal in RGB-Reihenfolge.
        ''' Ohne Normierung 0 und 1 uebergeben.</summary>
        Public Shared Function AlsTensor(image As SkiaSharp.SKBitmap,
                                         average As Single(), spread As Single()) As DenseTensor(Of Single)
            If image Is Nothing Then Return Nothing
            Dim w = image.Width, h = image.Height
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim m = If(average IsNot Nothing AndAlso average.Length = 3, average, New Single() {0.0F, 0.0F, 0.0F})
            Dim s = If(spread IsNot Nothing AndAlso spread.Length = 3, spread, New Single() {1.0F, 1.0F, 1.0F})

            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, h, w})
            Dim target = tensor.Buffer.Span
            Dim layer = w * h
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim p = image.GetPixel(x, y)
                    Dim i = y * w + x
                    target(i) = (p.Red / 255.0F - m(0)) / s(0)
                    target(layer + i) = (p.Green / 255.0F - m(1)) / s(1)
                    target(layer * 2 + i) = (p.Blue / 255.0F - m(2)) / s(2)
                Next
            Next
            Return tensor
        End Function

    End Class

End Namespace
