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

        ''' <summary>Woher die Kennlinien einer Korrektur stammen. Die Statuszeile im Editor
        ''' nennt es, damit klar ist, warum ein Bild ohne Objektivangabe trotzdem korrigiert wird.</summary>
        Public Enum CorrectionSource
            ''' Ein Profil der Sammlung, ueber den Objektivnamen gefunden.
            Profile = 0
            ''' Das feste Objektiv einer Kompaktkamera, ueber die Kamera gefunden.
            FixedLens = 1
            ''' Die Korrekturwerte, die die Kamera selbst in die RAW schreibt.
            CameraData = 2
        End Enum

        ''' <summary>Das Ergebnis eines Abgleichs: die drei fertig interpolierten Kennlinien fuer
        ''' GENAU diese Aufnahme, plus die Umrechnung in das normierte Koordinatensystem.
        '''
        ''' Zwei verschiedene Radien, das ist die haeufigste Falle: Verzeichnung und Farbquerfehler
        ''' rechnen mit r = 1 an der Mitte der LANGEN Kante (also halbe Bildhoehe im Querformat),
        ''' die Vignettierung dagegen mit r = 1 in der ECKE. Wer denselben Radius fuer beides
        ''' nimmt, korrigiert um den Faktor der Bilddiagonale daneben.</summary>
        Public NotInheritable Class Korrektur
            Public Property LensName As String = ""
            Public Property Source As CorrectionSource = CorrectionSource.Profile

            ''' Verzeichnung als Stuetzwerte statt als Formel (Modell "knots"): das Verhaeltnis
            ''' verzeichneter zu korrigiertem Radius an gleichmaessig verteilten Stellen von der
            ''' Bildmitte (erster Wert) bis zur Ecke (letzter Wert). Dazwischen linear.
            Public Property DistortionKnots As Double()
            ''' Farbquerfehler als Stuetzwerte wie oben, je Kanal der Faktor gegenueber Gruen.
            ''' Belegt, ersetzen sie die Formel aus TcaBr/TcaCr/TcaVr und TcaBb/TcaCb/TcaVb.
            Public Property TcaRedKnots As Double()
            Public Property TcaBlueKnots As Double()
            Public Property Brennweite As Double
            Public Property Aperture As Double
            ''' Der Fokusabstand in Metern, mit dem die Vignettierung gewaehlt wurde; 0 heisst
            ''' unbekannt, dann gilt die groesste gemessene Entfernung.
            Public Property FocusDistance As Double

            ''' Pixel mal diesem Faktor ergibt den Radius im System der Verzeichnung/des
            ''' Farbquerfehlers (r = 1 an der Mitte der langen Kante). VORLAEUFIG: er gilt fuer die
            ''' Masse, mit denen gesucht wurde. Die Korrekturstufen rechnen ihn ueber
            ''' <see cref="NormScaleFor"/> auf die Masse des Bildes um, das WIRKLICH vor ihnen liegt.
            Public Property NormScale As Double = 1.0
            ''' Der Radius der Verzeichnung mal diesem Faktor ergibt den Radius der Vignettierung
            ''' (r = 1 in der Ecke).
            Public Property CornerScale As Double = 1.0

            ''' Das Seitenverhaeltnis, bei dem die Kennlinie gemessen wurde, und der Quotient aus
            ''' dem Crop-Faktor des Objektivs und dem der Kamera. Nur damit laesst sich die
            ''' Normierung auf andere Bildmasse umrechnen; 0 heisst "nicht belegt", dann bleibt es
            ''' bei <see cref="NormScale"/>.
            Public Property CalibrationAspectRatio As Double
            Public Property CropRatio As Double

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
            ''' Die Feinkorrektur von Hand als Faktor, 1 = nichts; siehe FineChromaticAberrationFactor.
            ''' Sie wird mit dem Faktor des Profils multipliziert.
            Public Property TcaRedFine As Double = 1.0
            Public Property TcaBlueFine As Double = 1.0

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
                Case "knots"
                    ' Die Stuetzwerte stehen auf der Achse mit 1 in der ECKE.
                    If k.DistortionKnots Is Nothing Then Return ru
                    Return ru * InterpolateKnots(k.DistortionKnots, ru * k.CornerScale)
                Case Else
                    Return ru
            End Select
        End Function

        ''' <summary>Linear zwischen gleichmaessig von 0 bis 1 verteilten Stuetzwerten; ausserhalb
        ''' gilt der Randwert.</summary>
        Friend Shared Function InterpolateKnots(knots As Double(), position As Double) As Double
            If knots Is Nothing OrElse knots.Length = 0 Then Return 1.0
            If knots.Length = 1 Then Return knots(0)
            Dim p = Math.Max(0.0, Math.Min(1.0, position)) * (knots.Length - 1)
            Dim i = Math.Min(CInt(Math.Floor(p)), knots.Length - 2)
            Dim t = p - i
            Return knots(i) + t * (knots(i + 1) - knots(i))
        End Function

        ''' <summary>Farbquerfehler: der Faktor, mit dem der Rot- bzw. Blaukanal an diesem Radius
        ''' abgetastet werden muss. Rueckgabe 1 heisst "unveraendert".</summary>
        Public Shared Function ChromaticAberrationFactor(k As Korrektur, ru As Double, rot As Boolean) As Double
            If ru <= 0.0 Then Return 1.0
            Dim knots = If(rot, k.TcaRedKnots, k.TcaBlueKnots)
            Dim f As Double
            If knots IsNot Nothing Then
                f = InterpolateKnots(knots, ru * k.CornerScale)
            Else
                Dim b = If(rot, k.TcaBr, k.TcaBb)
                Dim c = If(rot, k.TcaCr, k.TcaCb)
                Dim v = If(rot, k.TcaVr, k.TcaVb)
                f = b * ru * ru + c * ru + v
            End If
            Dim fine = If(rot, k.TcaRedFine, k.TcaBlueFine)
            Return (1.0 + (f - 1.0) * k.ChromaticAberrationStrength) * fine
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
        Friend Shared Function NormalizedForName(s As String) As String
            If String.IsNullOrWhiteSpace(s) Then Return ""
            ' MetadataExtractor formatiert die Blende mit der Prozesskultur. Bei deutscher
            ' Oberfläche wird aus "f/1.8" daher "f/1,8", während Lensfun stets den Punkt
            ' führt. Vor dem Zerlegen angleichen, sonst gehen die gewichtigen Blenden-Token
            ' auseinander und ein ansonsten eindeutiges Objektiv fällt unter die Matchschwelle.
            Dim t = Regex.Replace(s.ToLowerInvariant().Replace(","c, "."c), "[^a-z0-9\.]+", " ")
            ' Kamera-MakerNotes nennen etwa "f/1.8", die Lensfun-Datenbank dagegen
            ' "f/1.8G". Ziffer und nachgestellter Buchstabe gehören für den Namensabgleich
            ' nicht zu einem untrennbaren Wort; getrennt bleiben Brennweite/Lichtstärke auch
            ' ohne einen vollständigen Handelsnamen vergleichbar. Dasselbe gilt umgekehrt
            ' für EXIF-Namen ohne Wortgrenze wie "EF50mm" oder "F1.4".
            t = Regex.Replace(t, "(?<=\d)(?=[a-z])", " ")
            t = Regex.Replace(t, "(?<=[a-z])(?=\d)", " ")
            ' Olympus schreibt die Micro-Four-Thirds-Reihe in manchen MakerNotes als
            ' "M.12mm" statt "M.Zuiko … 12mm". Der Punkt trennt dort Marke und
            ' Brennweite, ist also keine Dezimalstelle.
            t = Regex.Replace(t, "(?<=[a-z])\.(?=\d)", " ")
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

        ''' <summary>Objektivhersteller, die KEINE Kameras zu diesem Bajonett bauen und deren Namen
        ''' die Kamera deshalb ins EXIF schreibt, wenn sie sie liest.</summary>
        Private Shared ReadOnly _fremdhersteller As String() =
            {"sigma", "tamron", "tokina", "samyang", "voigtlander", "zeiss", "laowa",
             "meike", "viltrox", "yongnuo", "irix", "lensbaby"}

        ''' <summary>Sperrt einen Kandidaten, der den Namen eines FREMDEN Objektivherstellers traegt,
        ''' den der gesuchte Name nicht nennt.
        '''
        ''' Der Grund ist gemessen: Bestandteile mit Ziffern zaehlen dreifach, und damit reichen
        ''' gleiche Brennweite und Lichtstaerke aus, um ueber die Schwelle zu kommen. Ein Sony
        ''' "FE 24-70mm F2.8 GM" gewann so an einer Canon gegen "Sigma 24-70mm F2.8 DG OS HSM" -
        ''' ein anderes Objektiv, dessen Kennlinie das Bild sichtbar verbogen haette. Der Anschluss
        ''' faengt das nicht ab, weil dieses Sigma per Adapter zulaessig IST.
        '''
        ''' Traegt die Kamera denselben Namen (ein Sigma-Objektiv an einer Sigma-Kamera), greift die
        ''' Sperre nicht. Gemessen ueber vier Kameras und 1476 Kandidaten kostet sie KEINEN einzigen
        ''' richtigen Treffer; sie nimmt nur falsche weg.</summary>
        Private Shared Function FremdherstellerPasst(gesucht As String, kandidat As String,
                                                     camera As CameraEntry) As Boolean
            Dim tk = NormalizedForName(kandidat).Split(" "c)
            If tk.Length = 0 Then Return True
            Dim tg = NormalizedForName(gesucht).Split(" "c)
            Dim kameraMarke = NormalizedForName(If(camera Is Nothing, "", camera.Maker))
            For Each marke In _fremdhersteller
                If Not tk.Contains(marke) Then Continue For
                If marke = kameraMarke Then Continue For
                If Not tg.Contains(marke) Then Return False
            Next
            Return True
        End Function

        ''' <summary>Ein Nikon-MakerNote-Lens-Eintrag nennt oft nur Brennweite und Blende
        ''' (etwa „35mm f/1.8“), nicht aber die Marke. Die Lensfun-Mount-Kompatibilitaet ist fuer
        ''' adaptierte Objektive bewusst weit und liesse damit zum Beispiel ein Beroflex-T2-Profil
        ''' an einer Nikon zu. Ohne eine Fremdmarke in der Aufnahmeangabe darf daher nur ein Profil
        ''' des Kameraherstellers gewinnen. Nennt die Aufnahme Sigma, Tamron usw., bleibt deren
        ''' Abgleich wie bisher zulaessig.</summary>
        Private Shared Function MakerMatchesForIncompleteName(gesucht As String,
                                                                kandidat As LensEntry,
                                                                camera As CameraEntry) As Boolean
            If kandidat Is Nothing OrElse camera Is Nothing Then Return True
            Dim gesuchtTokens = New HashSet(Of String)(NormalizedForName(gesucht).Split(" "c), StringComparer.OrdinalIgnoreCase)
            Dim kameraTokens = NormalizedForName(camera.Maker).Split(" "c).Where(Function(token) token.Length > 0).ToArray()
            If kameraTokens.Length = 0 Then Return True
            If kameraTokens.Any(Function(token) gesuchtTokens.Contains(token)) Then Return True
            If _fremdhersteller.Any(Function(marke) gesuchtTokens.Contains(marke)) Then Return True

            Dim kandidatMaker = NormalizedForName(kandidat.Maker).Split(" "c)
            Return kameraTokens.Any(Function(token) kandidatMaker.Contains(token))
        End Function

        ''' <summary>Namensbestandteile, an denen ein Fremdobjektiv auch OHNE Herstellernamen zu
        ''' erkennen ist. Canon- und Sony-Gehaeuse schreiben bei Sigma-Glas nur die Bezeichnung des
        ''' Objektivs ins EXIF ("24-70mm F2.8 DG OS HSM | Art 017"), Fujifilm ebenso bei Tamron
        ''' ("17-70mm F/2.8 DiIII-A VC RXD B070X"). Ohne den Hersteller liessen die beiden Sperren
        ''' oben nur Profile des Kameraherstellers zu, und gleiche Brennweite und Lichtstaerke
        ''' hoben dann ein Canon EF 24-70mm f/2.8L ueber die Schwelle - eine fremde Kennlinie.
        '''
        ''' Aufgenommen ist nur, was in der Sammlung AUSSCHLIESSLICH beim jeweiligen Hersteller
        ''' vorkommt (nachgezaehlt ueber alle Namen). "DC" fehlt deshalb (Nikons DC-Nikkor, Canon,
        ''' Pentax), "DG" steht drin, gilt aber nicht neben "Leica" (Panasonics Leica DG).</summary>
        Private Shared ReadOnly _makerSignatures As (Maker As String, Tokens As String())() = {
            ("sigma", {"hsm", "dn", "dg", "art", "contemporary", "sports"}),
            ("tamron", {"di", "diii", "diiii", "vc", "usd", "vxd", "rxd", "pzd"})}

        ''' <summary>Namen von Marken, die neben einem Signaturmerkmal stehen duerfen, ohne dass es
        ''' etwas ueber den Hersteller sagt: wer sie nennt, hat den Hersteller schon genannt.</summary>
        Private Shared ReadOnly _brandTokens As String() =
            {"canon", "nikon", "nikkor", "sony", "fujifilm", "fujinon", "olympus", "zuiko", "om",
             "panasonic", "lumix", "leica", "pentax", "smc", "samsung", "hasselblad", "ricoh"}

        ''' <summary>Der Suchname mit vorangestelltem Hersteller, wenn die Aufnahme keinen nennt,
        ''' das Objektiv ihn aber an seiner Bezeichnung verraet. Sonst unveraendert.</summary>
        Private Shared Function WithInferredMaker(name As String) As String
            Dim tokens = NormalizedForName(name).Split(" "c)
            If tokens.Any(Function(t) _fremdhersteller.Contains(t) OrElse _brandTokens.Contains(t)) Then Return name
            For Each signature In _makerSignatures
                If signature.Tokens.Any(Function(t) tokens.Contains(t)) Then Return signature.Maker & " " & name
            Next
            Return name
        End Function

        ''' <summary>Nennt der Suchname einen fremden Objektivhersteller, kommen NUR dessen Profile in
        ''' Frage. Die Gegenrichtung von <see cref="FremdherstellerPasst"/>: dort darf ein Sigma-
        ''' Profil nicht ein Canon-Objektiv bedienen, hier ein Canon-Profil kein Sigma-Objektiv. Ist
        ''' das Sigma nicht in der Sammlung, gewann sonst das Canon mit gleicher Brennweite und
        ''' Lichtstaerke. Der Hersteller zaehlt, wenn er im Herstellerfeld ODER im Namen steht: die
        ''' neueren Sigma-Eintraege fuehren ihn nur im Herstellerfeld.</summary>
        Private Shared Function NamedMakerMatches(gesucht As String, kandidat As LensEntry,
                                                  camera As CameraEntry) As Boolean
            Dim tg = NormalizedForName(gesucht).Split(" "c)
            Dim kameraMarke = NormalizedForName(If(camera Is Nothing, "", camera.Maker))
            For Each marke In _fremdhersteller
                If Not tg.Contains(marke) OrElse marke = kameraMarke Then Continue For
                If NormalizedForName(kandidat.Maker).Split(" "c).Contains(marke) Then Return True
                Return kandidat.Namen.Any(Function(n) NormalizedForName(n).Split(" "c).Contains(marke))
            Next
            Return True
        End Function

        ''' <summary>Panasonic baut unter zwei Namen: Lumix und Leica DG. Gleiche Brennweite und
        ''' Lichtstaerke heissen dort nicht dasselbe Objektiv, das "LEICA DG 12-35/F2.8" ist eine
        ''' andere Rechnung als das "Lumix G X Vario 12-35mm f/2.8" und fand dessen Profil. Nennt
        ''' der Suchname Leica, muss der Kandidat es auch tun.</summary>
        Private Shared Function LeicaLineMatches(searchName As String, candidate As LensEntry) As Boolean
            If Not NormalizedForName(searchName).Split(" "c).Contains("leica") Then Return True
            If NormalizedForName(candidate.Maker).Split(" "c).Contains("leica") Then Return True
            Return candidate.Namen.Any(Function(n) NormalizedForName(n).Split(" "c).Contains("leica"))
        End Function

        ''' <summary>Die Brennweite aus einem Objektivnamen: (10, 20) fuer "10-20mm", (50, 50) fuer
        ''' "EF50mm". Panasonic laesst das "mm" weg und haengt die Blende mit Schraegstrich an
        ''' ("LEICA DG 12-35/F2.8"); auch das zaehlt. Nothing, wenn der Name keine Brennweite traegt.</summary>
        Private Shared Function FocalRange(name As String) As (Low As Double, High As Double)?
            If String.IsNullOrWhiteSpace(name) Then Return Nothing
            Dim m = Regex.Match(name.Replace(","c, "."c), "(\d+(?:\.\d+)?)(?:\s*-\s*(\d+(?:\.\d+)?))?\s*(?:mm|(?=/\s*f\s*\d))", RegexOptions.IgnoreCase)
            If Not m.Success Then Return Nothing
            Dim low = Double.Parse(m.Groups(1).Value, CultureInfo.InvariantCulture)
            Dim high = If(m.Groups(2).Success, Double.Parse(m.Groups(2).Value, CultureInfo.InvariantCulture), low)
            Return (low, high)
        End Function

        ''' <summary>Tragen beide Namen eine Brennweite, muss sie uebereinstimmen. Die Aehnlichkeit
        ''' allein sieht das nicht: aus "10-20mm f/3.5" passen 20, mm, f und 3.5 auf ein
        ''' "Nikkor 20mm f/3.5" und heben die Festbrennweite ueber die Schwelle, ebenso ein
        ''' "28-70mm f/2.8" auf ein "Nikkor 28mm f/2.8" oder ein "18-200mm" auf ein "18-300mm".
        ''' Ein Zoom ist nie eine Festbrennweite, und eine andere Brennweite nie dasselbe Objektiv.</summary>
        Private Shared Function FocalRangeMatches(gesucht As (Low As Double, High As Double)?, kandidat As String) As Boolean
            If Not gesucht.HasValue Then Return True
            Dim k = FocalRange(kandidat)
            If Not k.HasValue Then Return True
            Return Math.Abs(gesucht.Value.Low - k.Value.Low) < 0.6 AndAlso
                   Math.Abs(gesucht.Value.High - k.Value.High) < 0.6
        End Function

        ''' <summary>Die Lichtstaerke aus einem Objektivnamen: (3.5, 5.6) fuer "f/3.5-5.6", (2.8, 0)
        ''' fuer "F2.8" oder "1:2.8". Das F darf direkt an "mm" haengen ("XF23mmF2.8"), sonst nicht
        ''' an einem Buchstaben, damit "AF" oder "DF" nicht mitzaehlen.</summary>
        Private Shared Function ApertureRange(name As String) As (Wide As Double, Narrow As Double)?
            If String.IsNullOrWhiteSpace(name) Then Return Nothing
            Dim m = Regex.Match(name.Replace(","c, "."c),
                                "(?:(?:(?<![a-z])|(?<=mm))f\s*/?\s*|1\s*:\s*)(\d+(?:\.\d+)?)(?:\s*-\s*(\d+(?:\.\d+)?))?",
                                RegexOptions.IgnoreCase)
            If Not m.Success Then Return Nothing
            Dim wide = Double.Parse(m.Groups(1).Value, CultureInfo.InvariantCulture)
            Dim narrow = If(m.Groups(2).Success, Double.Parse(m.Groups(2).Value, CultureInfo.InvariantCulture), 0.0)
            Return (wide, narrow)
        End Function

        ''' <summary>Wie <see cref="FocalRangeMatches"/> fuer die Lichtstaerke. Die Brennweite
        ''' allein trennt zwei Objektive derselben Reihe nicht: ein "FE 28-70mm F2 GM" fand das
        ''' "FE 28-70mm f/3.5-5.6 OSS", ein "XF23mmF2.8" das "XF23mmF2". Die groesste Oeffnung muss
        ''' stimmen; die kleinste nur, wenn beide Namen eine nennen.</summary>
        Private Shared Function ApertureMatches(gesucht As (Wide As Double, Narrow As Double)?, kandidat As String) As Boolean
            If Not gesucht.HasValue Then Return True
            Dim k = ApertureRange(kandidat)
            If Not k.HasValue Then Return True
            If Math.Abs(gesucht.Value.Wide - k.Value.Wide) > 0.05 Then Return False
            If gesucht.Value.Narrow > 0 AndAlso k.Value.Narrow > 0 AndAlso
               Math.Abs(gesucht.Value.Narrow - k.Value.Narrow) > 0.05 Then Return False
            Return True
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
                    AppSettingsService.Update(Sub(s)
                                                  s.LensAssignments = assignments.Select(Function(p) New LensAssignment With {
                                                      .ExifName = p.Key, .Modell = p.Value}).ToList()
                                              End Sub)
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
        ''' Das Ergebnis wird je Datei gemerkt: der Abgleich laeuft ueber 1500 Objektive und wird
        ''' beim Blaettern durch einen Ordner sonst fuer jedes Bild neu gerechnet.</summary>
        Public Shared Function FindCorrectionForFile(path As String,
                                                       Optional modelOverride As String = "") As Korrektur
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            ' Die Vorgabe gehoert in den Schluessel: sonst liefert der Zwischenspeicher das Ergebnis
            ' der vorigen Wahl zurueck, und die Auswahl saehe wirkungslos aus.
            Dim key = If(String.IsNullOrWhiteSpace(modelOverride), path, path & "|" & modelOverride)
            Dim cached As Korrektur = Nothing
            SyncLock _dateiCacheLock
                ' KOPIE herausgeben: der Aufrufer streicht daran die fuer dieses Bild
                ' abgeschalteten Korrekturen weg (siehe Filtere). Am gemerkten Objekt getan, waere
                ' die Abschaltung des einen Bildes fuer alle weiteren mit demselben Objektiv gueltig.
                If _dateiCache.TryGetValue(key, cached) Then Return CloneEntry(cached)
            End SyncLock

            Dim result As Korrektur = Nothing
            Try
                Dim directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                ' NICHT das erste Verzeichnis nehmen, sondern das erste mit einem belegten Wert.
                ' RAW-Container (ARW/DNG/NEF) zeigen ueber den TIFF-Eintrag "SubIFDs" auf ihre
                ' Vorschaubilder, und jedes davon wird ebenfalls ein "Exif SubIFD". Da TIFF die
                ' Eintraege aufsteigend nach Nummer ablegt, stehen diese Vorschau-Verzeichnisse IMMER
                ' vor dem eigentlichen Aufnahme-Verzeichnis. Das erste zu nehmen liefert deshalb
                ' zuverlaessig leere Werte: das Objektiv stand in der Anzeige, die Korrektur fand
                ' aber weder Namen noch Brennweite noch Blende und blieb wirkungslos.
                ' Fuer Hersteller und Modell gilt dasselbe, sobald eine Datei mehrere EXIF-Bloecke
                ' mitbringt. Ohne sie faellt in FindCorrection der Anschlussfilter weg, und dann
                ' gewinnt leicht die falsche Bauform eines aehnlich benannten Objektivs.
                Dim maker = ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake)
                Dim model = ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel)
                Dim lens = ExifService.GetLensDescription(directories)
                Dim focalLength = FirstNumber(ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifSubIfdDirectory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFocalLength))
                Dim aperture = FirstNumber(ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifSubIfdDirectory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFNumber))
                Dim focusDistance = ExifService.GetFocusDistanceMeters(directories)
                Dim width = 0, height = 0
                For Each d In directories
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
                Dim assigned = ZuordnungFuer(lens)
                Dim searchName = lens
                If Not String.IsNullOrWhiteSpace(assigned) Then searchName = assigned
                If Not String.IsNullOrWhiteSpace(modelOverride) Then searchName = modelOverride
                ' Stammt der Name aus den Aufnahmedaten (keine Vorgabe, keine Zuordnung), duerfen das
                ' feste Objektiv einer Kompaktkamera und die Korrekturwerte der Kamera einspringen.
                ' Hat der Nutzer ein Objektiv genannt, gilt nur dieses: findet es sich nicht, sagt
                ' die Statuszeile "keine Messwerte vorhanden", statt still ein anderes zu nehmen.
                Dim fromCaptureData = String.IsNullOrWhiteSpace(modelOverride) AndAlso String.IsNullOrWhiteSpace(assigned)
                ' Ohne Aufnahmedaten fehlen auch die Bildmasse im EXIF. Die stehen aber in der
                ' DATEI - genau bei diesen Bildern wird die Zuordnung von Hand gebraucht, und ohne
                ' Masse laesst sich der Radius nicht normieren.
                If width <= 1 OrElse height <= 1 Then
                    Dim fromFile = ExifService.ReadImageDimensions(path)
                    width = fromFile.Width.GetValueOrDefault()
                    height = fromFile.Height.GetValueOrDefault()
                End If
                If width > 1 AndAlso height > 1 Then
                    result = FindCorrection(maker, model, searchName, focalLength, aperture, width, height,
                                            focusDistance, fromCaptureData)
                    ' Kein Profil in der Sammlung: dann die Korrekturwerte, die die Kamera selbst in
                    ' die RAW schreibt, nach derselben Regel.
                    If result Is Nothing AndAlso fromCaptureData Then
                        result = CameraLensCorrectionService.TryCreate(directories, width, height)
                        If result IsNot Nothing Then
                            result.LensName = If(String.IsNullOrWhiteSpace(lens), model, lens)
                            result.Brennweite = focalLength
                            result.Aperture = aperture
                        End If
                    End If
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
                .FocusDistance = k.FocusDistance, .Source = k.Source,
                .DistortionKnots = k.DistortionKnots, .TcaRedKnots = k.TcaRedKnots, .TcaBlueKnots = k.TcaBlueKnots,
                .NormScale = k.NormScale, .CornerScale = k.CornerScale,
                .CalibrationAspectRatio = k.CalibrationAspectRatio, .CropRatio = k.CropRatio,
                .HasDistortion = k.HasDistortion, .DistortionModel = k.DistortionModel,
                .Va = k.Va, .Vb = k.Vb, .Vc = k.Vc,
                .HasChromaticAberration = k.HasChromaticAberration,
                .TcaBr = k.TcaBr, .TcaCr = k.TcaCr, .TcaVr = k.TcaVr,
                .TcaBb = k.TcaBb, .TcaCb = k.TcaCb, .TcaVb = k.TcaVb,
                .TcaRedFine = k.TcaRedFine, .TcaBlueFine = k.TcaBlueFine,
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
            ''' Feinkorrektur des Farbquerfehlers je Kanal, -100 bis 100 (ImageAdjustments.LensTcaRed
            ''' und LensTcaBlue). Wirkt zusaetzlich zum Profil und auch ohne eines.
            Public Property ChromaticAberrationRed As Double
            Public Property ChromaticAberrationBlue As Double
            ''' Den Sensorrand abschneiden (ImageAdjustments.RawSensorEdgeCrop). Haengt an der
            ''' Wahl, weil sie schon durch jeden Decode-Weg gereicht wird; mit dem Objektiv hat
            ''' er sonst nichts zu tun. Nothing als Wahl heisst: kein Beschnitt.
            Public Property CropSensorEdge As Boolean
        End Class

        ''' <summary>Der Faktor einer Feinkorrektur: 100 auf dem Regler heisst, der Kanal wird bei
        ''' r * 1,002 abgetastet. Die Spanne deckt, was im Bestand gemessen wurde (bis rund 0,0015
        ''' ohne Profil), mit Luft nach oben; fein genug ist sie, weil ein Schritt am Rand eines
        ''' 24-Megapixel-Bildes eine Zehntelpixel-Verschiebung ist.</summary>
        Public Shared Function FineChromaticAberrationFactor(sliderValue As Double) As Double
            Return 1.0 + Math.Max(-100.0, Math.Min(100.0, sliderValue)) * 0.00002
        End Function

        Private Shared Function Gilt(feld As Boolean?, vorgabe As Boolean) As Boolean
            Return If(feld.HasValue, feld.Value, vorgabe)
        End Function

        ''' <summary>Streicht aus einem Ergebnis, was fuer dieses Bild abgeschaltet ist. Rueckgabe
        ''' Nothing, wenn danach nichts mehr uebrig ist - dann laeuft im Decode auch keine der
        ''' teuren Schleifen an.</summary>
        Public Shared Function Filtere(k As Korrektur, wahl As Wahl, vorgabe As Boolean) As Korrektur
            ' Die Feinkorrektur von Hand braucht kein Profil: ohne gefundenes Objektiv entsteht
            ' eine Korrektur, die nur sie traegt.
            Dim redFine = FineChromaticAberrationFactor(If(wahl Is Nothing, 0.0, wahl.ChromaticAberrationRed))
            Dim blueFine = FineChromaticAberrationFactor(If(wahl Is Nothing, 0.0, wahl.ChromaticAberrationBlue))
            Dim hasFine = redFine <> 1.0 OrElse blueFine <> 1.0
            If k Is Nothing Then
                If Not hasFine Then Return Nothing
                k = New Korrektur()
            End If
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
            ' Ist das Profil fuer den Farbquerfehler aus (oder gibt es keines), traegt die Stufe
            ' allein die Feinkorrektur: die Profilwerte werden neutral, damit sie nicht doch wirken.
            If hasFine AndAlso Not k.HasChromaticAberration Then
                k.TcaBr = 0 : k.TcaCr = 0 : k.TcaVr = 1.0
                k.TcaBb = 0 : k.TcaCb = 0 : k.TcaVb = 1.0
                k.TcaRedKnots = Nothing : k.TcaBlueKnots = Nothing
                k.ChromaticAberrationStrength = 1.0
                k.HasChromaticAberration = True
            End If
            k.TcaRedFine = redFine
            k.TcaBlueFine = blueFine
            Return If(k.HasAnything, k, Nothing)
        End Function

        ''' <summary>Sucht die Kennlinien fuer eine konkrete Aufnahme. Rueckgabe Nothing, wenn
        ''' Objektiv oder Brennweite unbekannt sind.
        '''
        ''' <para><paramref name="allowFixedLens"/>: findet der Name kein Profil, darf das feste
        ''' Objektiv einer Kompaktkamera einspringen. Nur wenn der Name aus den Aufnahmedaten
        ''' stammt; bei einer Wahl im Rezept oder einer Zuordnung von Hand hat der Nutzer ein
        ''' bestimmtes Objektiv genannt, und findet sich das nicht, soll die Statuszeile das sagen,
        ''' statt still das Kameraprofil zu nehmen (dieselbe Regel wie beim Rueckfall auf die
        ''' Korrekturwerte der Kamera).</para></summary>
        Public Shared Function FindCorrection(cameraMaker As String, cameraModel As String,
                                              lensName As String,
                                              focalLengthMm As Double, aperture As Double,
                                              width As Integer, height As Integer,
                                              Optional focusDistanceM As Double = 0,
                                              Optional allowFixedLens As Boolean = True) As Korrektur
            If width < 2 OrElse height < 2 Then Return Nothing
            LoadOnce()
            If _objektive.Count = 0 Then Return Nothing

            ' Erst die Kamera, dann das Objektiv: der Anschluss der Kamera ist der schaerfste
            ' Filter, den wir haben. Ohne ihn gewinnt bei aehnlich benannten Reihen leicht die
            ' FALSCHE Bauform - gemessen am Referenzfoto wurde die spiegellose Fassung eines
            ' Objektivs gefunden, das in Wahrheit an einer Spiegelreflex-Fassung sass. Deren
            ' Kennlinie haette das Bild sichtbar verbogen.
            Dim camera = BestCamera(cameraMaker, cameraModel)
            Dim obj = If(String.IsNullOrWhiteSpace(lensName), Nothing, BestLens(lensName, camera))
            Dim source = CorrectionSource.Profile
            If obj Is Nothing AndAlso allowFixedLens Then
                Dim fixedLens = FindFixedLens(cameraMaker, cameraModel)
                obj = fixedLens.Lens
                If fixedLens.Camera IsNot Nothing Then camera = fixedLens.Camera
                source = CorrectionSource.FixedLens
            End If
            If obj Is Nothing Then Return Nothing

            ' Ohne Brennweite laesst sich normalerweise kein Kalibrierpunkt waehlen. Bei einer
            ' FESTBRENNWEITE gibt es aber nur einen - dann ist die Angabe entbehrlich. Das ist genau
            ' der Fall, der bei Bildern ohne Aufnahmedaten weiterhilft: wer sein Objektiv von Hand
            ' zuordnet, hat oft auch sonst nichts im EXIF stehen.
            If focalLengthMm <= 0 Then
                focalLengthMm = EinzigeBrennweite(obj)
                If focalLengthMm <= 0 Then Return Nothing
            End If

            ' Der Crop-Faktor der KAMERA, nicht des Objektivs: die Kennlinien sind an einem
            ' bestimmten Sensor gemessen worden, und ein anderer Sensor sieht einen anderen
            ' Ausschnitt desselben Bildkreises.
            Dim cameraCrop = If(camera IsNot Nothing, camera.CropFactor, obj.CropFactor)

            Dim k = New Korrektur With {
                .LensName = obj.Modell,
                .Brennweite = focalLengthMm,
                .Aperture = aperture,
                .FocusDistance = focusDistanceM,
                .Source = source
            }
            ComputeNormalization(k, obj, cameraCrop, width, height)
            ApplyDistortion(k, obj, focalLengthMm)
            ApplyChromaticAberration(k, obj, focalLengthMm)
            ApplyVignetting(k, obj, focalLengthMm, aperture, focusDistanceM)
            Return If(k.HasAnything, k, Nothing)
        End Function

        ''' <summary>Löst nur den Namen des passenden Objektivprofils auf. Anders als
        ''' <see cref="FindCorrection"/> verlangt diese Abfrage keine Bildmasse, Brennweite oder
        ''' vorhandene Kennlinie: Die Oberfläche kann damit auch bei einer Aufnahme ohne nutzbare
        ''' Messwerte sagen, WELCHES Objektiv erkannt wurde. <paramref name="allowFixedLens"/>
        ''' wie bei <see cref="FindCorrection"/>.</summary>
        Public Shared Function ResolveLensName(cameraMaker As String, cameraModel As String,
                                               lensName As String,
                                               Optional allowFixedLens As Boolean = True) As String
            LoadOnce()
            If _objektive.Count = 0 Then Return ""
            Dim obj = If(String.IsNullOrWhiteSpace(lensName), Nothing,
                         BestLens(lensName, BestCamera(cameraMaker, cameraModel)))
            If obj Is Nothing AndAlso allowFixedLens Then obj = FindFixedLens(cameraMaker, cameraModel).Lens
            Return If(obj?.Modell, "")
        End Function

        ''' <summary>Dateivariante von <see cref="ResolveLensName"/>. Sie folgt derselben
        ''' Priorität wie die Korrektur: Rezeptvorgabe, dauerhafte Zuordnung, EXIF.</summary>
        Public Shared Function ResolveLensNameForFile(path As String, Optional modelOverride As String = "") As String
            If String.IsNullOrWhiteSpace(path) Then Return ""
            Try
                Dim directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                Dim maker = ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake)
                Dim model = ExifService.GetTagDescAcross(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)(
                    directories, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel)
                Dim lens = ExifService.GetLensDescription(directories)
                Dim assigned = ZuordnungFuer(lens)
                Dim searchName = If(String.IsNullOrWhiteSpace(modelOverride), assigned, modelOverride)
                If String.IsNullOrWhiteSpace(searchName) Then searchName = lens
                Dim fromCaptureData = String.IsNullOrWhiteSpace(modelOverride) AndAlso String.IsNullOrWhiteSpace(assigned)
                Return ResolveLensName(maker, model, searchName, fromCaptureData)
            Catch
                Return ""
            End Try
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

        Private Shared Function BestLens(lensName As String, camera As CameraEntry) As LensEntry
            Dim best As LensEntry = Nothing
            Dim bestScore As Double = 0
            Dim bestCropDistance As Double = Double.MaxValue
            Dim bestExtent As Integer = -1
            Dim cameraCrop = If(camera IsNot Nothing, camera.CropFactor, 0.0)
            Dim searchName = WithInferredMaker(lensName)
            Dim searchFocal = FocalRange(searchName)
            Dim searchAperture = ApertureRange(searchName)

            For Each o In _objektive
                If Not PasstAnschluss(o, camera) Then Continue For
                If Not MakerMatchesForIncompleteName(searchName, o, camera) Then Continue For
                If Not NamedMakerMatches(searchName, o, camera) Then Continue For
                If Not LeicaLineMatches(searchName, o) Then Continue For
                Dim g As Double = 0
                For Each n In o.Namen
                    If Not FremdherstellerPasst(searchName, n, camera) Then Continue For
                    If Not FocalRangeMatches(searchFocal, n) Then Continue For
                    If Not ApertureMatches(searchAperture, n) Then Continue For
                    g = Math.Max(g, Similarity(searchName, n))
                Next
                If g <= 0 Then Continue For

                Dim cropDistance = If(cameraCrop > 0, Math.Abs(o.CropFactor - cameraCrop), 0.0)
                Dim extent = o.Distortion.Count + o.ChromaticAberration.Count + o.Vignetting.Count

                Dim isBetter = False
                If g > bestScore + 0.0001 Then
                    isBetter = True
                ElseIf Math.Abs(g - bestScore) <= 0.0001 Then
                    If cropDistance < bestCropDistance - 0.01 Then
                        isBetter = True
                    ElseIf Math.Abs(cropDistance - bestCropDistance) <= 0.01 AndAlso extent > bestExtent Then
                        isBetter = True
                    End If
                End If

                If isBetter Then
                    bestScore = g
                    bestCropDistance = cropDistance
                    bestExtent = extent
                    best = o
                End If
            Next
            Return If(bestScore >= MatchThreshold, best, Nothing)
        End Function

        Private Shared Function BestCamera(maker As String, model As String) As CameraEntry
            If String.IsNullOrWhiteSpace(model) Then Return Nothing
            maker = CollectionMaker(maker, model)
            Dim best As CameraEntry = Nothing
            Dim bestScore As Double = 0
            For Each c In _kameras
                Dim g = Similarity(model, c.Modell)
                If Not String.IsNullOrWhiteSpace(maker) AndAlso
                   Not String.IsNullOrWhiteSpace(c.Maker) Then
                    ' Der Hersteller ist ein starker Filter: "5D" gibt es bei mehreren Marken.
                    If Similarity(maker, c.Maker) < 0.5 Then Continue For
                End If
                If g > bestScore Then
                    bestScore = g
                    best = c
                End If
            Next
            Return If(bestScore >= MatchThreshold, best, Nothing)
        End Function

        ''' <summary>Der Hersteller, unter dem die Sammlung die Kamera fuehrt. Pentax-Gehaeuse ab
        ''' etwa 2013 schreiben "RICOH IMAGING COMPANY, LTD." in die Aufnahmedaten und "PENTAX K-3"
        ''' als Modell; die Sammlung fuehrt sie unter "Pentax". Mit Ricoh als Hersteller warf der
        ''' Filter in <see cref="BestCamera"/> die richtige Kamera hinaus, und das Objektiv fand
        ''' kein Profil.</summary>
        Private Shared Function CollectionMaker(maker As String, model As String) As String
            If If(model, "").TrimStart().StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) Then Return "Pentax"
            Return maker
        End Function

        ''' <summary>Das fest eingebaute Objektiv einer Kompaktkamera. Die Sammlung fuehrt es unter
        ''' einem Anschluss, den nur diese Kamera und baugleiche tragen ("panasonicDMCLX100"), mit
        ''' dem Namen "festes Objektiv"; die Aufnahmedaten nennen dafuer meist keinen
        ''' Objektivnamen. Gefunden wird es deshalb ueber die Kamera, und zwar nur bei GENAU
        ''' gleichem Modellnamen: ein unscharfer Treffer wie beim Wechselobjektiv ("RX100M7" auf
        ''' "RX100M3") braechte hier das Objektiv eines anderen Gehaeuses. Fuehrt der Anschluss
        ''' mehr als ein Objektiv, ist es kein festes, und es bleibt bei Nothing.</summary>
        Private Shared Function FindFixedLens(maker As String, model As String) As (Lens As LensEntry, Camera As CameraEntry)
            If String.IsNullOrWhiteSpace(model) Then Return (Nothing, Nothing)
            maker = CollectionMaker(maker, model)
            Dim key = CameraModelKey(maker, model)
            If key.Length = 0 Then Return (Nothing, Nothing)
            Dim cameras = _kameras.Where(Function(c) CameraModelKey(c.Maker, c.Modell) = key AndAlso
                                                     (String.IsNullOrWhiteSpace(maker) OrElse
                                                      String.IsNullOrWhiteSpace(c.Maker) OrElse
                                                      Similarity(maker, c.Maker) >= 0.5)).ToList()
            If cameras.Count = 0 Then Return (Nothing, Nothing)

            Dim mounts = New HashSet(Of String)(cameras.SelectMany(Function(c) c.Anschluesse), StringComparer.OrdinalIgnoreCase)
            Dim lenses = _objektive.Where(Function(o) o.Anschluesse.Any(Function(a) mounts.Contains(a))).ToList()
            If lenses.Count = 0 Then Return (Nothing, Nothing)
            If lenses.Select(Function(o) NormalizedForName(o.Modell)).Distinct().Count() <> 1 Then Return (Nothing, Nothing)

            ' Dasselbe Objektiv kann mehrfach stehen, je Seitenverhaeltnis gemessen: das mit den
            ' meisten Messwerten, und dazu der Kameraeintrag mit dem naechsten Crop-Faktor.
            Dim lens = lenses.OrderByDescending(Function(o) o.Distortion.Count + o.ChromaticAberration.Count + o.Vignetting.Count).First()
            Dim camera = cameras.OrderBy(Function(c) Math.Abs(c.CropFactor - lens.CropFactor)).First()
            Return (lens, camera)
        End Function

        ''' <summary>Der Profilname fuer die Anzeige. Die Sammlung haengt an das feste Objektiv
        ''' einer Kompaktkamera Verwaltungszusaetze: "& compatibles" (baugleiche Gehaeuse) und
        ''' "(Standard)" (im Unterschied zu einer zweiten Messung an anderen Rohdaten). In der
        ''' Statuszeile stehen sie nur im Weg ("Sony RX10 & compatibles"). Zusaetze mit Inhalt,
        ''' etwa ein Telekonverter, bleiben stehen. Gesucht und zugeordnet wird weiter mit dem
        ''' vollen Namen.</summary>
        Public Shared Function DisplayLensName(profileName As String) As String
            If String.IsNullOrWhiteSpace(profileName) Then Return ""
            Dim result = Regex.Replace(profileName, "\s*&\s*compatibles", "", RegexOptions.IgnoreCase)
            result = Regex.Replace(result, "\s*\(Standard\)", "", RegexOptions.IgnoreCase)
            Return result.Trim()
        End Function

        ''' <summary>Der Modellname einer Kamera in Vergleichsform, ohne vorangestellten
        ''' Hersteller: die Aufnahmedaten schreiben "Canon PowerShot G7 X", die Sammlung
        ''' "PowerShot G7 X" mit dem Hersteller im eigenen Feld.</summary>
        Private Shared Function CameraModelKey(maker As String, model As String) As String
            Dim normalized = NormalizedForName(model)
            Dim makerToken = NormalizedForName(maker).Split(" "c).FirstOrDefault()
            If Not String.IsNullOrEmpty(makerToken) AndAlso normalized.StartsWith(makerToken & " ", StringComparison.Ordinal) Then
                normalized = normalized.Substring(makerToken.Length + 1)
            End If
            Return normalized
        End Function

        ''' <summary>Legt die beiden Radien fest: r = 1 in der Mitte der LANGEN Kante fuer
        ''' Verzeichnung und Farbquerfehler, r = 1 in der ECKE fuer die Vignettierung.
        '''
        ''' Die Kalibrierdaten (Seitenverhaeltnis der Messung, Crop-Quotient) bleiben am Ergebnis
        ''' stehen, damit jede Stufe die Umrechnung mit ihren EIGENEN Bildmassen holen kann - siehe
        ''' <see cref="NormScaleFor"/>. Der hier gesetzte NormScale ist nur der Wert fuer die Masse,
        ''' mit denen gesucht wurde.</summary>
        Private Shared Sub ComputeNormalization(k As Korrektur, obj As LensEntry,
                                              cameraCrop As Double, width As Integer, height As Integer)
            k.CalibrationAspectRatio = obj.Seitenverhaeltnis
            k.CropRatio = obj.CropFactor / Math.Max(0.0001, cameraCrop)
            ' Die Vignettierung rechnet mit r = 1 in der ECKE. Im System der Verzeichnung liegt die
            ' Ecke bei Wurzel(Seitenverhaeltnis^2 + 1) - genau darum wird geteilt.
            k.CornerScale = 1.0 / CalibrationDiagonal(k)
            k.NormScale = NormScaleFor(k, width, height)
        End Sub

        Private Shared Function CalibrationDiagonal(k As Korrektur) As Double
            Return Math.Sqrt(k.CalibrationAspectRatio * k.CalibrationAspectRatio + 1.0)
        End Function

        ''' <summary>Die Umrechnung von Pixeln in den normierten Radius fuer GENAU DIESE Bildmasse.
        '''
        ''' Jede Stufe muss sie mit den Massen des Bildes holen, das wirklich vor ihr liegt, statt
        ''' den Wert aus dem Abgleich zu nehmen. Der stammt aus den Aufnahmedaten, und die weichen
        ''' von der Wirklichkeit ab: eine RAF nennt dort ueberhaupt keine Masse, dann kommen sie aus
        ''' der eingebetteten Vorschau (1920 x 1280 statt 6032 x 4028). Der Radius war damit
        ''' dreifach zu gross - die Kennlinien liefen weit hinter ihren Messbereich, die
        ''' Vignettierung schlug ins Negative und die Verzeichnung zog das ganze Bild in eine Kugel.
        ''' Derselbe Fehler steckt in jedem halb aufgeloesten Decode (Kachelbilder), dort um den
        ''' Faktor zwei.
        '''
        ''' Rueckgabe ist der Wert aus dem Abgleich, wenn die Kalibrierdaten fehlen - so bleiben von
        ''' Hand aufgebaute Kennlinien (Pruefstand) bei dem, was der Aufrufer gesetzt hat.</summary>
        Public Shared Function NormScaleFor(k As Korrektur, width As Integer, height As Integer) As Double
            If k Is Nothing Then Return 0.0
            If k.CropRatio <= 0.0 OrElse k.CalibrationAspectRatio <= 0.0 OrElse
               width < 2 OrElse height < 2 Then Return k.NormScale

            Dim w = width - 1
            Dim h = height - 1
            Dim shortSide = CDbl(Math.Min(w, h))
            Dim imageAspectRatio = If(w < h, CDbl(h) / w, CDbl(w) / h)

            ' Der Ausgleich dafuer, dass die Kennlinie an einem anderen Sensor gemessen wurde,
            ' fuehrt ueber die Bilddiagonale - Crop-Faktoren sind genau darueber definiert.
            Dim adjust = CalibrationDiagonal(k) /
                         Math.Sqrt(imageAspectRatio * imageAspectRatio + 1.0) * k.CropRatio
            Return 2.0 / shortSide * adjust
        End Function

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

        ''' <summary>Die Vignettierung haengt an drei Groessen: Entfernung, Blende, Brennweite. Bei
        ''' rund 130 Objektiven der Sammlung unterscheidet sie sich mit der Entfernung spuerbar (im
        ''' Median 13 Prozent in der Ecke zwischen nah und fern). Ist der Fokusabstand bekannt
        ''' (<see cref="ExifService.GetFocusDistanceMeters"/>), wird zwischen den beiden
        ''' gemessenen Entfernungen links und rechts davon gemischt, und zwar in 1/Entfernung:
        ''' zwischen 1 m und unendlich liegt die Haelfte des Weges bei 2 m, nicht bei 500 m.
        ''' Ausserhalb des gemessenen Bereichs gilt die naechste. Ohne Abstand bleibt es bei der
        ''' groessten gemessenen (Unendlich-Einstellung, der Normalfall bei Landschaft und
        ''' Architektur, wo die Randabdunklung ueberhaupt auffaellt).</summary>
        Private Shared Sub ApplyVignetting(k As Korrektur, obj As LensEntry,
                                                  focalLength As Double, aperture As Double,
                                                  focusDistance As Double)
            If obj.Vignetting.Count = 0 Then Return
            Dim farthest = obj.Vignetting.Max(Function(x) x.Distance)
            Dim atFarthest = obj.Vignetting.Where(Function(x) x.Distance = farthest).ToList()
            If atFarthest.Count = 0 Then Return

            ' Ohne Blendenangabe die offenste gemessene nehmen - dort ist die Abdunklung am
            ' staerksten, und eine zu schwache Korrektur ist harmloser als eine zu starke.
            Dim targetAperture = If(aperture > 0, aperture, atFarthest.Min(Function(x) x.Aperture))

            ' Je Brennweite fuer sich: erst ueber DEREN Entfernungen, dann ueber die Blende, zuletzt
            ' ueber die Brennweite mischen. Nicht ueber eine gemeinsame Entfernung: bei Zooms wie
            ' dem RF 100-500 ist jede Brennweite bei ihrer eigenen Naheinstellgrenze gemessen, und
            ' eine Entfernung, die nur bei 100 mm vorkommt, braechte sonst deren Vignettierung in
            ' ein 500-mm-Bild. Es zaehlen nur Brennweiten, die auch bei der groessten Entfernung
            ' gemessen sind; ohne Fokusabstand ist das genau die bisherige Rechnung.
            Dim focalLengths = atFarthest.Select(Function(x) x.Brennweite).Distinct().ToList()
            Dim perFocalLength = focalLengths.
                Select(Function(f) VignettingAtFocusDistance(obj.Vignetting.Where(Function(x) x.Brennweite = f).ToList(),
                                                            targetAperture, focusDistance)).
                Where(Function(v) v IsNot Nothing).ToList()
            Dim u = Surrounding(perFocalLength, focalLength, Function(x) x.Brennweite)
            If u.Unten Is Nothing Then Return
            k.Vk1 = Misch(u.Unten.K1, u.Oben.K1, u.Anteil)
            k.Vk2 = Misch(u.Unten.K2, u.Oben.K2, u.Anteil)
            k.Vk3 = Misch(u.Unten.K3, u.Oben.K3, u.Anteil)
            k.HasVignetting = True
        End Sub

        ''' <summary>Die Vignettierung einer Brennweite beim Fokusabstand: zwischen den beiden
        ''' gemessenen Entfernungen links und rechts davon in 1/Entfernung gemischt, ausserhalb die
        ''' naechste; ohne Abstand die groesste. Je Entfernung zuerst die passende Blende.</summary>
        Private Shared Function VignettingAtFocusDistance(values As List(Of VignettingValue),
                                                          aperture As Double, focusDistance As Double) As VignettingValue
            Dim distances = values.Select(Function(x) x.Distance).Distinct().OrderBy(Function(d) d).ToList()
            If distances.Count = 0 Then Return Nothing
            Dim far = distances.Last()
            Dim near = far
            Dim share = 0.0
            If focusDistance > 0 AndAlso distances.Count > 1 AndAlso distances.First() > 0 Then
                Dim target = Math.Max(distances.First(), Math.Min(far, focusDistance))
                near = distances.Last(Function(d) d <= target)
                far = distances.First(Function(d) d >= target)
                If far > near Then share = (1.0 / near - 1.0 / target) / (1.0 / near - 1.0 / far)
            End If

            Dim atNear = NearestAperture(values.Where(Function(x) x.Distance = near).ToList(), aperture)
            Dim atFar = If(far = near, atNear, NearestAperture(values.Where(Function(x) x.Distance = far).ToList(), aperture))
            If atNear Is Nothing Then Return atFar
            If atFar Is Nothing Then Return atNear
            Return New VignettingValue With {
                .Brennweite = atNear.Brennweite,
                .Aperture = aperture,
                .Distance = If(focusDistance > 0, focusDistance, far),
                .K1 = Misch(atNear.K1, atFar.K1, share),
                .K2 = Misch(atNear.K2, atFar.K2, share),
                .K3 = Misch(atNear.K3, atFar.K3, share)}
        End Function

        Private Shared Function NearestAperture(values As List(Of VignettingValue), aperture As Double) As VignettingValue
            Dim bottom = values.Where(Function(x) x.Aperture <= aperture).OrderByDescending(Function(x) x.Aperture).FirstOrDefault()
            Dim top = values.Where(Function(x) x.Aperture >= aperture).OrderBy(Function(x) x.Aperture).FirstOrDefault()
            If bottom Is Nothing Then Return top
            If top Is Nothing Then Return bottom
            If bottom Is top Then Return bottom
            ' In Blendenstufen mischen, nicht in Blendenzahlen: der Lichtabfall ist logarithmisch,
            ' zwischen 2.8 und 8 liegt linear gemittelt nicht die Haelfte des Effekts.
            Dim l0 = Math.Log(Math.Max(0.1, bottom.Aperture)), l1 = Math.Log(Math.Max(0.1, top.Aperture))
            Dim lz = Math.Log(Math.Max(0.1, aperture))
            Dim share = If(Math.Abs(l1 - l0) < 0.000001, 0.0, (lz - l0) / (l1 - l0))
            Return New VignettingValue With {
                .Brennweite = bottom.Brennweite,
                .Aperture = aperture,
                .Distance = bottom.Distance,
                .K1 = Misch(bottom.K1, top.K1, share),
                .K2 = Misch(bottom.K2, top.K2, share),
                .K3 = Misch(bottom.K3, top.K3, share)}
        End Function

    End Class

End Namespace
