Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Xml.Linq
Imports System.Reflection

Namespace Services

    ''' <summary>Kalibrierdaten fuer Objektive: Verzeichnung, Farbquerfehler und Vignettierung.
    '''
    ''' Die Daten stammen aus einer offenen, unter Creative Commons Attribution-ShareAlike 3.0
    ''' stehenden Sammlung und liegen UNVERAENDERT als eigenstaendige Dateien in
    ''' Assets/Objektivdaten. Gelesen wird nur; die Namensnennung steht in der Technologieliste der
    ''' Einstellungen.
    '''
    ''' Wir bilden die Bibliothek NICHT nach. Gebraucht werden drei Kennlinien und ein Abgleich
    ''' ueber die EXIF-Felder - der Rest (Projektionswechsel, Fischauge, automatisches Nachskalieren)
    ''' hat hier keinen Zweck.</summary>
    Public NotInheritable Class LensDataService

        Private Sub New()
        End Sub

        ' ── Datenmodell ─────────────────────────────────────────────────────────

        ''' <summary>Eine Verzeichnungs-Stuetzstelle. <see cref="Modell"/> entscheidet, wie
        ''' <see cref="A"/>/<see cref="B"/>/<see cref="C"/> zu lesen sind.</summary>
        Public NotInheritable Class DistortionValue
            Public Property Brennweite As Double
            Public Property Modell As String = ""
            Public Property A As Double
            Public Property B As Double
            Public Property C As Double
        End Class

        Public NotInheritable Class ChromaticAberrationValue
            Public Property Brennweite As Double
            Public Property Modell As String = ""
            ' Rot: rd = ru * (Br*ru^2 + Cr*ru + Vr); Blau entsprechend.
            Public Property Br As Double
            Public Property Cr As Double
            Public Property Vr As Double = 1.0
            Public Property Bb As Double
            Public Property Cb As Double
            Public Property Vb As Double = 1.0
        End Class

        Public NotInheritable Class VignettingValue
            Public Property Brennweite As Double
            Public Property Aperture As Double
            Public Property Distance As Double
            Public Property K1 As Double
            Public Property K2 As Double
            Public Property K3 As Double
        End Class

        Public NotInheritable Class LensEntry
            Public Property Maker As String = ""
            Public Property Modell As String = ""
            Public Property Namen As New List(Of String)()
            Public Property Anschluesse As New List(Of String)()
            Public Property CropFactor As Double = 1.0
            Public Property Seitenverhaeltnis As Double = 1.5
            Public Property Distortion As New List(Of DistortionValue)()
            Public Property ChromaticAberration As New List(Of ChromaticAberrationValue)()
            Public Property Vignetting As New List(Of VignettingValue)()
        End Class

        Public NotInheritable Class CameraEntry
            Public Property Maker As String = ""
            Public Property Modell As String = ""
            Public Property CropFactor As Double = 1.0
            Public Property Anschluesse As New List(Of String)()
        End Class

        ''' <summary>Das Ergebnis eines Abgleichs: die drei fertig interpolierten Kennlinien fuer
        ''' GENAU diese Aufnahme, plus die Umrechnung in das normierte Koordinatensystem.
        '''
        ''' Zwei verschiedene Radien, das ist die haeufigste Falle: Verzeichnung und Farbquerfehler
        ''' rechnen mit r = 1 an der Mitte der LANGEN Kante (also halbe Bildhoehe im Querformat),
        ''' die Vignettierung dagegen mit r = 1 in der ECKE. Wer denselben Radius fuer beides
        ''' nimmt, korrigiert um den Faktor der Bilddiagonale daneben.</summary>
        Public NotInheritable Class Korrektur
            Public Property LensName As String = ""
            Public Property Brennweite As Double
            Public Property Aperture As Double

            ''' Pixel mal diesem Faktor ergibt den Radius im System der Verzeichnung/des
            ''' Farbquerfehlers (r = 1 an der Mitte der langen Kante).
            Public Property NormScale As Double = 1.0
            ''' Der Radius der Verzeichnung mal diesem Faktor ergibt den Radius der Vignettierung
            ''' (r = 1 in der Ecke).
            Public Property CornerScale As Double = 1.0

            Public Property HasDistortion As Boolean
            Public Property DistortionModel As String = ""
            Public Property Va As Double
            Public Property Vb As Double
            Public Property Vc As Double

            Public Property HasChromaticAberration As Boolean
            Public Property TcaBr As Double
            Public Property TcaCr As Double
            Public Property TcaVr As Double = 1.0
            Public Property TcaBb As Double
            Public Property TcaCb As Double
            Public Property TcaVb As Double = 1.0

            ''' Staerke je Korrektur, 1,0 = wie kalibriert. Siehe ImageAdjustments.Lens*Amount.
            Public Property DistortionStrength As Double = 1.0
            Public Property ChromaticAberrationStrength As Double = 1.0
            Public Property VignettingStrength As Double = 1.0

            Public Property HasVignetting As Boolean
            Public Property Vk1 As Double
            Public Property Vk2 As Double
            Public Property Vk3 As Double

            Public ReadOnly Property HasAnything As Boolean
                Get
                    Return HasDistortion OrElse HasChromaticAberration OrElse HasVignetting
                End Get
            End Property
        End Class

        ' ── Die Kennlinien ──────────────────────────────────────────────────────
        '
        ' Alle drei Formeln bilden den KORRIGIERTEN Radius auf den VERZEICHNETEN ab, nicht
        ' umgekehrt. Das sieht verkehrt herum aus, ist aber genau richtig: gerechnet wird vom
        ' fertigen Zielbild aus rueckwaerts, um im Originalbild nachzuschlagen. Wer die Richtung
        ' dreht, verdoppelt den Fehler statt ihn zu entfernen.

        ''' <summary>Verzeichnung: korrigierter Radius zu verzeichnetem Radius.</summary>
        Public Shared Function DistortionRadius(k As Korrektur, ru As Double) As Double
            Dim rd = DistortionRadiusFull(k, ru)
            ' Die Staerke skaliert die ABWEICHUNG vom Nichtstun, nicht die Koeffizienten: nur so
            ' bedeutet 0 wirklich "unveraendert" und 100 "wie kalibriert", unabhaengig vom Modell.
            Return ru + (rd - ru) * k.DistortionStrength
        End Function

        Private Shared Function DistortionRadiusFull(k As Korrektur, ru As Double) As Double
            Select Case k.DistortionModel
                Case "poly3"
                    ' rd = ru * (1 - k1 + k1*ru^2), k1 steckt in Va.
                    Return ru * (1.0 - k.Va + k.Va * ru * ru)
                Case "poly5"
                    ' rd = ru * (1 + k1*ru^2 + k2*ru^4)
                    Dim r2 = ru * ru
                    Return ru * (1.0 + k.Va * r2 + k.Vb * r2 * r2)
                Case "ptlens"
                    ' rd = ru * (a*ru^3 + b*ru^2 + c*ru + 1 - a - b - c)
                    Return ru * (k.Va * ru * ru * ru + k.Vb * ru * ru + k.Vc * ru +
                                 1.0 - k.Va - k.Vb - k.Vc)
                Case Else
                    Return ru
            End Select
        End Function

        ''' <summary>Farbquerfehler: der Faktor, mit dem der Rot- bzw. Blaukanal an diesem Radius
        ''' abgetastet werden muss. Rueckgabe 1 heisst "unveraendert".</summary>
        Public Shared Function ChromaticAberrationFactor(k As Korrektur, ru As Double, rot As Boolean) As Double
            If ru <= 0.0 Then Return 1.0
            Dim b = If(rot, k.TcaBr, k.TcaBb)
            Dim c = If(rot, k.TcaCr, k.TcaCb)
            Dim v = If(rot, k.TcaVr, k.TcaVb)
            Dim f = b * ru * ru + c * ru + v
            Return 1.0 + (f - 1.0) * k.ChromaticAberrationStrength
        End Function

        ''' <summary>Vignettierung: der Helligkeitsabfall an diesem Radius (r = 1 in der ECKE).
        ''' Korrigiert wird durch TEILEN durch diesen Wert - der Wert beschreibt den Fehler, nicht
        ''' seine Behebung.</summary>
        Public Shared Function VignettingFactor(k As Korrektur, rCorner As Double) As Double
            Dim r2 = rCorner * rCorner
            Dim r4 = r2 * r2
            Dim c = 1.0 + k.Vk1 * r2 + k.Vk2 * r4 + k.Vk3 * r4 * r2
            Return 1.0 + (c - 1.0) * k.VignettingStrength
        End Function

        ' ── Abgleich ────────────────────────────────────────────────────────────

        Private Shared ReadOnly _ladeLock As New Object()
        Private Shared _geladen As Boolean = False
        Private Shared _objektive As New List(Of LensEntry)()
        Private Shared _kameras As New List(Of CameraEntry)()
        Private Shared _ladeFehler As String = ""
        Private Shared ReadOnly _anschlussVertraegt As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)

        ''' <summary>Wie viele Objektive und Kameras die Sammlung traegt. Fuer die Einstellungen
        ''' und den Pruefstand; loest das Laden aus.</summary>
        Public Shared Function Inventory() As (Objektive As Integer, Kameras As Integer, Fehler As String)
            LoadOnce()
            Return (_objektive.Count, _kameras.Count, _ladeFehler)
        End Function

        Private Shared Sub LoadOnce()
            If _geladen Then Return
            SyncLock _ladeLock
                If _geladen Then Return
                Try
                    ' Bewusst ueber die Assembly und nicht ueber Avaloniens Ressourcenlader: der
                    ' setzt eine gestartete Anwendung voraus, und dieser Dienst muss auch im
                    ' Pruefstand und in Messwerkzeugen ohne Fenster laufen.
                    Dim asm = GetType(LensDataService).Assembly
                    Dim name = asm.GetManifestResourceNames().
                        FirstOrDefault(Function(n) n.EndsWith("objektivdaten.zip", StringComparison.OrdinalIgnoreCase))
                    If name Is Nothing Then
                        _ladeFehler = "objektivdaten.zip nicht in der Anwendung gefunden"
                        _geladen = True
                        Return
                    End If
                    Using strom = asm.GetManifestResourceStream(name)
                        Using archiv = New ZipArchive(strom, ZipArchiveMode.Read)
                            For Each entry In archiv.Entries
                                If Not entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) Then Continue For
                                Using leser = New StreamReader(entry.Open())
                                    ReadFile(leser.ReadToEnd())
                                End Using
                            Next
                        End Using
                    End Using
                Catch ex As Exception
                    _ladeFehler = ex.Message
                    DiagnosticLogService.LogException("Objektivdaten.Laden", ex)
                End Try
                _geladen = True
            End SyncLock
        End Sub

        Private Shared Sub ReadFile(xml As String)
            Dim doc As XDocument
            Try
                doc = XDocument.Parse(xml)
            Catch
                Return
            End Try
            If doc.Root Is Nothing Then Return

            ' Die Sammlung traegt selbst, welcher Anschluss welchen aufnimmt - inklusive der
            ' Adapterfaelle (ein spiegelloses Bajonett nimmt die Spiegelreflex-Objektive derselben
            ' Marke). Ohne das waere ein per Adapter angesetztes Objektiv nicht korrigierbar.
            For Each m In doc.Root.Elements("mount")
                Dim name = TextVon(m, "name")
                If name.Length = 0 Then Continue For
                Dim satz As HashSet(Of String) = Nothing
                If Not _anschlussVertraegt.TryGetValue(name, satz) Then
                    satz = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    _anschlussVertraegt(name) = satz
                End If
                satz.Add(name)
                For Each c In m.Elements("compat")
                    Dim n = If(c.Value, "").Trim()
                    If n.Length > 0 Then satz.Add(n)
                Next
            Next

            For Each k In doc.Root.Elements("camera")
                Dim entry = New CameraEntry With {
                    .Maker = TextVon(k, "maker"),
                    .Modell = TextVon(k, "model"),
                    .CropFactor = ZahlVon(TextVon(k, "cropfactor"), 1.0)
                }
                For Each a In k.Elements("mount")
                    Dim n = If(a.Value, "").Trim()
                    If n.Length > 0 Then entry.Anschluesse.Add(n)
                Next
                If entry.Modell.Length > 0 Then _kameras.Add(entry)
            Next

            For Each l In doc.Root.Elements("lens")
                Dim entry = New LensEntry With {
                    .Maker = TextVon(l, "maker"),
                    .Modell = TextVon(l, "model"),
                    .CropFactor = ZahlVon(TextVon(l, "cropfactor"), 1.0),
                    .Seitenverhaeltnis = ZahlVon(TextVon(l, "aspect-ratio"), 1.5)
                }
                ' Ein Objektiv fuehrt oft mehrere <model>-Zeilen (verschiedene Sprachen, alternative
                ' Schreibweisen). Alle sind fuer den Abgleich brauchbar - die EXIF-Angabe der Kamera
                ' trifft mal die eine, mal die andere.
                For Each m In l.Elements("model")
                    Dim s = If(m.Value, "").Trim()
                    If s.Length > 0 AndAlso Not entry.Namen.Contains(s) Then entry.Namen.Add(s)
                Next
                For Each a In l.Elements("mount")
                    Dim n = If(a.Value, "").Trim()
                    If n.Length > 0 Then entry.Anschluesse.Add(n)
                Next
                If entry.Namen.Count = 0 Then Continue For
                If entry.Modell.Length = 0 Then entry.Modell = entry.Namen(0)

                Dim kal = l.Element("calibration")
                If kal IsNot Nothing Then
                    For Each d In kal.Elements("distortion")
                        entry.Distortion.Add(New DistortionValue With {
                            .Brennweite = ZahlVon(AttrVon(d, "focal"), 0),
                            .Modell = AttrVon(d, "model"),
                            .A = ZahlVon(If(AttrVon(d, "a"), AttrVon(d, "k1")), 0),
                            .B = ZahlVon(If(AttrVon(d, "b"), AttrVon(d, "k2")), 0),
                            .C = ZahlVon(AttrVon(d, "c"), 0)})
                    Next
                    For Each t In kal.Elements("tca")
                        ' Das lineare Modell ist ein Sonderfall des kubischen: nur der konstante
                        ' Term, die Attribute heissen dort kr/kb.
                        Dim linear = String.Equals(AttrVon(t, "model"), "linear", StringComparison.OrdinalIgnoreCase)
                        entry.ChromaticAberration.Add(New ChromaticAberrationValue With {
                            .Brennweite = ZahlVon(AttrVon(t, "focal"), 0),
                            .Modell = AttrVon(t, "model"),
                            .Br = ZahlVon(AttrVon(t, "br"), 0),
                            .Cr = ZahlVon(AttrVon(t, "cr"), 0),
                            .Vr = ZahlVon(If(linear, AttrVon(t, "kr"), AttrVon(t, "vr")), 1.0),
                            .Bb = ZahlVon(AttrVon(t, "bb"), 0),
                            .Cb = ZahlVon(AttrVon(t, "cb"), 0),
                            .Vb = ZahlVon(If(linear, AttrVon(t, "kb"), AttrVon(t, "vb")), 1.0)})
                    Next
                    For Each v In kal.Elements("vignetting")
                        entry.Vignetting.Add(New VignettingValue With {
                            .Brennweite = ZahlVon(AttrVon(v, "focal"), 0),
                            .Aperture = ZahlVon(AttrVon(v, "aperture"), 0),
                            .Distance = ZahlVon(AttrVon(v, "distance"), 1000),
                            .K1 = ZahlVon(AttrVon(v, "k1"), 0),
                            .K2 = ZahlVon(AttrVon(v, "k2"), 0),
                            .K3 = ZahlVon(AttrVon(v, "k3"), 0)})
                    Next
                End If
                _objektive.Add(entry)
            Next
        End Sub

        Private Shared Function TextVon(e As XElement, name As String) As String
            Dim k = e.Element(name)
            Return If(k Is Nothing, "", If(k.Value, "").Trim())
        End Function

        Private Shared Function AttrVon(e As XElement, name As String) As String
            Dim a = e.Attribute(name)
            Return If(a Is Nothing, Nothing, a.Value)
        End Function

        Private Shared Function ZahlVon(s As String, vorgabe As Double) As Double
            Dim d As Double
            If Not String.IsNullOrWhiteSpace(s) AndAlso
               Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            Return vorgabe
        End Function

        ''' <summary>Vergleichsform eines Namens: klein, ohne Satzzeichen, ohne Mehrfach-Leerzeichen.
        ''' Die EXIF-Angaben der Kameras sind uneinheitlich ("EF24-70mm f/2.8L II USM" gegen
        ''' "Canon EF 24-70mm f/2.8L II USM"), deshalb wird nicht auf Gleichheit verglichen.</summary>
        Private Shared Function NormalizedForName(s As String) As String
            If String.IsNullOrWhiteSpace(s) Then Return ""
            Dim t = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9\.]+", " ")
            Return Regex.Replace(t, "\s+", " ").Trim()
        End Function

        ''' <summary>Wie gut passen zwei Namen zusammen? Gezaehlt werden die gemeinsamen Bestandteile,
        ''' und Bestandteile mit Ziffern (Brennweiten, Lichtstaerke) zaehlen dreifach - sie
        ''' unterscheiden zwei Objektive derselben Reihe, waehrend "ef", "usm" oder der
        ''' Herstellername auf Dutzende passen.</summary>
        Private Shared Function Similarity(a As String, b As String) As Double
            Dim ta = NormalizedForName(a).Split(" "c).Where(Function(x) x.Length > 0).ToList()
            Dim tb = NormalizedForName(b).Split(" "c).Where(Function(x) x.Length > 0).ToList()
            If ta.Count = 0 OrElse tb.Count = 0 Then Return 0
            Dim satzB = New HashSet(Of String)(tb)
            Dim treffer As Double = 0, total As Double = 0
            For Each t In ta
                Dim gewicht = If(t.Any(AddressOf Char.IsDigit), 3.0, 1.0)
                total += gewicht
                If satzB.Contains(t) Then treffer += gewicht
            Next
            ' Beidseitig bewerten: sonst gewinnt ein sehr kurzer Datenbankname, der in jedem
            ' laengeren EXIF-Namen vollstaendig enthalten ist.
            Dim satzA = New HashSet(Of String)(ta)
            Dim treffer2 As Double = 0, gesamt2 As Double = 0
            For Each t In tb
                Dim gewicht = If(t.Any(AddressOf Char.IsDigit), 3.0, 1.0)
                gesamt2 += gewicht
                If satzA.Contains(t) Then treffer2 += gewicht
            Next
            Return (treffer / total + treffer2 / gesamt2) / 2.0
        End Function

        ''' <summary>Unterhalb dieser Aehnlichkeit gilt ein Objektiv als NICHT gefunden. Lieber gar
        ''' keine Korrektur als die eines anderen Objektivs: eine falsche Kennlinie verbiegt das Bild
        ''' sichtbar, eine fehlende laesst es wie bisher.</summary>
        Private Const MatchThreshold As Double = 0.62

        ''' <summary>Von Hand gesetzte Zuordnungen: EXIF-Objektivname zu Eintrag in der Sammlung.
        '''
        ''' Der Schluessel ist der NAME AUS DEM EXIF, nicht der Bildpfad. Der haeufigste Grund fuer
        ''' einen Fehlschlag ist naemlich kein fehlender Datensatz, sondern eine andere Schreibweise
        ''' desselben Objektivs - und die ist bei jeder Aufnahme damit dieselbe. Einmal zugeordnet
        ''' gilt es fuer den ganzen Bestand dieses Objektivs statt Bild fuer Bild.</summary>
        Private Shared ReadOnly _zuordnungLock As New Object()
        Private Shared _zuordnungen As Dictionary(Of String, String) = Nothing

        Private Shared Function Zuordnungen() As Dictionary(Of String, String)
            SyncLock _zuordnungLock
                If _zuordnungen Is Nothing Then
                    _zuordnungen = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                    Try
                        For Each z In AppSettingsService.Load().LensAssignments
                            If Not String.IsNullOrWhiteSpace(z.ExifName) Then _zuordnungen(z.ExifName) = z.Modell
                        Next
                    Catch
                    End Try
                End If
                Return _zuordnungen
            End SyncLock
        End Function

        ''' <summary>Ein Objektiv von Hand zuordnen. Leeres Modell loest die Zuordnung wieder.
        '''
        ''' FALSE heisst: die Zuordnung ist NICHT dauerhaft. Sie gilt dann fuer diese Sitzung und ist
        ''' nach dem naechsten Start weg - vorher verschwand der Fehlschlag hier wortlos und ohne
        ''' Protokolleintrag, und dem Nutzer sah niemand an, warum seine Wahl nicht wiederkam.</summary>
        Public Shared Function SetAssignment(exifName As String, model As String) As Boolean
            If String.IsNullOrWhiteSpace(exifName) Then Return False
            Dim saved = False
            SyncLock _zuordnungLock
                Dim assignments = Zuordnungen()
                If String.IsNullOrWhiteSpace(model) Then assignments.Remove(exifName) Else assignments(exifName) = model
                Try
                    Dim settings = AppSettingsService.Load()
                    settings.LensAssignments = assignments.Select(Function(p) New LensAssignment With {
                        .ExifName = p.Key, .Modell = p.Value}).ToList()
                    AppSettingsService.Save(settings)
                    saved = True
                Catch ex As Exception
                    DiagnosticLogService.LogException("Objektivdaten.Zuordnung", ex)
                End Try
            End SyncLock
            ClearFileCache()
            Return saved
        End Function

        Public Shared Function ZuordnungFuer(exifName As String) As String
            If String.IsNullOrWhiteSpace(exifName) Then Return ""
            Dim m As String = Nothing
            SyncLock _zuordnungLock
                If Zuordnungen().TryGetValue(exifName, m) Then Return m
            End SyncLock
            Return ""
        End Function

        ''' <summary>Alle Eintraege der Sammlung, die an diese Kamera passen - fuer die Auswahlliste.
        ''' Der Anschluss filtert wie beim automatischen Abgleich: ein Objektiv fuer ein fremdes
        ''' Bajonett anzubieten hiesse, den Nutzer in genau den Fehler zu fuehren, den der
        ''' automatische Weg vermeidet.</summary>
        Public Shared Function MatchingLenses(kameraHersteller As String, kameraModell As String) As List(Of String)
            LoadOnce()
            Dim camera = BestCamera(kameraHersteller, kameraModell)
            Return _objektive.
                Where(Function(o) PasstAnschluss(o, camera)).
                Select(Function(o) o.Modell).
                Where(Function(n) Not String.IsNullOrWhiteSpace(n)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                OrderBy(Function(n) n, StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Public Shared Sub ClearFileCache()
            SyncLock _dateiCacheLock
                _dateiCache.Clear()
            End SyncLock
        End Sub

        ''' <summary>Die Kennlinien fuer eine konkrete Bilddatei: Kamera, Objektiv, Brennweite und
        ''' Blende kommen aus dem EXIF. Findet sich nichts Passendes, ist die Rueckgabe Nothing -
        ''' lieber keine Korrektur als die eines fremden Objektivs.
        '''
        ''' Das Ergebnis wird je Datei gemerkt: der Abgleich laeuft ueber 1300 Objektive und wird
        ''' beim Blaettern durch einen Ordner sonst fuer jedes Bild neu gerechnet.</summary>
        Public Shared Function FindCorrectionForFile(path As String,
                                                       Optional modellVorgabe As String = "") As Korrektur
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            ' Die Vorgabe gehoert in den Schluessel: sonst liefert der Zwischenspeicher das Ergebnis
            ' der vorigen Wahl zurueck, und die Auswahl saehe wirkungslos aus.
            Dim key = If(String.IsNullOrWhiteSpace(modellVorgabe), path, path & "|" & modellVorgabe)
            Dim gemerkt As Korrektur = Nothing
            SyncLock _dateiCacheLock
                ' KOPIE herausgeben: der Aufrufer streicht daran die fuer dieses Bild
                ' abgeschalteten Korrekturen weg (siehe Filtere). Am gemerkten Objekt getan, waere
                ' die Abschaltung des einen Bildes fuer alle weiteren mit demselben Objektiv gueltig.
                If _dateiCache.TryGetValue(key, gemerkt) Then Return CloneEntry(gemerkt)
            End SyncLock

            Dim result As Korrektur = Nothing
            Try
                Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                Dim ifd0 = verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)().FirstOrDefault()
                Dim sub0 = verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifSubIfdDirectory)().FirstOrDefault()
                Dim maker = If(ifd0?.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake), "")
                Dim modell = If(ifd0?.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel), "")
                Dim lens = If(sub0?.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagLensModel), "")
                Dim brennweite = FirstNumber(sub0?.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFocalLength))
                Dim blende = FirstNumber(sub0?.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFNumber))
                Dim width = 0, height = 0
                For Each d In verzeichnisse
                    Dim w = d.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagImageWidth)
                    Dim h = d.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagImageHeight)
                    Dim wi = CInt(FirstNumber(w)), hi = CInt(FirstNumber(h))
                    If wi > width Then width = wi
                    If hi > height Then height = hi
                Next
                ' Eine von Hand gesetzte Zuordnung ersetzt den EXIF-Namen. Sie ist bewusst
                ' STAERKER als die Automatik: wer sie gesetzt hat, weiss mehr ueber sein Objektiv
                ' als die Schreibweise im EXIF verraet.
                ' Reihenfolge: die Vorgabe aus dem Rezept schlaegt alles (sie gilt fuer genau
                ' dieses Bild), danach die dauerhafte Zuordnung ueber den Objektivnamen, zuletzt der
                ' Name aus den Aufnahmedaten.
                Dim zugeordnet = ZuordnungFuer(lens)
                Dim suchName = lens
                If Not String.IsNullOrWhiteSpace(zugeordnet) Then suchName = zugeordnet
                If Not String.IsNullOrWhiteSpace(modellVorgabe) Then suchName = modellVorgabe
                ' Ohne Objektivangabe UND ohne Vorgabe gibt es nichts zu suchen - aber MIT
                ' Vorgabe schon: dann hat der Nutzer gesagt, welches Objektiv es war.
                ' Ohne Aufnahmedaten fehlen auch die Bildmasse im EXIF. Die stehen aber in der
                ' DATEI - genau bei diesen Bildern wird die Zuordnung von Hand gebraucht, und ohne
                ' Masse laesst sich der Radius nicht normieren.
                If width <= 1 OrElse height <= 1 Then
                    Dim fromFile = ExifService.ReadImageDimensions(path)
                    width = fromFile.Width.GetValueOrDefault()
                    height = fromFile.Height.GetValueOrDefault()
                End If
                If width > 1 AndAlso height > 1 Then
                    result = FindCorrection(maker, modell, suchName, brennweite, blende, width, height)
                End If
            Catch
                result = Nothing
            End Try

            SyncLock _dateiCacheLock
                _dateiCache(key) = result
            End SyncLock
            Return CloneEntry(result)
        End Function

        Private Shared Function CloneEntry(k As Korrektur) As Korrektur
            If k Is Nothing Then Return Nothing
            Return New Korrektur With {
                .LensName = k.LensName, .Brennweite = k.Brennweite, .Aperture = k.Aperture,
                .NormScale = k.NormScale, .CornerScale = k.CornerScale,
                .HasDistortion = k.HasDistortion, .DistortionModel = k.DistortionModel,
                .Va = k.Va, .Vb = k.Vb, .Vc = k.Vc,
                .HasChromaticAberration = k.HasChromaticAberration,
                .TcaBr = k.TcaBr, .TcaCr = k.TcaCr, .TcaVr = k.TcaVr,
                .TcaBb = k.TcaBb, .TcaCb = k.TcaCb, .TcaVb = k.TcaVb,
                .HasVignetting = k.HasVignetting, .Vk1 = k.Vk1, .Vk2 = k.Vk2, .Vk3 = k.Vk3,
                .DistortionStrength = k.DistortionStrength,
                .ChromaticAberrationStrength = k.ChromaticAberrationStrength,
                .VignettingStrength = k.VignettingStrength}
        End Function

        Private Shared ReadOnly _dateiCacheLock As New Object()
        Private Shared ReadOnly _dateiCache As New Dictionary(Of String, Korrektur)(StringComparer.Ordinal)

        ''' <summary>Die erste Zahl aus einem EXIF-Text ("70 mm", "f/5,6"). Die Beschreibungen sind
        ''' bereits nach der Anzeigesprache formatiert, deshalb zaehlen Punkt UND Komma als
        ''' Dezimaltrenner.</summary>
        Private Shared Function FirstNumber(s As String) As Double
            If String.IsNullOrWhiteSpace(s) Then Return 0
            Dim m = Regex.Match(s, "[0-9]+([.,][0-9]+)?")
            If Not m.Success Then Return 0
            Dim d As Double
            If Double.TryParse(m.Value.Replace(","c, "."c), NumberStyles.Float,
                               CultureInfo.InvariantCulture, d) Then Return d
            Return 0
        End Function

        ''' <summary>Welche der drei Korrekturen fuer DIESES Bild gelten sollen. Jeder Wert Nothing
        ''' heisst "wie in den Einstellungen vorgegeben"; nur so laesst sich unterscheiden, ob AUS
        ''' eine Entscheidung am Bild war oder nur der damalige Standard.</summary>
        Public NotInheritable Class Wahl
            Public Property Distortion As Boolean?
            Public Property ChromaticAberration As Boolean?
            Public Property Vignetting As Boolean?
            ''' 1,0 = wie kalibriert.
            Public Property DistortionStrength As Double = 1.0
            Public Property ChromaticAberrationStrength As Double = 1.0
            Public Property VignettingStrength As Double = 1.0
            ''' Von Hand gewaehltes Objektiv fuer GENAU dieses Bild - nur belegt, wenn die
            ''' Aufnahmedaten keines nennen.
            Public Property LensModel As String = ""
        End Class

        Private Shared Function Gilt(feld As Boolean?, vorgabe As Boolean) As Boolean
            Return If(feld.HasValue, feld.Value, vorgabe)
        End Function

        ''' <summary>Streicht aus einem Ergebnis, was fuer dieses Bild abgeschaltet ist. Rueckgabe
        ''' Nothing, wenn danach nichts mehr uebrig ist - dann laeuft im Decode auch keine der
        ''' teuren Schleifen an.</summary>
        Public Shared Function Filtere(k As Korrektur, wahl As Wahl, vorgabe As Boolean) As Korrektur
            If k Is Nothing Then Return Nothing
            Dim verz = vorgabe, tca = vorgabe, vign = vorgabe
            If wahl IsNot Nothing Then
                verz = Gilt(wahl.Distortion, vorgabe)
                tca = Gilt(wahl.ChromaticAberration, vorgabe)
                vign = Gilt(wahl.Vignetting, vorgabe)
            End If
            If Not verz Then k.HasDistortion = False
            If Not tca Then k.HasChromaticAberration = False
            If Not vign Then k.HasVignetting = False
            If wahl IsNot Nothing Then
                k.DistortionStrength = wahl.DistortionStrength
                k.ChromaticAberrationStrength = wahl.ChromaticAberrationStrength
                k.VignettingStrength = wahl.VignettingStrength
            End If
            ' Staerke null ist dasselbe wie abgeschaltet - dann muss auch keine Schleife anlaufen.
            If k.DistortionStrength = 0.0 Then k.HasDistortion = False
            If k.ChromaticAberrationStrength = 0.0 Then k.HasChromaticAberration = False
            If k.VignettingStrength = 0.0 Then k.HasVignetting = False
            Return If(k.HasAnything, k, Nothing)
        End Function

        ''' <summary>Sucht die Kennlinien fuer eine konkrete Aufnahme. Rueckgabe Nothing, wenn
        ''' Objektiv oder Brennweite unbekannt sind.</summary>
        Public Shared Function FindCorrection(kameraHersteller As String, kameraModell As String,
                                              objektivName As String,
                                              brennweiteMm As Double, blende As Double,
                                              width As Integer, height As Integer) As Korrektur
            If width < 2 OrElse height < 2 Then Return Nothing
            If String.IsNullOrWhiteSpace(objektivName) Then Return Nothing
            LoadOnce()
            If _objektive.Count = 0 Then Return Nothing

            ' Erst die Kamera, dann das Objektiv: der Anschluss der Kamera ist der schaerfste
            ' Filter, den wir haben. Ohne ihn gewinnt bei aehnlich benannten Reihen leicht die
            ' FALSCHE Bauform - gemessen am Referenzfoto wurde die spiegellose Fassung eines
            ' Objektivs gefunden, das in Wahrheit an einer Spiegelreflex-Fassung sass. Deren
            ' Kennlinie haette das Bild sichtbar verbogen.
            Dim camera = BestCamera(kameraHersteller, kameraModell)
            Dim obj = BestLens(objektivName, camera)
            If obj Is Nothing Then Return Nothing

            ' Ohne Brennweite laesst sich normalerweise kein Kalibrierpunkt waehlen. Bei einer
            ' FESTBRENNWEITE gibt es aber nur einen - dann ist die Angabe entbehrlich. Das ist genau
            ' der Fall, der bei Bildern ohne Aufnahmedaten weiterhilft: wer sein Objektiv von Hand
            ' zuordnet, hat oft auch sonst nichts im EXIF stehen.
            If brennweiteMm <= 0 Then
                brennweiteMm = EinzigeBrennweite(obj)
                If brennweiteMm <= 0 Then Return Nothing
            End If

            ' Der Crop-Faktor der KAMERA, nicht des Objektivs: die Kennlinien sind an einem
            ' bestimmten Sensor gemessen worden, und ein anderer Sensor sieht einen anderen
            ' Ausschnitt desselben Bildkreises.
            Dim cameraCrop = If(camera IsNot Nothing, camera.CropFactor, obj.CropFactor)

            Dim k = New Korrektur With {
                .LensName = obj.Modell,
                .Brennweite = brennweiteMm,
                .Aperture = blende
            }
            ComputeNormalization(k, obj, cameraCrop, width, height)
            ApplyDistortion(k, obj, brennweiteMm)
            ApplyChromaticAberration(k, obj, brennweiteMm)
            ApplyVignetting(k, obj, brennweiteMm, blende)
            Return If(k.HasAnything, k, Nothing)
        End Function

        ''' <summary>Passt dieses Objektiv ueberhaupt an diese Kamera? Kennen wir den Anschluss der
        ''' Kamera nicht, wird nicht gefiltert - sonst faende man bei unbekannten Gehaeusen gar
        ''' nichts mehr.</summary>
        Private Shared Function PasstAnschluss(obj As LensEntry, camera As CameraEntry) As Boolean
            If camera Is Nothing OrElse camera.Anschluesse.Count = 0 Then Return True
            If obj.Anschluesse.Count = 0 Then Return True
            For Each ka In camera.Anschluesse
                Dim satz As HashSet(Of String) = Nothing
                For Each oa In obj.Anschluesse
                    If String.Equals(ka, oa, StringComparison.OrdinalIgnoreCase) Then Return True
                    If _anschlussVertraegt.TryGetValue(ka, satz) AndAlso satz.Contains(oa) Then Return True
                Next
            Next
            Return False
        End Function

        ''' <summary>Sucht das Objektiv. Namensgleichheit allein reicht NICHT: dieselbe Rechnung
        ''' steht in der Sammlung mehrfach, einmal je Sensorgroesse, an der sie gemessen wurde - und
        ''' die Fassungen unterscheiden sich stark im Umfang (an einem Beispiel 9 Farbquerfehler- und
        ''' 120 Vignettierungswerte gegen gar keine). Wer einfach den ersten Namenstreffer nimmt,
        ''' bekommt zufaellig mal die eine, mal die andere.
        '''
        ''' Bei Gleichstand entscheidet deshalb erst der Abstand der Sensorgroesse zur Kamera, dann
        ''' der Umfang der Messwerte.</summary>
        ''' <summary>Braucht dieses Objektiv eine Brennweitenangabe? Ein Zoom schon - ohne sie ist
        ''' nicht zu entscheiden, welcher Kalibrierpunkt gilt. Fuer die Anzeige, damit die Gruppe
        ''' sagen kann, WARUM nichts passiert, statt stumm zu bleiben.</summary>
        Public Shared Function BrauchtBrennweite(modell As String, kameraModell As String) As Boolean
            If String.IsNullOrWhiteSpace(modell) Then Return False
            LoadOnce()
            Dim obj = BestLens(modell, BestCamera("", kameraModell))
            If obj Is Nothing Then Return False
            Return EinzigeBrennweite(obj) <= 0
        End Function

        ''' <summary>Die eine Brennweite eines Objektivs, sofern alle Messwerte bei derselben
        ''' aufgenommen wurden (Festbrennweite). Sonst 0.</summary>
        Private Shared Function EinzigeBrennweite(obj As LensEntry) As Double
            Dim values = obj.Distortion.Select(Function(x) x.Brennweite).
                Concat(obj.ChromaticAberration.Select(Function(x) x.Brennweite)).
                Concat(obj.Vignetting.Select(Function(x) x.Brennweite)).
                Where(Function(f) f > 0).Distinct().ToList()
            Return If(values.Count = 1, values(0), 0.0)
        End Function

        Private Shared Function BestLens(objektivName As String, camera As CameraEntry) As LensEntry
            Dim bester As LensEntry = Nothing
            Dim besteGuete As Double = 0
            Dim bestCropDistance As Double = Double.MaxValue
            Dim bestExtent As Integer = -1
            Dim cameraCrop = If(camera IsNot Nothing, camera.CropFactor, 0.0)

            For Each o In _objektive
                If Not PasstAnschluss(o, camera) Then Continue For
                Dim g As Double = 0
                For Each n In o.Namen
                    g = Math.Max(g, Similarity(objektivName, n))
                Next
                If g <= 0 Then Continue For

                Dim cropDistance = If(cameraCrop > 0, Math.Abs(o.CropFactor - cameraCrop), 0.0)
                Dim umfang = o.Distortion.Count + o.ChromaticAberration.Count + o.Vignetting.Count

                Dim besser = False
                If g > besteGuete + 0.0001 Then
                    besser = True
                ElseIf Math.Abs(g - besteGuete) <= 0.0001 Then
                    If cropDistance < bestCropDistance - 0.01 Then
                        besser = True
                    ElseIf Math.Abs(cropDistance - bestCropDistance) <= 0.01 AndAlso umfang > bestExtent Then
                        besser = True
                    End If
                End If

                If besser Then
                    besteGuete = g
                    bestCropDistance = cropDistance
                    bestExtent = umfang
                    bester = o
                End If
            Next
            Return If(besteGuete >= MatchThreshold, bester, Nothing)
        End Function

        Private Shared Function BestCamera(maker As String, modell As String) As CameraEntry
            If String.IsNullOrWhiteSpace(modell) Then Return Nothing
            Dim bester As CameraEntry = Nothing
            Dim besteGuete As Double = 0
            For Each c In _kameras
                Dim g = Similarity(modell, c.Modell)
                If Not String.IsNullOrWhiteSpace(maker) AndAlso
                   Not String.IsNullOrWhiteSpace(c.Maker) Then
                    ' Der Hersteller ist ein starker Filter: "5D" gibt es bei mehreren Marken.
                    If Similarity(maker, c.Maker) < 0.5 Then Continue For
                End If
                If g > besteGuete Then
                    besteGuete = g
                    bester = c
                End If
            Next
            Return If(besteGuete >= MatchThreshold, bester, Nothing)
        End Function

        ''' <summary>Die Umrechnung von Pixeln in den normierten Radius.
        '''
        ''' r = 1 liegt in der Mitte der LANGEN Kante, also bei der halben kurzen Bildseite. Dazu
        ''' kommt der Ausgleich dafuer, dass die Kennlinie an einem anderen Sensor gemessen wurde:
        ''' der Weg fuehrt ueber die Bilddiagonale, weil Crop-Faktoren genau darueber definiert
        ''' sind.</summary>
        Private Shared Sub ComputeNormalization(k As Korrektur, obj As LensEntry,
                                              cameraCrop As Double, width As Integer, height As Integer)
            Dim w = Math.Max(1, width - 1)
            Dim h = Math.Max(1, height - 1)
            Dim shortSide = CDbl(Math.Min(w, h))
            Dim imageAspectRatio = If(w < h, CDbl(h) / w, CDbl(w) / h)

            Dim kalibrierAusgleich = Math.Sqrt(obj.Seitenverhaeltnis * obj.Seitenverhaeltnis + 1.0)
            Dim ausgleich = 1.0 / Math.Sqrt(imageAspectRatio * imageAspectRatio + 1.0) *
                            (obj.CropFactor / Math.Max(0.0001, cameraCrop)) * kalibrierAusgleich

            k.NormScale = 2.0 / shortSide * ausgleich
            ' Die Vignettierung rechnet mit r = 1 in der ECKE. Im System oben liegt die Ecke bei
            ' Wurzel(Seitenverhaeltnis^2 + 1) - genau darum wird geteilt.
            k.CornerScale = 1.0 / kalibrierAusgleich
        End Sub

        ' ── Stuetzstellen ueber die Brennweite mitteln ──────────────────────────

        ''' <summary>Sucht die zwei Stuetzstellen, zwischen denen die Brennweite liegt, und gibt den
        ''' Mischanteil zurueck. Liegt sie ausserhalb, gilt die naechstgelegene unveraendert -
        ''' extrapolieren waere bei diesen Polynomen gefaehrlich.</summary>
        Private Shared Function Surrounding(Of T)(values As List(Of T), brennweite As Double,
                                               brennweiteVon As Func(Of T, Double)) _
                                               As (Unten As T, Oben As T, Anteil As Double)
            Dim sortiert = values.OrderBy(brennweiteVon).ToList()
            If sortiert.Count = 0 Then Return (Nothing, Nothing, 0)
            If sortiert.Count = 1 Then Return (sortiert(0), sortiert(0), 0)
            If brennweite <= brennweiteVon(sortiert(0)) Then Return (sortiert(0), sortiert(0), 0)
            Dim last = sortiert(sortiert.Count - 1)
            If brennweite >= brennweiteVon(last) Then Return (last, last, 0)
            For i = 0 To sortiert.Count - 2
                Dim f0 = brennweiteVon(sortiert(i)), f1 = brennweiteVon(sortiert(i + 1))
                If brennweite >= f0 AndAlso brennweite <= f1 Then
                    Dim span = f1 - f0
                    Return (sortiert(i), sortiert(i + 1), If(span <= 0, 0, (brennweite - f0) / span))
                End If
            Next
            Return (last, last, 0)
        End Function

        Private Shared Function Misch(a As Double, b As Double, share As Double) As Double
            Return a + (b - a) * share
        End Function

        Private Shared Sub ApplyDistortion(k As Korrektur, obj As LensEntry, brennweite As Double)
            If obj.Distortion.Count = 0 Then Return
            Dim u = Surrounding(obj.Distortion, brennweite, Function(x) x.Brennweite)
            If u.Unten Is Nothing Then Return
            ' Nur mischen, wenn beide Stuetzstellen dasselbe Modell fuehren - a/b/c bedeuten je
            ' Modell etwas anderes, ein Mittelwert daraus waere Unsinn.
            If u.Oben Is Nothing OrElse Not String.Equals(u.Unten.Modell, u.Oben.Modell, StringComparison.OrdinalIgnoreCase) Then
                u = (u.Unten, u.Unten, 0)
            End If
            k.DistortionModel = u.Unten.Modell
            k.Va = Misch(u.Unten.A, u.Oben.A, u.Anteil)
            k.Vb = Misch(u.Unten.B, u.Oben.B, u.Anteil)
            k.Vc = Misch(u.Unten.C, u.Oben.C, u.Anteil)
            k.HasDistortion = k.DistortionModel.Length > 0
        End Sub

        Private Shared Sub ApplyChromaticAberration(k As Korrektur, obj As LensEntry, brennweite As Double)
            If obj.ChromaticAberration.Count = 0 Then Return
            Dim u = Surrounding(obj.ChromaticAberration, brennweite, Function(x) x.Brennweite)
            If u.Unten Is Nothing Then Return
            k.TcaBr = Misch(u.Unten.Br, u.Oben.Br, u.Anteil)
            k.TcaCr = Misch(u.Unten.Cr, u.Oben.Cr, u.Anteil)
            k.TcaVr = Misch(u.Unten.Vr, u.Oben.Vr, u.Anteil)
            k.TcaBb = Misch(u.Unten.Bb, u.Oben.Bb, u.Anteil)
            k.TcaCb = Misch(u.Unten.Cb, u.Oben.Cb, u.Anteil)
            k.TcaVb = Misch(u.Unten.Vb, u.Oben.Vb, u.Anteil)
            k.HasChromaticAberration = True
        End Sub

        ''' <summary>Die Vignettierung haengt an drei Groessen. Die Entfernung kennen wir aus dem
        ''' EXIF praktisch nie zuverlaessig, deshalb wird die groesste gemessene genommen
        ''' (Unendlich-Einstellung, der Normalfall bei Landschaft und Architektur, wo die
        ''' Randabdunklung ueberhaupt auffaellt). Danach erst ueber die Blende, dann ueber die
        ''' Brennweite mitteln.</summary>
        Private Shared Sub ApplyVignetting(k As Korrektur, obj As LensEntry,
                                                  brennweite As Double, blende As Double)
            If obj.Vignetting.Count = 0 Then Return
            Dim maxEntfernung = obj.Vignetting.Max(Function(x) x.Distance)
            Dim candidates = obj.Vignetting.Where(Function(x) x.Distance = maxEntfernung).ToList()
            If candidates.Count = 0 Then Return

            ' Ohne Blendenangabe die offenste gemessene nehmen - dort ist die Abdunklung am
            ' staerksten, und eine zu schwache Korrektur ist harmloser als eine zu starke.
            Dim targetAperture = If(blende > 0, blende, candidates.Min(Function(x) x.Aperture))

            ' Je Brennweiten-Stuetzstelle die passende Blende suchen, dann ueber die Brennweite
            ' mischen. Andersherum (erst Brennweite) verwaesserte die Blendenauswahl.
            Dim jeBrennweite = candidates.GroupBy(Function(x) x.Brennweite).
                Select(Function(g) NearestAperture(g.ToList(), targetAperture)).ToList()
            Dim u = Surrounding(jeBrennweite, brennweite, Function(x) x.Brennweite)
            If u.Unten Is Nothing Then Return
            k.Vk1 = Misch(u.Unten.K1, u.Oben.K1, u.Anteil)
            k.Vk2 = Misch(u.Unten.K2, u.Oben.K2, u.Anteil)
            k.Vk3 = Misch(u.Unten.K3, u.Oben.K3, u.Anteil)
            k.HasVignetting = True
        End Sub

        Private Shared Function NearestAperture(values As List(Of VignettingValue), blende As Double) As VignettingValue
            Dim bottom = values.Where(Function(x) x.Aperture <= blende).OrderByDescending(Function(x) x.Aperture).FirstOrDefault()
            Dim top = values.Where(Function(x) x.Aperture >= blende).OrderBy(Function(x) x.Aperture).FirstOrDefault()
            If bottom Is Nothing Then Return top
            If top Is Nothing Then Return bottom
            If bottom Is top Then Return bottom
            ' In Blendenstufen mischen, nicht in Blendenzahlen: der Lichtabfall ist logarithmisch,
            ' zwischen 2.8 und 8 liegt linear gemittelt nicht die Haelfte des Effekts.
            Dim l0 = Math.Log(Math.Max(0.1, bottom.Aperture)), l1 = Math.Log(Math.Max(0.1, top.Aperture))
            Dim lz = Math.Log(Math.Max(0.1, blende))
            Dim share = If(Math.Abs(l1 - l0) < 0.000001, 0.0, (lz - l0) / (l1 - l0))
            Return New VignettingValue With {
                .Brennweite = bottom.Brennweite,
                .Aperture = blende,
                .Distance = bottom.Distance,
                .K1 = Misch(bottom.K1, top.K1, share),
                .K2 = Misch(bottom.K2, top.K2, share),
                .K3 = Misch(bottom.K3, top.K3, share)}
        End Function

    End Class

End Namespace
