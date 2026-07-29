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
    ''' Zweitens laufen sie auf der CPU. Eine Grafikkarte waere schneller, verlangt aber je nach
    ''' Hersteller eigene Pakete und eigene Fehlerbilder. Der CPU-Weg laeuft ueberall, und fuer eine
    ''' Maske reicht er: eine Maske braucht keine volle Aufloesung.</summary>
    Public NotInheritable Class KiModellService

        Private Sub New()
        End Sub

        Private Shared ReadOnly _sperre As New Object()
        Private Shared ReadOnly _sitzungen As New Dictionary(Of String, InferenceSession)(StringComparer.OrdinalIgnoreCase)
        Private Shared _laufzeitGeprueft As Boolean = False
        Private Shared _laufzeitDa As Boolean = False
        Private Shared _laufzeitFehler As String = ""
        Private Shared _telemetrieAus As Boolean = False

        ''' <summary>Der Ordner des NUTZERS. Was hier liegt, gewinnt: wer ein Modell selbst
        ''' exportiert oder gegen ein neueres tauscht, soll das ohne Schreibrechte im System
        ''' koennen.</summary>
        Public Shared ReadOnly Property ModellOrdner As String
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
        Public Shared ReadOnly Iterator Property Suchorte As IEnumerable(Of String)
            Get
                Yield ModellOrdner
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
        Public Shared ReadOnly Property LaufzeitVerfuegbar As Boolean
            Get
                PruefeLaufzeit()
                Return _laufzeitDa
            End Get
        End Property

        ''' <summary>Warum die Laufzeit nicht laeuft, sonst leer. Fuer die Technologie-Anzeige.</summary>
        Public Shared ReadOnly Property LaufzeitFehler As String
            Get
                PruefeLaufzeit()
                Return _laufzeitFehler
            End Get
        End Property

        Private Shared Sub PruefeLaufzeit()
            SyncLock _sperre
                If _laufzeitGeprueft Then Return
                _laufzeitGeprueft = True
                Try
                    ' TELEMETRIE AUS, und zwar als ERSTES - bevor irgendetwas anderes die Laufzeit
                    ' benutzt. Die Laufzeit meldet unter Windows ueber den Ereignisdienst des
                    ' Betriebssystems an ihren Hersteller; unter Linux und macOS tut sie es nicht,
                    ' aber die Abschaltung steht trotzdem plattformunabhaengig hier, damit sie nicht
                    ' davon abhaengt, wo gebaut wird. Was ein Nutzer mit seinen Fotos tut, geht
                    ' niemanden ausser ihm etwas an.
                    OrtEnv.Instance().DisableTelemetryEvents()
                    _telemetrieAus = True

                    ' Eine Sitzungsoption zu bauen laedt die native Bibliothek - genau das, woran es
                    ' scheitern kann. Ein blosser Typzugriff wuerde noch nichts laden.
                    Using o = New SessionOptions()
                        _laufzeitDa = True
                    End Using
                Catch ex As Exception
                    _laufzeitDa = False
                    _laufzeitFehler = ex.GetType().Name & ": " & ex.Message
                End Try
            End SyncLock
        End Sub

        ''' <summary>Wurde die Telemetrie der Laufzeit wirklich abgeschaltet? Nicht Kosmetik: der
        ''' Aufruf steht in demselben Try wie das Laden, und ein Fehlschlag beim Laden duerfte nicht
        ''' unbemerkt dazu fuehren, dass er uebersprungen wird.</summary>
        Public Shared ReadOnly Property TelemetrieAbgeschaltet As Boolean
            Get
                PruefeLaufzeit()
                Return _telemetrieAus
            End Get
        End Property

        ''' <summary>Was beim Start gefunden wurde: Dateiname auf Fundort. EINMAL erhoben, danach
        ''' nur noch gelesen.
        '''
        ''' Warum gemerkt: an jeder Bindung haengt die Frage "gibt es diese Funktion?", und die wird
        ''' bei jedem Neuzeichnen ausgewertet. Bei jedem Mal in drei Ordnern nach Dateien zu suchen
        ''' waere Plattenzugriff im Zeichenpfad - genau die Sorte Kosten, die man erst bemerkt, wenn
        ''' der Ordner auf einem Netzlaufwerk liegt.</summary>
        Private Shared _bestand As Dictionary(Of String, String) = Nothing

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
        Public NotInheritable Class ModellEintrag
            ''' <summary>Der bestaendige Name des Modells, OHNE Version. Danach fragen die Dienste -
            ''' welche Datei das gerade ist, entscheidet dieser Dienst.</summary>
            Public Property Schluessel As String = ""
            ''' <summary>Die AKTUELLE Datei. Ihr Name traegt die Version.</summary>
            Public Property Datei As String = ""
            ''' <summary>Aeltere Fassungen, die weiterhin laufen. Wer nicht aktualisiert hat, soll
            ''' weiterarbeiten koennen - ein Modellwechsel darf niemandem sein Werkzeug wegnehmen,
            ''' nur weil er den Knopf noch nicht gedrueckt hat.</summary>
            ''' <summary>Aeltere Dateinamen, mit denen sich weiterarbeiten laesst.
            ''' 
            ''' NUR, solange der Vertrag derselbe bleibt. Bei "lama" steht hier bewusst nichts: die
            ''' erste Fassung hatte zwei getrennte Eingaenge und gab 0 bis 255 aus, die zweite hat
            ''' einen Eingang mit vier Kanaelen und gibt 0 bis 1. Ein Rueckfall auf die alte Datei
            ''' ergaebe kein aelteres Ergebnis, sondern Unsinn.</summary>
            Public Property Vorgaenger As String() = New String() {}
            Public Property Zweck As String = ""
            Public Property Bytes As Long = 0
            Public Property Sha256 As String = ""
            ''' <summary>Alle Dateien eines Bausteins - erst wenn ALLE vorliegen, gibt es die
            ''' Funktion. Ein halbes Modell ist keins.</summary>
            Public Property Gruppe As String = ""
            ''' <summary>Eigene Adresse, falls dieses Modell nicht bei den anderen liegt. Leer heisst:
            ''' <see cref="HerkunftBasis"/> plus Dateiname.</summary>
            Public Property Herkunft As String = ""

            ''' <summary>Die Adresse, von der diese Datei geholt wird.</summary>
            Public ReadOnly Property Adresse As String
                Get
                    If Not String.IsNullOrWhiteSpace(Herkunft) Then Return Herkunft
                    Return HerkunftBasis & Datei
                End Get
            End Property
        End Class

        ''' <summary>Alle Modelle, die die Anwendung kennt. Wer eines hinzufuegt, traegt es hier ein;
        ''' sonst wird es beim Start nicht gesucht und die Funktion bliebe unsichtbar.</summary>
        Public Shared ReadOnly Property BekannteEintraege As IReadOnlyList(Of ModellEintrag) =
            New List(Of ModellEintrag) From {
                New ModellEintrag With {.Schluessel = "mobilesam-encoder",
                                        .Datei = "mobilesam-encoder-v1.onnx", .Gruppe = "Objektauswahl",
                                        .Zweck = "Bildkodierer", .Bytes = 27982937,
                                        .Sha256 = "fec144aeb820a5a2f45ff4d6f3c46362ebd09227fdab1e6e42ae569ffa7cc3d6"},
                New ModellEintrag With {.Schluessel = "mobilesam-decoder",
                                        .Datei = "mobilesam-decoder-v1.onnx", .Gruppe = "Objektauswahl",
                                        .Zweck = "Maskendekodierer", .Bytes = 16496934,
                                        .Sha256 = "a21b65b6e1b75e2c6265b36835747a0ab9169ec1ed725139a78ce90297f95126"},
                New ModellEintrag With {.Schluessel = "midas-small",
                                        .Datei = "midas-small-v1.onnx", .Gruppe = "Tiefe",
                                        .Zweck = "Tiefenkarte", .Bytes = 66339845,
                                        .Sha256 = "007d73146ac82eb424d7306fb2e9d15fb4d2702d5129040d9e68adeb28bc384e"},
                New ModellEintrag With {.Schluessel = "lama",
                                        .Datei = "lama-v1.onnx", .Gruppe = "Objekt entfernen",
                                        .Zweck = "Lücken füllen", .Bytes = 110513159,
                                        .Sha256 = "11ba60a0e23344f7d42d2aba31cf9a599e9d1b3bb265b41b68595e2a2d72df16"}}

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
        Public Shared Function EintragFuer(schluessel As String) As ModellEintrag
            If String.IsNullOrWhiteSpace(schluessel) Then Return Nothing
            Return BekannteEintraege.FirstOrDefault(
                Function(e) String.Equals(e.Schluessel, schluessel, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Welche Datei dieses Modells gerade BENUTZT wird: die aktuelle, wenn sie da ist,
        ''' sonst die neueste vorhandene aeltere. Leer, wenn keine da ist.
        '''
        ''' Die Reihenfolge ist der ganze Punkt: wer aktualisiert hat, bekommt die neue; wer nicht,
        ''' arbeitet mit der alten weiter, statt vor einem verschwundenen Werkzeug zu stehen.</summary>
        Public Shared Function BesteDatei(schluessel As String) As String
            Dim e = EintragFuer(schluessel)
            If e Is Nothing Then Return ""
            If ModellVorhanden(e.Datei) Then Return e.Datei
            If e.Vorgaenger IsNot Nothing Then
                For Each alt In e.Vorgaenger
                    If ModellVorhanden(alt) Then Return alt
                Next
            End If
            Return ""
        End Function

        ''' <summary>Liegt eine AELTERE Fassung vor, waehrend es eine neuere gibt? Dann erscheint der
        ''' Aktualisieren-Knopf - und alles laeuft in der Zwischenzeit weiter.</summary>
        Public Shared Function IstAktualisierbar(schluessel As String) As Boolean
            Dim e = EintragFuer(schluessel)
            If e Is Nothing Then Return False
            If ModellVorhanden(e.Datei) Then Return False
            Return Not String.IsNullOrEmpty(BesteDatei(schluessel))
        End Function

        ''' <summary>Die Sitzung zum gerade benutzten Stand eines Modells.</summary>
        Public Shared Function SitzungFuer(schluessel As String) As InferenceSession
            Dim datei = BesteDatei(schluessel)
            If String.IsNullOrEmpty(datei) Then Return Nothing
            Return Sitzung(datei)
        End Function

        ''' <summary>Die Pruefsumme einer Datei, oder leer. Kleinbuchstaben, ohne Trenner.</summary>
        Public Shared Function PruefsummeVon(pfad As String) As String
            Try
                Using strom = File.OpenRead(pfad)
                    Using sha = Security.Cryptography.SHA256.Create()
                        Return BitConverter.ToString(sha.ComputeHash(strom)).Replace("-", "").ToLowerInvariant()
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Stimmt die Datei mit dem ueberein, was die Anwendung erwartet?</summary>
        Public Shared Function IstUnversehrt(eintrag As ModellEintrag) As Boolean
            If eintrag Is Nothing Then Return False
            Dim pfad = ModellPfad(eintrag.Datei)
            If String.IsNullOrEmpty(pfad) Then Return False
            Try
                If New IO.FileInfo(pfad).Length <> eintrag.Bytes Then Return False
            Catch
                Return False
            End Try
            Return String.Equals(PruefsummeVon(pfad), eintrag.Sha256, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Alle Dateinamen, nach denen beim Start gesucht wird - die aktuellen UND die
        ''' aelteren. Nur so kann festgestellt werden, dass jemand mit einer alten Fassung arbeitet.</summary>
        Private Shared ReadOnly Property BekannteDateien As String()
            Get
                Return BekannteEintraege.
                    SelectMany(Function(e) New String() {e.Datei}.Concat(If(e.Vorgaenger, New String() {}))).
                    Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            End Get
        End Property

        ''' <summary>Einmal beim Start nachsehen, welche Modelle vorliegen. Danach steht fest, welche
        ''' Funktionen es in dieser Sitzung gibt - und die Oberflaeche blendet aus, was fehlt.
        '''
        ''' Bewusst EINMAL und nicht bei jedem Zugriff: eine Funktion, die mitten in der Arbeit
        ''' auftaucht oder verschwindet, weil jemand eine Datei verschoben hat, ist schlimmer als
        ''' eine, die erst nach einem Neustart erscheint. Wer ein Modell nachlegt, kann ueber
        ''' <see cref="ErneutPruefen"/> auch ohne Neustart nachsehen lassen.</summary>
        Public Shared Sub PruefeBestand()
            Dim gefunden = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each name In BekannteDateien
                For Each ort In Suchorte
                    If String.IsNullOrEmpty(ort) Then Continue For
                    Dim p = Path.Combine(ort, name)
                    If File.Exists(p) Then
                        gefunden(name) = p
                        Exit For
                    End If
                Next
            Next
            SyncLock _sperre
                _bestand = gefunden
            End SyncLock
            DiagnosticLogService.LogAlways("KiModell",
                $"Modelle: {gefunden.Count} von {BekannteEintraege.Count} gefunden" &
                If(gefunden.Count = 0, "", " (" & String.Join(", ", gefunden.Keys) & ")"))
        End Sub

        ''' <summary>Noch einmal nachsehen - fuer den Fall, dass jemand waehrend der Sitzung ein
        ''' Modell abgelegt hat. Bereits offene Sitzungen bleiben, sie sind ja gueltig.</summary>
        Public Shared Sub ErneutPruefen()
            PruefeBestand()
        End Sub

        ''' <summary>Vollstaendiger Pfad einer Modelldatei, am ersten Ort, an dem sie liegt. Leer,
        ''' wenn sie nirgends liegt.</summary>
        Public Shared Function ModellPfad(dateiName As String) As String
            If String.IsNullOrWhiteSpace(dateiName) Then Return ""
            Dim tabelle As Dictionary(Of String, String)
            SyncLock _sperre
                tabelle = _bestand
            End SyncLock
            ' Noch nie geprueft (Pruefstand, frueher Aufruf): dann jetzt, statt Nichts zu melden.
            If tabelle Is Nothing Then
                PruefeBestand()
                SyncLock _sperre
                    tabelle = _bestand
                End SyncLock
            End If
            Dim p As String = Nothing
            If tabelle IsNot Nothing AndAlso tabelle.TryGetValue(dateiName, p) Then Return p
            Return ""
        End Function

        ''' <summary>Liegt dieses Modell irgendwo vor?</summary>
        Public Shared Function ModellVorhanden(dateiName As String) As Boolean
            Return Not String.IsNullOrEmpty(ModellPfad(dateiName))
        End Function

        ''' <summary>Die Sitzung zu einem Modell, oder Nothing.
        '''
        ''' Sitzungen bleiben offen und werden geteilt: das Laden kostet je nach Modell hunderte
        ''' Millisekunden, und ein Klick-fuer-Klick neu geladenes Modell waere unbenutzbar. Der
        ''' Besitz bleibt HIER - der Aufrufer darf sie nicht schliessen.</summary>
        Public Shared Function Sitzung(dateiName As String) As InferenceSession
            If Not LaufzeitVerfuegbar Then Return Nothing
            Dim pfad = ModellPfad(dateiName)
            If String.IsNullOrEmpty(pfad) Then Return Nothing
            SyncLock _sperre
                Dim vorhanden As InferenceSession = Nothing
                If _sitzungen.TryGetValue(pfad, vorhanden) Then Return vorhanden
                Try
                    Dim optionen = New SessionOptions()
                    ' Ein Modell laeuft waehrend der Bearbeitung neben der Vorschau. Alle Kerne zu
                    ' belegen liesse die Oberflaeche stehen - die Haelfte, mindestens einer.
                    optionen.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount \ 2)
                    optionen.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    ' Nur noch echte Fehler. Beim Optimieren meldet die Laufzeit sonst jede
                    ' ungenutzte Groesse im Modell einzeln auf die Fehlerausgabe - hunderte Zeilen,
                    ' die niemanden etwas angehen und echte Meldungen zudecken.
                    optionen.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                    Dim neu = New InferenceSession(pfad, optionen)
                    _sitzungen(pfad) = neu
                    Return neu
                Catch ex As Exception
                    DiagnosticLogService.LogAlways("KiModell", $"laedt nicht: {Path.GetFileName(pfad)} - {ex.Message}")
                    Return Nothing
                End Try
            End SyncLock
        End Function

        ''' <summary>Alle Sitzungen schliessen. Fuer den Prueftstand und das Beenden.</summary>
        Public Shared Sub GibFrei()
            SyncLock _sperre
                For Each s In _sitzungen.Values
                    Try
                        s.Dispose()
                    Catch
                    End Try
                Next
                _sitzungen.Clear()
            End SyncLock
        End Sub

        ''' <summary>Ein Bild als Tensor in der Form [1, 3, h, w], Kanaele getrennt, Werte 0..1 und
        ''' danach normiert. So erwarten es die allermeisten Bildmodelle.
        '''
        ''' <paramref name="mittel"/> und <paramref name="streuung"/> je Kanal in RGB-Reihenfolge.
        ''' Ohne Normierung 0 und 1 uebergeben.</summary>
        Public Shared Function AlsTensor(bild As SkiaSharp.SKBitmap,
                                         mittel As Single(), streuung As Single()) As DenseTensor(Of Single)
            If bild Is Nothing Then Return Nothing
            Dim w = bild.Width, h = bild.Height
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim m = If(mittel IsNot Nothing AndAlso mittel.Length = 3, mittel, New Single() {0.0F, 0.0F, 0.0F})
            Dim s = If(streuung IsNot Nothing AndAlso streuung.Length = 3, streuung, New Single() {1.0F, 1.0F, 1.0F})

            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, h, w})
            Dim ziel = tensor.Buffer.Span
            Dim ebene = w * h
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim p = bild.GetPixel(x, y)
                    Dim i = y * w + x
                    ziel(i) = (p.Red / 255.0F - m(0)) / s(0)
                    ziel(ebene + i) = (p.Green / 255.0F - m(1)) / s(1)
                    ziel(ebene * 2 + i) = (p.Blue / 255.0F - m(2)) / s(2)
                Next
            Next
            Return tensor
        End Function

    End Class

End Namespace
