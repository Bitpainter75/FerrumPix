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
    ''' gefittet. Auf anderen Modellen entwickelt er systematisch zu hell, gemessen bis knapp eine
    ''' Blendenstufe. Diese Tabelle gleicht die Modelle UNTEREINANDER an.
    '''
    ''' WIE GEMESSEN: je Modell der Tonwert-Versatz unserer Basisstufe zur kamerainternen
    ''' JPEG-Vorschau (Median der Luminanz, Vorschau nach ihrem Orientierungs-Tag gedreht), ueber
    ''' 235 Aufnahmen aus 217 Modellen und 14 Marken (5 Modelle verworfen, deren Vorschau
    ''' unbrauchbar war - jenseits von 1,2 EV ist das keine Grundbelichtung mehr, sondern ein Messfehler). Der Versatz ist weit ueberwiegend eine
    ''' Kameraeigenschaft: Streuung innerhalb eines Modells 0,07 EV, zwischen den Modellen 0,44 EV.
    '''
    ''' WAS DIE TABELLE NICHT KANN: Die kamerainterne Vorschau traegt den BILDSTIL des Herstellers.
    ''' Die Streuung innerhalb einer Marke ist klein, die Mediane zwischen den Marken unterscheiden
    ''' sich aber deutlich - ein Teil des Versatzes ist also Stil, nicht Sensoreigenschaft. Getrennt
    ''' werden koennte das nur mit Referenzexporten mehrerer Kameras. Deshalb ist die Tabelle eine
    ''' EINSTELLUNG und nicht das Standardverhalten.
    '''
    ''' VERANKERUNG: Alle Werte sind RELATIV zu verstehen. Angewendet wird die Differenz zur
    ''' Referenzkamera, an der die Grundbelichtung gefittet wurde - die behaelt damit exakt ihr
    ''' bisheriges Ergebnis. Die Referenzkamera ist selbst untypisch (1,5 Streuungen unter dem
    ''' Median); sobald ein zweiter echter Referenzexport vorliegt, gehoert referenceOffsetEv
    ''' nachgezogen und die Verankerung geprueft.
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

        ''' <summary>Aeusserste Grenze der Verschiebung. Ein einzelner Tabellenwert kann durch eine
        ''' unbrauchbare Vorschau danebenliegen; ohne Deckel wuerde daraus ein unbrauchbares Bild.</summary>
        Private Const DefaultMaxShiftEv As Double = 1.0

        Private Const ResourceFileName As String = "CameraBaselineTable.json"

        ''' <summary>Der EINE gelesene Stand der Ressource. Anker, Deckel, Versatztabelle und
        ''' Farbkalibrierung stehen in derselben Datei und entstehen deshalb in einem Zug: zwei
        ''' getrennte Ladewege haetten sie zweimal geparst und koennten bei einem Lesefehler
        ''' unterschiedlich weit gekommen sein.</summary>
        Private NotInheritable Class BaselineResource
            Public ReadOnly ReferenceOffsetEv As Double
            Public ReadOnly MaxShiftEv As Double
            Public ReadOnly Offsets As Dictionary(Of String, Double)
            Public ReadOnly ColorCalibrations As Dictionary(Of String, ColorCalibration)

            Public Sub New(referenceOffsetEv As Double, maxShiftEv As Double,
                           offsets As Dictionary(Of String, Double),
                           colorCalibrations As Dictionary(Of String, ColorCalibration))
                Me.ReferenceOffsetEv = referenceOffsetEv
                Me.MaxShiftEv = maxShiftEv
                Me.Offsets = offsets
                Me.ColorCalibrations = colorCalibrations
            End Sub
        End Class

        Private Shared ReadOnly Resource As BaselineResource = LoadResource()

        ''' <summary>Die vorhandenen Regler der Kamerakalibrierung als kamerafeste Vorgabe. Sie
        ''' werden nur bei aktivierter optionaler Kameratabelle auf uneditierte RAWs gesetzt.</summary>
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
                        referenceOffsetEv = readReferenceOffsetEv
                        maxShiftEv = readMaxShiftEv
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraBaselineTable.LoadResource", ex)
                offsets.Clear()
                calibrations.Clear()
                referenceOffsetEv = DefaultReferenceOffsetEv
                maxShiftEv = DefaultMaxShiftEv
            End Try

            Return New BaselineResource(referenceOffsetEv, maxShiftEv, offsets, calibrations)
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

        ''' <summary>Anzahl der hinterlegten Modelle - fuer die Diagnose und die Einstellungsseite.</summary>
        Public Shared ReadOnly Property ModelCount As Integer
            Get
                Return Resource.Offsets.Count
            End Get
        End Property

        ''' <summary>Anzahl der Modelle mit hinterlegter Farbkalibrierung. Sie ist heute null: die
        ''' Mechanik steht, die Werte brauchen je Kamera mehrere farblich belastbare Referenzen.
        ''' Fuer die Diagnose, und damit die Aufrufer den Weg ueberspringen koennen, solange nichts
        ''' zu finden ist.</summary>
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

        ''' <summary>Die kamerafesten Kalibrierungsregler fuer dieses Modell, oder Nothing.</summary>
        Public Shared Function ColorCalibrationFor(maker As String, model As String) As ColorCalibration
            If Resource.ColorCalibrations.Count = 0 Then Return Nothing
            Dim result As ColorCalibration = Nothing
            Return If(Resource.ColorCalibrations.TryGetValue(Key(maker, model), result), result, Nothing)
        End Function

    End Class

End Namespace
