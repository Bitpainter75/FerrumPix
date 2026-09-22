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
    ''' WOZU: Unsere Basisstufe traegt eine feste Grundbelichtung (RawDecodeService.GrundbelichtungEv).
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
    ''' Median); sobald ein zweiter echter Referenzexport vorliegt, gehoert ReferenzVersatzEv
    ''' nachgezogen und die Verankerung geprueft.
    ''' </summary>
    Public NotInheritable Class CameraBaselineTable

        Private Sub New()
        End Sub

        ''' <summary>Versatz der Kamera, an der GrundbelichtungEv gefittet wurde (Canon EOS R6).
        ''' Nur diese eine Zahl verankert die Tabelle absolut - alles andere ist relativ.</summary>
        Private Shared ReferenceOffsetEv As Double

        ''' <summary>Aeusserste Grenze der Verschiebung. Ein einzelner Tabellenwert kann durch eine
        ''' unbrauchbare Vorschau danebenliegen; ohne Deckel wuerde daraus ein unbrauchbares Bild.</summary>
        Private Shared MaxShiftEv As Double

        Private Shared ReadOnly Table As Dictionary(Of String, Double) = LoadTable()

        Private Shared Function LoadTable() As Dictionary(Of String, Double)
            Dim d As New Dictionary(Of String, Double)(StringComparer.Ordinal)
            ReferenceOffsetEv = -0.26
            MaxShiftEv = 1.0

            Try
                Dim assembly = GetType(CameraBaselineTable).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith("CameraBaselineTable.json", StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Throw New InvalidDataException("Kamera-Referenzwerte fehlen.")
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Throw New InvalidDataException("Kamera-Referenzwerte fehlen.")
                    Using document = JsonDocument.Parse(stream)
                        Dim root = document.RootElement
                        ReferenceOffsetEv = root.GetProperty("referenceOffsetEv").GetDouble()
                        MaxShiftEv = root.GetProperty("maxShiftEv").GetDouble()
                        For Each entry In root.GetProperty("offsets").EnumerateObject()
                            d(entry.Name) = entry.Value.GetDouble()
                        Next
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraBaselineTable.LoadTable", ex)
                d.Clear()
            End Try

            Return d
        End Function

        ''' <summary>Schluessel aus Hersteller und Modell: nur Buchstaben und Ziffern, gross.
        ''' Die Marke wird vorangestellt, wenn das Modell sie nicht schon enthaelt - Nikon schreibt
        ''' "NIKON D800", Fujifilm nur "X-Pro1".</summary>
        Friend Shared Function Key(maker As String, modell As String) As String
            Dim token = LettersAndDigitsOnly(If(maker, "").Split(" "c)(0))
            Dim m = LettersAndDigitsOnly(modell)
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
                Return Table.Count
            End Get
        End Property

        ''' <summary>Die Grundbelichtung fuer diese Kamera. Unbekanntes Modell oder fehlende Angaben:
        ''' der uebergebene Standardwert bleibt unveraendert.</summary>
        Public Shared Function BaseExposureFor(maker As String, modell As String,
                                                   standardEv As Double) As Double
            Dim k = Key(maker, modell)
            If k.Length = 0 Then Return standardEv
            Dim offset As Double
            If Not Table.TryGetValue(k, offset) Then Return standardEv
            ' Relativ zur Referenzkamera: die behaelt exakt ihren bisherigen Wert.
            Dim delta = offset - ReferenceOffsetEv
            If delta > MaxShiftEv Then delta = MaxShiftEv
            If delta < -MaxShiftEv Then delta = -MaxShiftEv
            Return standardEv - delta
        End Function

    End Class

End Namespace
