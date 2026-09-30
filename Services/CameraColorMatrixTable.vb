Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Farbmatrizen fuer Kameras, fuer die die geladene LibRaw keine hat.
    '''
    ''' WOZU: Kennt LibRaw eine Kamera nicht, entwickelt sie mit der Einheitsmatrix. Grau bleibt
    ''' grau, aber die Saettigung faellt, das Bild wird FLAU (gemessen, siehe OFFENE_PUNKTE.md).
    ''' Betroffen sind vor allem neue Modelle, die LibRaw erst mit einer spaeteren Fassung lernt,
    ''' etwa die OM-3 mit 0.22.2.
    '''
    ''' WAS IN DER DATEI STEHT: je Kamera die Matrix XYZ nach Kamera fuer D65, in derselben Form
    ''' wie LibRaws eigene Tabelle und das DNG-Feld ColorMatrix2. Daraus rechnet diese Klasse
    ''' rgb_cam und die Tageslicht-Multiplikatoren genau so, wie LibRaw es fuer eine bekannte
    ''' Kamera tut (cam_xyz_coeff). Die Rechnung ist gegen LibRaw geprueft: aus Adobes Matrix der
    ''' D90 kommt LibRaws rgb_cam der D90 heraus.
    '''
    ''' WANN SIE GILT: nur solange LibRaw fuer die Kamera die Einheitsmatrix meldet. Lernt eine
    ''' spaetere LibRaw die Kamera, wird der Eintrag still wirkungslos und kann heraus. Angewandt
    ''' wird in RawDecodeService (OwnCameraMatrix).
    '''
    ''' WO DIE ZAHLEN STEHEN: in Resources/CameraColorMatrices.json, als eingebettete Ressource.
    ''' Gelesen wird EINMAL; bei einem Lesefehler bleibt die Tabelle ganz leer, nie halb.
    ''' </summary>
    Public NotInheritable Class CameraColorMatrixTable

        Private Sub New()
        End Sub

        Private Const ResourceFileName As String = "CameraColorMatrices.json"

        ''' <summary>sRGB (linear, D65) nach XYZ, dieselben Zahlen wie LibRaws xyz_rgb. Andere
        ''' Stellen als dort ergaeben eine andere Matrix als die, die LibRaw fuer eine bekannte
        ''' Kamera rechnet.</summary>
        Private Shared ReadOnly XyzFromSrgb As Double() = {
            0.412453, 0.35758, 0.180423,
            0.212671, 0.71516, 0.072169,
            0.019334, 0.119193, 0.950227}

        ''' <summary>Eine Kamera der Tabelle, fertig umgerechnet.</summary>
        Public NotInheritable Class CameraMatrix
            ''' <summary>Kameraraum nach sRGB (rgb_cam), 3x3 zeilenweise; jede Zeile summiert
            ''' sich auf 1.</summary>
            Public ReadOnly RgbFromCamera As Single()
            ''' <summary>Multiplikatoren fuer Tageslicht (pre_mul), vier Eintraege; der vierte
            ''' ist das zweite Gruen und traegt denselben Wert wie das erste.</summary>
            Public ReadOnly DaylightMultipliers As Single()

            Public Sub New(rgbFromCamera As Single(), daylightMultipliers As Single())
                Me.RgbFromCamera = rgbFromCamera
                Me.DaylightMultipliers = daylightMultipliers
            End Sub
        End Class

        Private Shared ReadOnly Resource As Dictionary(Of String, CameraMatrix) = LoadResource()

        ''' <summary>Die Eintraege, geschluesselt nach LibRaws vereinheitlichter Marke und Modell
        ''' ("Olympus|OM-3"), gross und klein gleich.</summary>
        Public Shared ReadOnly Property Matrices As Dictionary(Of String, CameraMatrix)
            Get
                Return Resource
            End Get
        End Property

        Private Shared Function LoadResource() As Dictionary(Of String, CameraMatrix)
            Dim result As New Dictionary(Of String, CameraMatrix)(StringComparer.OrdinalIgnoreCase)
            Try
                Dim assembly = GetType(CameraColorMatrixTable).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Throw New InvalidDataException("Farbmatrizen fehlen.")
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Throw New InvalidDataException("Farbmatrizen fehlen.")
                    Using document = JsonDocument.Parse(stream)
                        For Each entry In document.RootElement.GetProperty("matrices").EnumerateObject()
                            Dim values = entry.Value.GetProperty("xyzToCamera").EnumerateArray().Select(Function(v) v.GetDouble()).ToArray()
                            Dim matrix = FromXyzToCamera(values)
                            ' Ein unbrauchbarer Eintrag verwirft die ganze Datei: eine halbe Tabelle
                            ' faellt niemandem auf, eine leere schon.
                            If matrix Is Nothing Then Throw New InvalidDataException($"Farbmatrix '{entry.Name}' ist nicht umkehrbar.")
                            result(entry.Name) = matrix
                        Next
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraColorMatrixTable.LoadResource", ex)
                result.Clear()
            End Try
            Return result
        End Function

        ''' <summary>rgb_cam und Tageslicht-Multiplikatoren aus der Matrix XYZ nach Kamera, wie
        ''' LibRaws cam_xyz_coeff: erst nach sRGB-Primaervalenzen umrechnen, jede Zeile auf 1
        ''' normieren (der Kehrwert der Zeilensumme ist pre_mul), dann umkehren. Nothing bei
        ''' falscher Laenge, nicht endlichen Werten, nicht positiver Zeilensumme oder einer
        ''' Matrix, die sich nicht umkehren laesst.</summary>
        Public Shared Function FromXyzToCamera(xyzToCamera As Double()) As CameraMatrix
            If xyzToCamera Is Nothing OrElse xyzToCamera.Length <> 9 Then Return Nothing
            If xyzToCamera.Any(Function(v) Not Double.IsFinite(v)) Then Return Nothing

            Dim camRgb(8) As Double
            Dim multipliers(3) As Single
            For row = 0 To 2
                Dim sum = 0.0
                For column = 0 To 2
                    Dim value = 0.0
                    For k = 0 To 2
                        value += xyzToCamera(row * 3 + k) * XyzFromSrgb(k * 3 + column)
                    Next
                    camRgb(row * 3 + column) = value
                    sum += value
                Next
                If Not (sum > 0.000001) Then Return Nothing
                For column = 0 To 2
                    camRgb(row * 3 + column) /= sum
                Next
                multipliers(row) = CSng(1.0 / sum)
            Next
            multipliers(3) = multipliers(1)

            Dim inverse = Invert3x3(camRgb)
            If inverse Is Nothing Then Return Nothing
            Return New CameraMatrix(inverse.Select(Function(v) CSng(v)).ToArray(), multipliers)
        End Function

        Private Shared Function Invert3x3(m As Double()) As Double()
            Dim a = m(0), b = m(1), c = m(2)
            Dim d = m(3), e = m(4), f = m(5)
            Dim g = m(6), h = m(7), i = m(8)
            Dim det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
            If Math.Abs(det) < 0.000000001 OrElse Not Double.IsFinite(det) Then Return Nothing
            Return {
                (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
                (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
                (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det}
        End Function

    End Class

End Namespace
