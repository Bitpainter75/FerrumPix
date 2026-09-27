Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Referenzwerte der Grundbelichtung je Kameramodell.
    '''
    ''' WOZU: Unsere Basisstufe traegt eine feste Grundbelichtung (RawDecodeService.BaseExposureEv).
    ''' Adobe hinterlegt den entsprechenden Wert JE KAMERA - unserer ist an genau einer Kamera
    ''' gefittet (EOS R6). Auf den meisten anderen Modellen entwickelt er zu hell, gegen Lightroom im
    ''' Median um 0,7 Blendenstufen. Diese Tabelle gleicht die Modelle UNTEREINANDER an.
    '''
    ''' WIE GEMESSEN (2026-09-27, Diagnostics/Grundbelichtung): Bezug ist Adobes eigene Entwicklung.
    ''' Adobes DNG-Konverter legt eine Vorschau in voller Groesse in die DNG, gerechnet mit Adobes
    ''' Entwicklung in der Grundeinstellung; an der R6 deckt sie sich mit dem echten
    ''' Lightroom-Export ohne Preset. Je Modell wurde die Grundbelichtung ueber diese Tabelle
    ''' verschoben, bis unsere Entwicklung im Mittel von fuenf Perzentilen auf Adobes traf. Der
    ''' Eintrag ist das Mittel aus dieser Messung (ueber alle belastbaren Bilder des Modells) und
    ''' Adobes BaselineExposure. Beide Quellen stimmen eng ueberein (Korrelation 0,94); das Mittel
    ''' trifft das Mittel der uebrigen Bilder eines Modells auf 0,07 EV. Ausgelassen sind Kameras,
    ''' die selbst DNG schreiben, und Fujis SuperCCD und EXR. Modelle ohne neue Messung behalten den
    ''' frueheren Wert aus dem Abgleich mit der Kameravorschau.
    '''
    ''' WAS DIE TABELLE NICHT KANN: bei einzelnen Modellen haengt Adobes Wert an der Aufnahme
    ''' (Canons Tonwert-Prioritaet, Nikons Bittiefe), eine Zahl je Modell bildet das nicht ab. Und
    ''' sie wirkt auf JEDE Entwicklung, auch auf Bilder, deren Regler auf der festen
    ''' Grundbelichtung eingestellt wurden. Deshalb ist sie eine EINSTELLUNG und nicht das
    ''' Standardverhalten.
    '''
    ''' VERANKERUNG: Alle Werte sind RELATIV zu verstehen. Angewendet wird die Differenz zur
    ''' Referenzkamera, an der die Grundbelichtung gefittet wurde - die behaelt damit exakt ihr
    ''' bisheriges Ergebnis.
    '''
    ''' WO DIE ZAHLEN STEHEN: in Resources/CameraBaselineTable.json, als eingebettete Ressource.
    ''' Diese Klasse liest sie EINMAL und faellt bei einem Lesefehler geschlossen auf leere
    ''' Tabellen und die Notbehelfe zurueck - nie auf eine Mischung aus beidem.
    ''' </summary>
    Public NotInheritable Class CameraBaselineTable

        Private Sub New()
        End Sub

        ''' <summary>Versatz der Kamera, an der GrundbelichtungEv gefittet wurde (Canon EOS R6).
        ''' Nur diese eine Zahl verankert die Tabelle absolut - alles andere ist relativ. Die
        ''' Ressource bringt sie mit; dieser Wert ist der Notbehelf, wenn sie nicht lesbar ist.</summary>
        Private Const DefaultReferenceOffsetEv As Double = -0.26

        ''' <summary>Aeusserste Grenze der Verschiebung, als Notbehelf; die Ressource bringt ihren
        ''' eigenen Deckel mit (2 EV, weil gemessene Versaetze bis rund 1,6 EV reichen). Ein einzelner
        ''' Tabellenwert kann danebenliegen; ohne Deckel wuerde daraus ein unbrauchbares Bild.</summary>
        Private Const DefaultMaxShiftEv As Double = 1.0

        Private Const ResourceFileName As String = "CameraBaselineTable.json"

        ''' <summary>Der EINE gelesene Stand der Ressource. Anker, Deckel, Versatztabelle,
        ''' Farbkalibrierung und Pegelkorrekturen stehen in derselben Datei und entstehen deshalb in einem Zug: zwei
        ''' getrennte Ladewege haetten sie zweimal geparst und koennten bei einem Lesefehler
        ''' unterschiedlich weit gekommen sein.</summary>
        Private NotInheritable Class BaselineResource
            Public ReadOnly ReferenceOffsetEv As Double
            Public ReadOnly MaxShiftEv As Double
            Public ReadOnly Offsets As Dictionary(Of String, Double)
            Public ReadOnly ColorCalibrations As Dictionary(Of String, ColorCalibration)
            Public ReadOnly DefaultColorCalibration As ColorCalibration
            Public ReadOnly LevelOverrides As Dictionary(Of String, (Black As Integer, Range As Integer, White As Integer))
            ''' Wo Canons ColorData einer Kamera ihre Pegel hat, je Pegeleintrag mit black = -3.
            Public ReadOnly LevelColorData As New Dictionary(Of String, (Version As Integer, BlackWord As Integer, WhiteWord As Integer))(StringComparer.OrdinalIgnoreCase)
            ''' Wo Canons ColorData ihre Pegel hat, je Fassung und Laenge ("66|3778"): Wort des
            ''' ersten Schwarzwerts; SpecularWhiteLevel steht fuenf Worte dahinter.
            Public ReadOnly CanonColorDataLayouts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)

            Public Sub New(referenceOffsetEv As Double, maxShiftEv As Double,
                           offsets As Dictionary(Of String, Double),
                           colorCalibrations As Dictionary(Of String, ColorCalibration),
                           defaultColorCalibration As ColorCalibration,
                           levelOverrides As Dictionary(Of String, (Black As Integer, Range As Integer, White As Integer)))
                Me.ReferenceOffsetEv = referenceOffsetEv
                Me.MaxShiftEv = maxShiftEv
                Me.Offsets = offsets
                Me.ColorCalibrations = colorCalibrations
                Me.DefaultColorCalibration = defaultColorCalibration
                Me.LevelOverrides = levelOverrides
            End Sub
        End Class

        Private Shared ReadOnly Resource As BaselineResource = LoadResource()

        ''' <summary>Die vorhandenen Regler der Kamerakalibrierung als Vorgabe. Sie werden nur mit
        ''' der Einstellung "Farben an das Kameramodell anpassen" auf unbearbeitete RAWs gesetzt.</summary>
        Public NotInheritable Class ColorCalibration
            Public Property RedHue As Single
            Public Property RedSaturation As Single
            Public Property GreenHue As Single
            Public Property GreenSaturation As Single
            Public Property BlueHue As Single
            Public Property BlueSaturation As Single
            Public Property ShadowTint As Single
        End Class

        Private Shared Function LoadResource() As BaselineResource
            Dim offsets As New Dictionary(Of String, Double)(StringComparer.Ordinal)
            Dim calibrations As New Dictionary(Of String, ColorCalibration)(StringComparer.Ordinal)
            Dim levels As New Dictionary(Of String, (Black As Integer, Range As Integer, White As Integer))(StringComparer.OrdinalIgnoreCase)
            Dim levelColorData As New Dictionary(Of String, (Version As Integer, BlackWord As Integer, WhiteWord As Integer))(StringComparer.OrdinalIgnoreCase)
            Dim canonLayouts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            Dim defaultCalibration As ColorCalibration = Nothing
            ' Anker und Deckel werden erst zugewiesen, wenn die ganze Datei gelesen ist. Sonst
            ' stuende nach einem Abbruch zwischen beiden der eine Wert aus der Datei neben dem
            ' anderen aus dem Notbehelf - eine Mischung, die es nirgends geben darf.
            Dim referenceOffsetEv As Double = DefaultReferenceOffsetEv
            Dim maxShiftEv As Double = DefaultMaxShiftEv

            Try
                Dim assembly = GetType(CameraBaselineTable).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Throw New InvalidDataException("Kamera-Referenzwerte fehlen.")
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Throw New InvalidDataException("Kamera-Referenzwerte fehlen.")
                    Using document = JsonDocument.Parse(stream)
                        Dim root = document.RootElement
                        Dim readReferenceOffsetEv = root.GetProperty("referenceOffsetEv").GetDouble()
                        Dim readMaxShiftEv = root.GetProperty("maxShiftEv").GetDouble()
                        For Each entry In root.GetProperty("offsets").EnumerateObject()
                            offsets(entry.Name) = entry.Value.GetDouble()
                        Next
                        Dim calibrationEntries As JsonElement
                        If root.TryGetProperty("colorCalibrations", calibrationEntries) Then
                            For Each entry In calibrationEntries.EnumerateObject()
                                calibrations(entry.Name) = ReadColorCalibration(entry.Value)
                            Next
                        End If
                        ' Der Standard fuer jede Kamera ohne eigenen Eintrag: Adobes Bildstil ist zum
                        ' groessten Teil fuer alle Kameras derselbe (gemessen an rund 400 Aufnahmen,
                        ' Audits/RAW_UND_FARBE.md). Ein Modelleintrag ersetzt ihn ganz, er ist also
                        ' vollstaendig und keine Abweichung davon.
                        Dim defaultEntry As JsonElement
                        If root.TryGetProperty("colorCalibrationDefault", defaultEntry) Then
                            defaultCalibration = ReadColorCalibration(defaultEntry)
                        End If
                        ' Schwarzpunkt und Tonumfang fuer Kameras, die LibRaw falsch liest; siehe
                        ' RawDecodeService.LevelOverrides. Die LibRaw-Werte und die Quelle daneben
                        ' sind Nachweis und werden nicht gelesen.
                        Dim levelEntries As JsonElement
                        If root.TryGetProperty("levelOverrides", levelEntries) Then
                            For Each entry In levelEntries.EnumerateObject()
                                ' range und white sind beide wahlfrei (-1 = bei LibRaw lassen): der
                                ' Tonumfang, wenn der Schwarzpunkt feststeht, der rohe Weisspunkt,
                                ' wenn er je Datei gemessen wird (black = -2, siehe
                                ' RawDecodeService.MeasuredBlackMarker).
                                Dim rangeValue, whiteValue, colorDataValue As JsonElement
                                levels(entry.Name) = (entry.Value.GetProperty("black").GetInt32(),
                                                      If(entry.Value.TryGetProperty("range", rangeValue), rangeValue.GetInt32(), -1),
                                                      If(entry.Value.TryGetProperty("white", whiteValue), whiteValue.GetInt32(), -1))
                                ' black = -3: Schwarz- und Weisspunkt aus Canons ColorData der Datei;
                                ' dazu gehoert, wo sie in welcher Fassung stehen (CanonColorData).
                                If entry.Value.TryGetProperty("colorData", colorDataValue) Then
                                    levelColorData(entry.Name) = (colorDataValue.GetProperty("version").GetInt32(),
                                                                  colorDataValue.GetProperty("black").GetInt32(),
                                                                  colorDataValue.GetProperty("white").GetInt32())
                                End If
                            Next
                        End If
                        ' Die Lage der Pegel in Canons ColorData je Fassung, fuer alle Canons ohne
                        ' eigenen Pegeleintrag (RawDecodeService.TryApplyLevelOverride).
                        Dim layoutEntries As JsonElement
                        If root.TryGetProperty("canonColorDataLayouts", layoutEntries) Then
                            For Each entry In layoutEntries.EnumerateObject()
                                canonLayouts(entry.Name) = entry.Value.GetInt32()
                            Next
                        End If
                        referenceOffsetEv = readReferenceOffsetEv
                        maxShiftEv = readMaxShiftEv
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraBaselineTable.LoadResource", ex)
                offsets.Clear()
                calibrations.Clear()
                defaultCalibration = Nothing
                levels.Clear()
                levelColorData.Clear()
                canonLayouts.Clear()
                referenceOffsetEv = DefaultReferenceOffsetEv
                maxShiftEv = DefaultMaxShiftEv
            End Try

            Dim result = New BaselineResource(referenceOffsetEv, maxShiftEv, offsets, calibrations, defaultCalibration, levels)
            For Each pair In levelColorData
                result.LevelColorData(pair.Key) = pair.Value
            Next
            For Each pair In canonLayouts
                result.CanonColorDataLayouts(pair.Key) = pair.Value
            Next
            Return result
        End Function

        Private Shared Function ReadColorCalibration(item As JsonElement) As ColorCalibration
            Return New ColorCalibration With {
                .RedHue = item.GetProperty("redHue").GetSingle(), .RedSaturation = item.GetProperty("redSaturation").GetSingle(),
                .GreenHue = item.GetProperty("greenHue").GetSingle(), .GreenSaturation = item.GetProperty("greenSaturation").GetSingle(),
                .BlueHue = item.GetProperty("blueHue").GetSingle(), .BlueSaturation = item.GetProperty("blueSaturation").GetSingle(),
                .ShadowTint = item.GetProperty("shadowTint").GetSingle()}
        End Function

        ''' <summary>Schluessel aus Hersteller und Modell: nur Buchstaben und Ziffern, gross.
        ''' Die Marke wird vorangestellt, wenn das Modell sie nicht schon enthaelt - Nikon schreibt
        ''' "NIKON D800", Fujifilm nur "X-Pro1".</summary>
        Friend Shared Function Key(maker As String, model As String) As String
            Dim token = LettersAndDigitsOnly(If(maker, "").Split(" "c)(0))
            Dim m = LettersAndDigitsOnly(model)
            If m.Length = 0 Then Return ""
            Return If(token.Length > 0 AndAlso m.StartsWith(token, StringComparison.Ordinal), m, token & m)
        End Function

        Private Shared Function LettersAndDigitsOnly(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim sb As New StringBuilder(s.Length)
            For Each c In s.ToUpperInvariant()
                If (c >= "A"c AndAlso c <= "Z"c) OrElse (c >= "0"c AndAlso c <= "9"c) Then sb.Append(c)
            Next
            Return sb.ToString()
        End Function

        ''' <summary>Schwarzpunkt und Tonumfang ueber Schwarz fuer Kameras, die LibRaw falsch liest,
        ''' aus dem Abschnitt levelOverrides der Ressource. Anders als die Versaetze gilt diese
        ''' Tabelle IMMER, nicht nur mit der Einstellung: sie behebt Fehler, sie verschiebt keine
        ''' Wiedergabe. Angewandt wird sie in RawDecodeService.TryApplyLevelOverride.</summary>
        Friend Shared ReadOnly Property LevelOverrides As Dictionary(Of String, (Black As Integer, Range As Integer, White As Integer))
            Get
                Return Resource.LevelOverrides
            End Get
        End Property

        ''' <summary>Wo Canons ColorData ihre Pegel hat, je Pegeleintrag mit black = -3; siehe
        ''' CanonColorData und RawDecodeService.ColorDataBlackMarker.</summary>
        Friend Shared ReadOnly Property LevelColorData As Dictionary(Of String, (Version As Integer, BlackWord As Integer, WhiteWord As Integer))
            Get
                Return Resource.LevelColorData
            End Get
        End Property

        ''' <summary>Wo Canons ColorData ihre Pegel hat, je Fassung und Laenge ("66|3778"): Wort des
        ''' ersten Schwarzwerts. Gilt fuer JEDE Canon ohne eigenen Pegeleintrag; siehe
        ''' RawDecodeService.TryApplyLevelOverride.</summary>
        Friend Shared ReadOnly Property CanonColorDataLayouts As Dictionary(Of String, Integer)
            Get
                Return Resource.CanonColorDataLayouts
            End Get
        End Property

        ''' <summary>Anzahl der hinterlegten Modelle - fuer die Diagnose und die Einstellungsseite.</summary>
        Public Shared ReadOnly Property ModelCount As Integer
            Get
                Return Resource.Offsets.Count
            End Get
        End Property

        ''' <summary>Anzahl der Modelle mit EIGENER Farbkalibrierung, ohne den Standard. Fuer die
        ''' Diagnose.</summary>
        Public Shared ReadOnly Property ColorCalibrationCount As Integer
            Get
                Return Resource.ColorCalibrations.Count
            End Get
        End Property

        ''' <summary>Die Grundbelichtung fuer diese Kamera. Unbekanntes Modell oder fehlende Angaben:
        ''' der uebergebene Standardwert bleibt unveraendert.</summary>
        Public Shared Function BaseExposureFor(maker As String, model As String,
                                                   standardEv As Double) As Double
            Dim k = Key(maker, model)
            If k.Length = 0 Then Return standardEv
            Dim offset As Double
            If Not Resource.Offsets.TryGetValue(k, offset) Then Return standardEv
            ' Relativ zur Referenzkamera: die behaelt exakt ihren bisherigen Wert.
            Dim delta = offset - Resource.ReferenceOffsetEv
            If delta > Resource.MaxShiftEv Then delta = Resource.MaxShiftEv
            If delta < -Resource.MaxShiftEv Then delta = -Resource.MaxShiftEv
            Return standardEv - delta
        End Function

        ''' <summary>Der Standard der Farbkalibrierung fuer Kameras ohne eigenen Eintrag, oder
        ''' Nothing, wenn die Ressource keinen traegt.</summary>
        Public Shared ReadOnly Property DefaultColorCalibration As ColorCalibration
            Get
                Return Resource.DefaultColorCalibration
            End Get
        End Property

        ''' <summary>Hat dieses Modell einen EIGENEN Eintrag (und nicht nur den Standard)?</summary>
        Public Shared Function HasOwnColorCalibration(maker As String, model As String) As Boolean
            Dim k = Key(maker, model)
            Return k.Length > 0 AndAlso Resource.ColorCalibrations.ContainsKey(k)
        End Function

        ''' <summary>Die Kalibrierungsregler fuer dieses Modell: sein eigener Eintrag, sonst der
        ''' Standard, sonst Nothing. Ohne Kameranamen gibt es ebenfalls den Standard, denn er haengt
        ''' an keiner Kamera.</summary>
        Public Shared Function ColorCalibrationFor(maker As String, model As String) As ColorCalibration
            Dim result As ColorCalibration = Nothing
            Dim k = Key(maker, model)
            If k.Length > 0 AndAlso Resource.ColorCalibrations.TryGetValue(k, result) Then Return result
            Return Resource.DefaultColorCalibration
        End Function

    End Class

End Namespace
