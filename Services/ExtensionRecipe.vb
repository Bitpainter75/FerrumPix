Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>Was ein Rezept ueber EINE Erweiterung festhaelt: welche es ist, in welcher Fassung
    ''' sie beim Einstellen vorlag, ob sie rechnet, und die Werte ihrer Regler. Mehr nicht: die
    ''' Rechnung selbst steht in der Erweiterung (<see cref="ExtensionDefinition"/>), nicht im
    ''' Rezept.</summary>
    Public NotInheritable Class ExtensionEntry
        Public Property Id As String = ""
        Public Property Version As Integer = 1
        Public Property Enabled As Boolean = True
        ''' <summary>Reglerwerte nach Schluessel. Ein Schalter steht als 0 oder 1.</summary>
        Public Property Values As New SortedDictionary(Of String, Double)(StringComparer.Ordinal)
        ''' <summary>Farben nach Schluessel als "#RRGGBB".</summary>
        Public Property Colors As New SortedDictionary(Of String, String)(StringComparer.Ordinal)
        ''' <summary>Kurven nach Schluessel: Stuetzpunkte von 0 bis 1, nach X steigend.</summary>
        Public Property Curves As New SortedDictionary(Of String, List(Of (X As Double, Y As Double)))(StringComparer.Ordinal)

        Public Function Clone() As ExtensionEntry
            Dim copy = New ExtensionEntry With {.Id = Id, .Version = Version, .Enabled = Enabled}
            For Each pair In Values
                copy.Values(pair.Key) = pair.Value
            Next
            For Each pair In Colors
                copy.Colors(pair.Key) = pair.Value
            Next
            For Each pair In Curves
                copy.Curves(pair.Key) = pair.Value.ToList()
            Next
            Return copy
        End Function
    End Class

    ''' <summary>
    ''' Die Erweiterungen eines Rezepts als EINE Zeichenkette (ImageAdjustments.ExtensionSettings).
    '''
    ''' WARUM EINE ZEICHENKETTE UND KEINE LISTE: das Rezept wird an vielen Stellen ueber seine
    ''' Eigenschaften abgefragt, kopiert und verglichen, ohne dass die einzelne Eigenschaft bekannt
    ''' ist (ImageAdjustments.PixelAdjustmentProperties: Uebertragen auf andere Bilder,
    ''' Korrekturebenen, "traegt dieses Rezept ueberhaupt eine Anpassung"). Diese Wege kopieren
    ''' den Wert und vergleichen ihn mit der Vorgabe. Eine Liste waere dort nie gleich ihrer
    ''' Vorgabe (jedes Bild gaelte als bearbeitet) und wuerde zwischen zwei Rezepten geteilt
    ''' statt kopiert. Eine Zeichenkette ist unveraenderlich und vergleicht nach Inhalt; leer
    ''' heisst "keine Erweiterung".
    '''
    ''' DIE FORM IST FEST, damit zwei Rezepte mit denselben Einstellungen zeichengleich sind: die
    ''' Erweiterungen in ihrer Reihenfolge (sie ist die Rechenfolge), die Werte nach Schluessel
    ''' sortiert, Zahlen ohne Landesformat und in der kuerzesten Form, die den Wert wiedergibt.
    '''
    ''' WAS NICHT GELESEN WERDEN KANN, GEHT NICHT VERLOREN, SONDERN LEER AUS: eine kaputte
    ''' Zeichenkette ergibt eine leere Liste. Eine Erweiterung, die auf diesem Rechner FEHLT, ist
    ''' dagegen kein Lesefehler; ihr Eintrag bleibt stehen und wird wieder so geschrieben.
    ''' </summary>
    Public NotInheritable Class ExtensionRecipe

        Private Sub New()
        End Sub

        Private Shared ReadOnly ColorPattern As New Text.RegularExpressions.Regex("^#[0-9A-Fa-f]{6}$")

        Public Shared Function Parse(text As String) As List(Of ExtensionEntry)
            Dim result As New List(Of ExtensionEntry)()
            If String.IsNullOrWhiteSpace(text) Then Return result
            Try
                Using document = JsonDocument.Parse(text)
                    If document.RootElement.ValueKind <> JsonValueKind.Array Then Return result
                    For Each item In document.RootElement.EnumerateArray()
                        If item.ValueKind <> JsonValueKind.Object Then Continue For
                        Dim entry As New ExtensionEntry()
                        Dim element As JsonElement
                        If item.TryGetProperty("id", element) AndAlso element.ValueKind = JsonValueKind.String Then
                            entry.Id = If(element.GetString(), "").Trim()
                        End If
                        If entry.Id.Length = 0 Then Continue For
                        ' Dieselbe Erweiterung zweimal waere zweimal dieselbe Rechnung mit zwei
                        ' Reglersaetzen, von denen die Oberflaeche nur einen zeigt.
                        If result.Any(Function(e) String.Equals(e.Id, entry.Id, StringComparison.Ordinal)) Then Continue For
                        Dim version As Integer
                        If item.TryGetProperty("version", element) AndAlso element.ValueKind = JsonValueKind.Number AndAlso
                           element.TryGetInt32(version) Then
                            entry.Version = Math.Max(1, version)
                        End If
                        If item.TryGetProperty("enabled", element) Then
                            entry.Enabled = element.ValueKind <> JsonValueKind.False
                        End If
                        If item.TryGetProperty("values", element) AndAlso element.ValueKind = JsonValueKind.Object Then
                            For Each value In element.EnumerateObject()
                                Dim number As Double
                                If value.Value.ValueKind = JsonValueKind.Number AndAlso value.Value.TryGetDouble(number) AndAlso
                                   Double.IsFinite(number) Then
                                    entry.Values(value.Name) = number
                                End If
                            Next
                        End If
                        If item.TryGetProperty("colors", element) AndAlso element.ValueKind = JsonValueKind.Object Then
                            For Each value In element.EnumerateObject()
                                Dim color = If(value.Value.ValueKind = JsonValueKind.String, If(value.Value.GetString(), "").Trim(), "")
                                If ColorPattern.IsMatch(color) Then entry.Colors(value.Name) = color.ToUpperInvariant()
                            Next
                        End If
                        If item.TryGetProperty("curves", element) AndAlso element.ValueKind = JsonValueKind.Object Then
                            For Each value In element.EnumerateObject()
                                Dim points As List(Of (X As Double, Y As Double)) = Nothing
                                If ExtensionService.ReadCurvePoints(value.Value, points).Length = 0 Then entry.Curves(value.Name) = points
                            Next
                        End If
                        result.Add(entry)
                    Next
                End Using
            Catch ex As JsonException
                result.Clear()
            End Try
            Return result
        End Function

        Public Shared Function Serialize(entries As IEnumerable(Of ExtensionEntry)) As String
            Dim list = If(entries, Enumerable.Empty(Of ExtensionEntry)()).
                Where(Function(e) e IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(e.Id)).ToList()
            If list.Count = 0 Then Return ""
            Using stream As New MemoryStream()
                Using writer As New Utf8JsonWriter(stream)
                    writer.WriteStartArray()
                    For Each entry In list
                        writer.WriteStartObject()
                        writer.WriteString("id", entry.Id.Trim())
                        writer.WriteNumber("version", Math.Max(1, entry.Version))
                        writer.WriteBoolean("enabled", entry.Enabled)
                        writer.WriteStartObject("values")
                        For Each pair In entry.Values
                            If Not Double.IsFinite(pair.Value) Then Continue For
                            writer.WritePropertyName(pair.Key)
                            writer.WriteRawValue(pair.Value.ToString("R", CultureInfo.InvariantCulture))
                        Next
                        writer.WriteEndObject()
                        ' Farben und Kurven nur, wenn es welche gibt: ein Eintrag aus Reglern allein
                        ' bleibt zeichengleich mit der Form, die es vor ihnen gab.
                        If entry.Colors.Count > 0 Then
                            writer.WriteStartObject("colors")
                            For Each pair In entry.Colors
                                writer.WriteString(pair.Key, pair.Value)
                            Next
                            writer.WriteEndObject()
                        End If
                        If entry.Curves.Count > 0 Then
                            writer.WriteStartObject("curves")
                            For Each pair In entry.Curves
                                writer.WriteStartArray(pair.Key)
                                For Each point In pair.Value
                                    writer.WriteStartArray()
                                    writer.WriteRawValue(point.X.ToString("R", CultureInfo.InvariantCulture))
                                    writer.WriteRawValue(point.Y.ToString("R", CultureInfo.InvariantCulture))
                                    writer.WriteEndArray()
                                Next
                                writer.WriteEndArray()
                            Next
                            writer.WriteEndObject()
                        End If
                        writer.WriteEndObject()
                    Next
                    writer.WriteEndArray()
                End Using
                Return Encoding.UTF8.GetString(stream.ToArray())
            End Using
        End Function

        ''' <summary>Dieselben Einstellungen in der festen Form; leer, wenn nichts darin steht.</summary>
        Public Shared Function Normalize(text As String) As String
            Return Serialize(Parse(text))
        End Function

        ''' <summary>Rechnet in diesem Rezept ueberhaupt eine Erweiterung? Ohne Blick darauf, ob sie
        ''' auf diesem Rechner vorliegt.</summary>
        Public Shared Function HasEnabled(text As String) As Boolean
            If String.IsNullOrWhiteSpace(text) Then Return False
            Return Parse(text).Any(Function(e) e.Enabled)
        End Function

    End Class

End Namespace
