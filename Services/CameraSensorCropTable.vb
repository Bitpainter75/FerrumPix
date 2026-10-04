Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json

Namespace Services

    ''' <summary>Der Sensorrand je Kameramodell und Bildgroesse: wie viele Pixel links, oben,
    ''' rechts und unten Adobe von LibRaws Entwicklung abschneidet.
    '''
    ''' LibRaw entwickelt die ganze aktive Sensorflaeche, samt der aeussersten Spalten und Zeilen,
    ''' die das Demosaic ohne Nachbarn nicht sauber rechnen kann; bei der D7000 stehen sie als
    ''' farbige Streifen am linken und rechten Rand. LibRaw selbst kennt den Rand nicht
    ''' (raw_inset_crops bleibt bei NEF leer). Adobe traegt ihn in jede DNG ein, als DefaultCrop
    ''' relativ zur ActiveArea.
    '''
    ''' WO DIE ZAHLEN STEHEN: in Resources/CameraSensorCrops.json, gebaut von
    ''' Diagnostics/Grundbelichtung/sensorrand.py aus Adobes DNG des Bestands. Eingetragen ist nur,
    ''' was eindeutig ist: LibRaws Bildgroesse ist genau Adobes aktive Flaeche, und alle Aufnahmen
    ''' desselben Modells in derselben Groesse haben denselben Beschnitt. Wo LibRaw eine andere
    ''' Flaeche entwickelt, ist ohne Messung nicht zu sagen, an welcher Ecke die beiden gegeneinander
    ''' verschoben sind, und ein um wenige Pixel falscher Beschnitt liesse genau den Streifen
    ''' stehen. Dort steht nichts, und der Schalter tut nichts.
    '''
    ''' Die Werte zaehlen in der UNGEDREHTEN Lage des Sensors; gedreht wird erst beim Anwenden,
    ''' siehe RawDecodeService.SensorEdgeFor.</summary>
    Public NotInheritable Class CameraSensorCropTable

        Private Sub New()
        End Sub

        Private Const ResourceFileName As String = "CameraSensorCrops.json"

        Private Shared ReadOnly Crops As Dictionary(Of String, (Left As Integer, Top As Integer, Right As Integer, Bottom As Integer)) = LoadResource()

        Private Shared Function LoadResource() As Dictionary(Of String, (Left As Integer, Top As Integer, Right As Integer, Bottom As Integer))
            Dim result As New Dictionary(Of String, (Left As Integer, Top As Integer, Right As Integer, Bottom As Integer))(StringComparer.Ordinal)
            Try
                Dim assembly = GetType(CameraSensorCropTable).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Return result
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Return result
                    Using document = JsonDocument.Parse(stream)
                        For Each entry In document.RootElement.GetProperty("crops").EnumerateObject()
                            Dim v = entry.Value.EnumerateArray().Select(Function(e) e.GetInt32()).ToArray()
                            If v.Length <> 4 OrElse v.Any(Function(x) x < 0) Then Continue For
                            result(entry.Name) = (v(0), v(1), v(2), v(3))
                        Next
                    End Using
                End Using
            Catch ex As Exception
                ' Ohne Tabelle schneidet der Schalter nichts ab; das Bild bleibt, wie LibRaw es
                ' entwickelt. Ein halb gelesener Stand wird verworfen.
                DiagnosticLogService.LogException("CameraSensorCropTable.LoadResource", ex)
                result.Clear()
            End Try
            Return result
        End Function

        ''' <summary>Der Rand fuer Kamera und ungedrehte Bildgroesse, oder Nothing ohne Eintrag.</summary>
        Public Shared Function Lookup(maker As String, model As String, width As Integer, height As Integer) As (Left As Integer, Top As Integer, Right As Integer, Bottom As Integer)?
            Dim cameraKey = CameraBaselineTable.Key(maker, model)
            If cameraKey.Length = 0 Then Return Nothing
            Dim crop As (Left As Integer, Top As Integer, Right As Integer, Bottom As Integer)
            If Not Crops.TryGetValue(cameraKey & "|" & width & "x" & height, crop) Then Return Nothing
            ' Ein Rand, der das Bild aufzehrt, ist ein Fehler in der Tabelle, kein Beschnitt.
            If crop.Left + crop.Right >= width OrElse crop.Top + crop.Bottom >= height Then Return Nothing
            Return crop
        End Function

        ''' <summary>Anzahl der Eintraege - fuer die Diagnose.</summary>
        Public Shared ReadOnly Property Count As Integer
            Get
                Return Crops.Count
            End Get
        End Property

    End Class

End Namespace
