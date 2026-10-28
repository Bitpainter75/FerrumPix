Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Kameramatrizen fuer ZWEI Lichtarten je Kamera und die Farbumrechnung, die zwischen ihnen
    ''' nach dem Weissabgleich der Aufnahme mischt, wie es die DNG-Spezifikation beschreibt.
    '''
    ''' WOZU: LibRaw fuehrt EINE Matrix, auf D65 bezogen, und rechnet den Weissabgleich als
    ''' Verstaerkung im Kameraraum. Das ist nur fuer Tageslicht genau. Das Referenzprogramm mischt
    ''' zwischen einer Matrix fuer Normlicht A und einer fuer D65 und passt das Weiss der Aufnahme
    ''' mit Bradford an. Unter Kunst- und Buehnenlicht unterscheiden sich die beiden Wege sichtbar.
    '''
    ''' DIE RECHNUNG (DNG-Spezifikation, Kapitel 6): aus der neutralen Kameraantwort n = 1/cam_mul
    ''' und der gemischten Matrix den Farbort des Aufnahmeweisses bestimmen, daraus die
    ''' Farbtemperatur, daraus das Mischgewicht in mired, und das wiederholen, bis der Farbort
    ''' steht. Dann Kamera nach sRGB:
    ''' <code>sRGB = XYZ_nach_sRGB · Bradford(Weiss nach D65) · CM^-1 · diag(n)</code>
    ''' angewandt auf Daten, deren Weissabgleich LibRaw schon gemacht hat (daher diag(n)), jede
    ''' Zeile auf 1 normiert. Unter D65 und mit der D65-Matrix ist das GENAU LibRaws rgb_cam; die
    ''' Selbstprobe haelt das fest.
    '''
    ''' WAS IN DER DATEI STEHT: ColorMatrix1 und ColorMatrix2 mit ihren Lichtarten, gelesen aus der
    ''' DNG, die der Konverter des Referenzprogramms aus einer RAW der Kamera macht
    ''' (Diagnostics/Farbmatrix/zweimatrizen.py). Nur die Kameramatrizen, nicht das Profil.
    ''' </summary>
    Public NotInheritable Class CameraIlluminantMatrixTable

        Private Sub New()
        End Sub

        Private Const ResourceFileName As String = "CameraIlluminantMatrices.json"

        ''' <summary>sRGB (linear, D65) nach XYZ, LibRaws xyz_rgb. Dieselben Zahlen wie in
        ''' CameraColorMatrixTable, damit unter D65 genau LibRaws Matrix herauskommt.</summary>
        Private Shared ReadOnly XyzFromSrgb As Double() = {
            0.412453, 0.35758, 0.180423,
            0.212671, 0.71516, 0.072169,
            0.019334, 0.119193, 0.950227}

        Private Shared ReadOnly SrgbFromXyz As Double() = Invert3x3(XyzFromSrgb)

        Private Shared ReadOnly Bradford As Double() = {
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296}

        Private Shared ReadOnly BradfordInverse As Double() = Invert3x3(Bradford)

        ''' <summary>Weiss von sRGB in XYZ, aus denselben Zahlen (Zeilensummen von xyz_rgb).</summary>
        Private Shared ReadOnly D65White As Double() = {
            XyzFromSrgb(0) + XyzFromSrgb(1) + XyzFromSrgb(2),
            XyzFromSrgb(3) + XyzFromSrgb(4) + XyzFromSrgb(5),
            XyzFromSrgb(6) + XyzFromSrgb(7) + XyzFromSrgb(8)}

        ''' <summary>Startwert der Iteration, wie im DNG SDK: D50.</summary>
        Private Const StartX As Double = 0.3457
        Private Const StartY As Double = 0.3585

        Private Const MaxPasses As Integer = 30

        ''' <summary>Eine Kamera der Tabelle. Matrix2 ist Nothing, wenn die Kamera nur eine
        ''' Matrix traegt; dann gilt sie fuer jedes Licht.</summary>
        Public NotInheritable Class Entry
            Public ReadOnly Matrix1 As Double()
            Public ReadOnly Temperature1 As Double
            Public ReadOnly Matrix2 As Double()
            Public ReadOnly Temperature2 As Double

            Public Sub New(matrix1 As Double(), temperature1 As Double, matrix2 As Double(), temperature2 As Double)
                ' Sortiert: Matrix1 ist die waermere Lichtart, wie es die Mischung erwartet.
                If matrix2 IsNot Nothing AndAlso temperature1 > temperature2 Then
                    Me.Matrix1 = matrix2 : Me.Temperature1 = temperature2
                    Me.Matrix2 = matrix1 : Me.Temperature2 = temperature1
                Else
                    Me.Matrix1 = matrix1 : Me.Temperature1 = temperature1
                    Me.Matrix2 = matrix2 : Me.Temperature2 = temperature2
                End If
            End Sub
        End Class

        ''' <summary>Das Ergebnis fuer eine Aufnahme.</summary>
        Public NotInheritable Class Solution
            ''' <summary>Kameraraum (nach dem Weissabgleich) nach sRGB, 3x3 zeilenweise; jede
            ''' Zeile summiert sich auf 1.</summary>
            Public RgbFromCamera As Single()
            ''' <summary>Farbort des Aufnahmeweisses (CIE 1931).</summary>
            Public WhiteX As Double
            Public WhiteY As Double
            ''' <summary>Farbtemperatur des Aufnahmeweisses nach Robertson, wie das DNG SDK.</summary>
            Public Kelvin As Double
            ''' <summary>Anteil der waermeren Matrix, 0 bis 1.</summary>
            Public Weight As Double
        End Class

        Private Shared ReadOnly Resource As Dictionary(Of String, Entry) = LoadResource()

        ''' <summary>Die Eintraege, geschluesselt nach LibRaws vereinheitlichter Marke und Modell
        ''' ("Nikon|D850"), gross und klein gleich.</summary>
        Public Shared ReadOnly Property Entries As Dictionary(Of String, Entry)
            Get
                Return Resource
            End Get
        End Property

        Public Shared Function ForCamera(make As String, model As String) As Entry
            If String.IsNullOrWhiteSpace(make) OrElse String.IsNullOrWhiteSpace(model) Then Return Nothing
            Dim entry As Entry = Nothing
            Return If(Resource.TryGetValue(make.Trim() & "|" & model.Trim(), entry), entry, Nothing)
        End Function

        Private Shared Function LoadResource() As Dictionary(Of String, Entry)
            Dim result As New Dictionary(Of String, Entry)(StringComparer.OrdinalIgnoreCase)
            Try
                Dim assembly = GetType(CameraIlluminantMatrixTable).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Throw New InvalidDataException("Kameramatrizen fehlen.")
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Throw New InvalidDataException("Kameramatrizen fehlen.")
                    Using document = JsonDocument.Parse(stream)
                        For Each item In document.RootElement.GetProperty("matrices").EnumerateObject()
                            Dim matrix1 = ReadMatrix(item.Value, "matrix1")
                            Dim temperature1 = IlluminantTemperature(ReadIlluminant(item.Value, "illuminant1"))
                            Dim matrix2 = ReadMatrix(item.Value, "matrix2")
                            Dim temperature2 = IlluminantTemperature(ReadIlluminant(item.Value, "illuminant2"))
                            ' Ein unbrauchbarer Eintrag verwirft die ganze Datei, wie bei den
                            ' Farbmatrizen: eine halbe Tabelle faellt niemandem auf.
                            If matrix1 Is Nothing OrElse Invert3x3(matrix1) Is Nothing Then
                                Throw New InvalidDataException($"Kameramatrix '{item.Name}' ist nicht umkehrbar.")
                            End If
                            If matrix2 IsNot Nothing Then
                                If Invert3x3(matrix2) Is Nothing Then Throw New InvalidDataException($"Kameramatrix '{item.Name}' ist nicht umkehrbar.")
                                ' Ohne bekannte Temperatur beider Lichtarten laesst sich nicht
                                ' mischen; dann gilt die zweite allein, wie im DNG SDK.
                                If Double.IsNaN(temperature1) OrElse Double.IsNaN(temperature2) OrElse temperature1 = temperature2 Then
                                    matrix1 = matrix2 : temperature1 = temperature2
                                    matrix2 = Nothing
                                End If
                            End If
                            result(item.Name) = New Entry(matrix1, temperature1, matrix2, temperature2)
                        Next
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraIlluminantMatrixTable.LoadResource", ex)
                result.Clear()
            End Try
            Return result
        End Function

        Private Shared Function ReadMatrix(element As JsonElement, name As String) As Double()
            Dim value As JsonElement
            If Not element.TryGetProperty(name, value) Then Return Nothing
            Dim numbers = value.EnumerateArray().Select(Function(v) v.GetDouble()).ToArray()
            If numbers.Length <> 9 OrElse numbers.Any(Function(v) Not Double.IsFinite(v)) Then Return Nothing
            Return numbers
        End Function

        Private Shared Function ReadIlluminant(element As JsonElement, name As String) As Integer
            Dim value As JsonElement
            Return If(element.TryGetProperty(name, value), value.GetInt32(), 0)
        End Function

        ''' <summary>Farbtemperatur einer Lichtart nach dem Code aus CalibrationIlluminant, mit
        ''' denselben Werten wie das DNG SDK (dort gerundet, Normlicht A etwa 2850 statt 2856 K).
        ''' NaN fuer unbekannt.</summary>
        Public Shared Function IlluminantTemperature(code As Integer) As Double
            Select Case code
                Case 3, 17 : Return 2850.0
                Case 24 : Return 3200.0
                Case 23 : Return 5000.0
                Case 1, 4, 9, 18, 20 : Return 5500.0
                Case 10, 19, 21 : Return 6500.0
                Case 11, 22 : Return 7500.0
                Case 12 : Return (5700.0 + 7100.0) * 0.5
                Case 13 : Return (4600.0 + 5500.0) * 0.5
                Case 2, 14 : Return (3800.0 + 4500.0) * 0.5
                Case 15 : Return (3250.0 + 3800.0) * 0.5
                Case 16 : Return (2600.0 + 3250.0) * 0.5
                Case Else : Return Double.NaN
            End Select
        End Function

        ''' <summary>Die Umrechnung fuer eine Aufnahme mit diesen Multiplikatoren (cam_mul, die
        ''' ersten drei Eintraege), oder Nothing, wenn sie nicht brauchbar sind.</summary>
        Public Shared Function Solve(entry As Entry, camMul As Single()) As Solution
            If entry Is Nothing OrElse camMul Is Nothing OrElse camMul.Length < 3 Then Return Nothing
            Dim neutral(2) As Double
            For i = 0 To 2
                If Not (Single.IsFinite(camMul(i)) AndAlso camMul(i) > 0.0F) Then Return Nothing
                neutral(i) = camMul(1) / CDbl(camMul(i))
            Next
            Return SolveNeutral(entry, neutral)
        End Function

        ''' <summary>Dieselbe Rechnung von der neutralen Kameraantwort aus (n = 1/cam_mul).</summary>
        Public Shared Function SolveNeutral(entry As Entry, neutral As Double()) As Solution
            If entry Is Nothing OrElse neutral Is Nothing OrElse neutral.Length < 3 Then Return Nothing

            ' Farbort des Aufnahmeweisses: Matrix fuer den letzten Farbort mischen, Weiss daraus
            ' neu bestimmen, bis es steht. Im letzten Durchgang die Mitte, wie im DNG SDK, damit
            ' ein Pendeln nicht am Rand stehen bleibt.
            Dim lastX = StartX, lastY = StartY
            Dim weight = 0.0
            Dim xyzToCamera As Double() = Nothing
            Dim found = False
            For pass = 0 To MaxPasses - 1
                weight = MixWeight(entry, lastX, lastY)
                xyzToCamera = Mix(entry, weight)
                Dim cameraToXyz = Invert3x3(xyzToCamera)
                If cameraToXyz Is Nothing Then Return Nothing
                Dim white = Apply(cameraToXyz, neutral)
                Dim sum = white(0) + white(1) + white(2)
                If Not (Double.IsFinite(sum) AndAlso sum > 0.0 AndAlso white(1) > 0.0) Then Return Nothing
                Dim nextX = white(0) / sum, nextY = white(1) / sum
                If Math.Abs(nextX - lastX) + Math.Abs(nextY - lastY) < 0.0000001 Then
                    lastX = nextX : lastY = nextY
                    found = True
                    Exit For
                End If
                If pass = MaxPasses - 1 Then
                    nextX = (lastX + nextX) * 0.5
                    nextY = (lastY + nextY) * 0.5
                End If
                lastX = nextX : lastY = nextY
            Next
            If Not found Then
                weight = MixWeight(entry, lastX, lastY)
                xyzToCamera = Mix(entry, weight)
            End If

            Dim inverse = Invert3x3(xyzToCamera)
            If inverse Is Nothing Then Return Nothing
            Dim adapt = BradfordAdaptation(lastX, lastY)
            If adapt Is Nothing Then Return Nothing
            Dim m = Multiply(SrgbFromXyz, Multiply(adapt, inverse))
            For row = 0 To 2
                For column = 0 To 2
                    m(row * 3 + column) *= neutral(column)
                Next
            Next
            Dim rgb(8) As Single
            For row = 0 To 2
                Dim rowSum = m(row * 3) + m(row * 3 + 1) + m(row * 3 + 2)
                If Not (Double.IsFinite(rowSum) AndAlso rowSum > 0.000001) Then Return Nothing
                For column = 0 To 2
                    rgb(row * 3 + column) = CSng(m(row * 3 + column) / rowSum)
                Next
            Next
            Return New Solution With {
                .RgbFromCamera = rgb,
                .WhiteX = lastX,
                .WhiteY = lastY,
                .Kelvin = RobertsonKelvin(lastX, lastY),
                .Weight = weight
            }
        End Function

        ''' <summary>Die Matrix in LINEAREM sRGB (zeilenweise), die ein Bild, das der Decode mit dem
        ''' Aufnahmeweiss (<paramref name="anchorX"/>, <paramref name="anchorY"/>) entwickelt hat, so
        ''' umrechnet, als haette er es mit dem behaupteten Weiss entwickelt. Nothing, wenn sie sich
        ''' nicht bilden laesst. Ohne Luminanz-Normierung; die macht der Aufrufer.
        '''
        ''' <para>WARUM NICHT BLOSS BRADFORD. Die DNG-Spezifikation mischt die Kameramatrix nach dem
        ''' Weissabgleich zwischen ColorMatrix1 und ColorMatrix2. Wird ein anderes Licht behauptet,
        ''' gilt also auch eine andere Matrix. Bradford allein trifft das Weiss, aber nicht die
        ''' bunten Farben: gemessen an einer EOS R6 bei behaupteten 2850 K (Aufnahme 4850 K) lagen
        ''' unscharfe weisse Blueten unter einem gruenen Weissabgleich bei uns auf ProPhoto-Farbton
        ''' 160 bis 180 statt 120 bis 160, und der Farbmischer eines Presets griff dort nicht
        ''' (Diagnostics/Reglereichung/weissmatrix.py).</para>
        '''
        ''' <code>sRGB_neu = S · Bradford(behauptet nach D65) · CM(behauptet)^-1 · CM(Aufnahme) ·
        ''' Bradford(D65 nach Aufnahme) · S^-1 · sRGB_alt</code>
        ''' Mit gleicher Matrix fuer beide Lichter ist das genau die reine Bradford-Adaption vom
        ''' behaupteten Licht auf den Anker.</summary>
        Public Shared Function RebalanceMatrix(entry As Entry, anchorX As Double, anchorY As Double,
                                               claimedX As Double, claimedY As Double) As Double()
            If entry Is Nothing Then Return Nothing
            Dim cameraAnchor = Mix(entry, MixWeight(entry, anchorX, anchorY))
            Dim claimedInverse = Invert3x3(Mix(entry, MixWeight(entry, claimedX, claimedY)))
            Dim toD65 = BradfordAdaptation(claimedX, claimedY)
            Dim anchorToD65 = BradfordAdaptation(anchorX, anchorY)
            If claimedInverse Is Nothing OrElse toD65 Is Nothing OrElse anchorToD65 Is Nothing Then Return Nothing
            Dim fromD65 = Invert3x3(anchorToD65)
            If fromD65 Is Nothing Then Return Nothing
            Return Multiply(SrgbFromXyz, Multiply(toD65, Multiply(claimedInverse,
                   Multiply(cameraAnchor, Multiply(fromD65, XyzFromSrgb)))))
        End Function

        ''' <summary>Das Licht, unter dem ein Bildpunkt neutral wird, nach der DNG-Spezifikation:
        ''' der Bildpunkt (LINEARES sRGB, so wie der Decode ihn mit dem Aufnahmeweiss entwickelt
        ''' hat) zurueck in den Kameraraum, dort als neutrale Kameraantwort genommen und wie ein
        ''' Aufnahmeweiss geloest. Nothing, wenn das nicht geht. Die Umkehrung von
        ''' <see cref="RebalanceMatrix"/> fuer die Pipette.</summary>
        Public Shared Function NeutralWhite(entry As Entry, anchorX As Double, anchorY As Double,
                                            linearSrgb As Double()) As Solution
            If entry Is Nothing OrElse linearSrgb Is Nothing OrElse linearSrgb.Length < 3 Then Return Nothing
            Dim anchorToD65 = BradfordAdaptation(anchorX, anchorY)
            If anchorToD65 Is Nothing Then Return Nothing
            Dim fromD65 = Invert3x3(anchorToD65)
            If fromD65 Is Nothing Then Return Nothing
            Dim cameraAnchor = Mix(entry, MixWeight(entry, anchorX, anchorY))
            Dim camera = Apply(Multiply(cameraAnchor, Multiply(fromD65, XyzFromSrgb)), linearSrgb)
            For i = 0 To 2
                If Not (Double.IsFinite(camera(i)) AndAlso camera(i) > 0.0) Then Return Nothing
            Next
            Return SolveNeutral(entry, camera)
        End Function

        ''' <summary>Anteil der waermeren Matrix: linear in mired zwischen den beiden Lichtarten,
        ''' ausserhalb geklemmt (DNG SDK, FindXYZtoCamera).</summary>
        Private Shared Function MixWeight(entry As Entry, x As Double, y As Double) As Double
            If entry.Matrix2 Is Nothing Then Return 1.0
            Dim kelvin = RobertsonKelvin(x, y)
            If Double.IsNaN(kelvin) Then Return 0.0
            If kelvin <= entry.Temperature1 Then Return 1.0
            If kelvin >= entry.Temperature2 Then Return 0.0
            Return (1.0 / kelvin - 1.0 / entry.Temperature2) / (1.0 / entry.Temperature1 - 1.0 / entry.Temperature2)
        End Function

        Private Shared Function Mix(entry As Entry, weight As Double) As Double()
            If entry.Matrix2 Is Nothing Then Return entry.Matrix1
            Dim result(8) As Double
            For i = 0 To 8
                result(i) = weight * entry.Matrix1(i) + (1.0 - weight) * entry.Matrix2(i)
            Next
            Return result
        End Function

        ''' <summary>Bradford von einem Weiss (x, y) nach D65, in XYZ.</summary>
        Private Shared Function BradfordAdaptation(x As Double, y As Double) As Double()
            If Not (y > 0.0) Then Return Nothing
            Dim source = {x / y, 1.0, (1.0 - x - y) / y}
            Dim coneSource = Apply(Bradford, source)
            Dim coneTarget = Apply(Bradford, D65White)
            Dim scale(8) As Double
            For i = 0 To 2
                If Not (Math.Abs(coneSource(i)) > 0.000001) Then Return Nothing
                scale(i * 4) = coneTarget(i) / coneSource(i)
            Next
            Return Multiply(BradfordInverse, Multiply(scale, Bradford))
        End Function

        ''' <summary>Robertsons Isotemperaturlinien (Wyszecki und Stiles), wie im DNG SDK:
        ''' mired, u, v und die Steigung der Linie.</summary>
        Private Shared ReadOnly RobertsonTable As Double(,) = {
            {0, 0.18006, 0.26352, -0.24341},
            {10, 0.18066, 0.26589, -0.25479},
            {20, 0.18133, 0.26846, -0.26876},
            {30, 0.18208, 0.27119, -0.28539},
            {40, 0.18293, 0.27407, -0.3047},
            {50, 0.18388, 0.27709, -0.32675},
            {60, 0.18494, 0.28021, -0.35156},
            {70, 0.18611, 0.28342, -0.37915},
            {80, 0.1874, 0.28668, -0.40955},
            {90, 0.1888, 0.28997, -0.44278},
            {100, 0.19032, 0.29326, -0.47888},
            {125, 0.19462, 0.30141, -0.58204},
            {150, 0.19962, 0.30921, -0.70471},
            {175, 0.20525, 0.31647, -0.84901},
            {200, 0.21142, 0.32312, -1.0182},
            {225, 0.21807, 0.32909, -1.2168},
            {250, 0.22511, 0.33439, -1.4512},
            {275, 0.23247, 0.33904, -1.7298},
            {300, 0.2401, 0.34308, -2.0637},
            {325, 0.24792, 0.34655, -2.4681},
            {350, 0.25591, 0.34951, -2.9641},
            {375, 0.264, 0.352, -3.5814},
            {400, 0.27218, 0.35407, -4.3633},
            {425, 0.28039, 0.35577, -5.3762},
            {450, 0.28863, 0.35714, -6.7262},
            {475, 0.29685, 0.35823, -8.5955},
            {500, 0.30505, 0.35907, -11.324},
            {525, 0.3132, 0.35968, -15.628},
            {550, 0.32129, 0.36011, -23.325},
            {575, 0.32931, 0.36038, -40.77},
            {600, 0.33724, 0.36051, -116.45}}

        ''' <summary>Farbtemperatur nach Robertson, wie dng_temperature im DNG SDK. NaN, wenn der
        ''' Farbort keinen Wert hergibt.</summary>
        Public Shared Function RobertsonKelvin(x As Double, y As Double) As Double
            Dim denominator = 1.5 - x + 6.0 * y
            If Not (Math.Abs(denominator) > 0.000001) Then Return Double.NaN
            Dim u = 2.0 * x / denominator
            Dim v = 3.0 * y / denominator
            Dim lastDt = 0.0
            For index = 1 To 30
                Dim du = 1.0
                Dim dv = RobertsonTable(index, 3)
                Dim length = Math.Sqrt(1.0 + dv * dv)
                du /= length
                dv /= length
                Dim uu = u - RobertsonTable(index, 1)
                Dim vv = v - RobertsonTable(index, 2)
                Dim dt = -uu * dv + vv * du
                If dt <= 0.0 OrElse index = 30 Then
                    If dt > 0.0 Then dt = 0.0
                    dt = -dt
                    Dim f = If(index = 1, 0.0, dt / (lastDt + dt))
                    Dim mired = RobertsonTable(index - 1, 0) * f + RobertsonTable(index, 0) * (1.0 - f)
                    If Not (mired > 0.0) Then Return 50000.0
                    Return 1000000.0 / mired
                End If
                lastDt = dt
            Next
            Return Double.NaN
        End Function

        ''' <summary>Proben, die die Rechnung von sich aus erfuellen muss. Leer heisst bestanden.
        '''
        ''' 1. Unter D65 mit der D65-Matrix kommt GENAU LibRaws rgb_cam heraus (CameraColorMatrixTable
        '''    rechnet ihn wie LibRaw). Das ist der Anker: fuer Tageslichtbilder aendert sich nichts.
        ''' 2. Das Weiss dieser Aufnahme liegt auf D65, rund 6504 K.
        ''' 3. Unter Normlicht A mit der A-Matrix liegt das Weiss bei Normlicht A (2856 K).
        ''' 4. Jede Zeile summiert sich auf 1, Grau bleibt Grau.</summary>
        Public Shared Function SelfCheck() As List(Of String)
            Dim failures As New List(Of String)()
            If Resource.Count = 0 Then
                failures.Add("Tabelle leer")
                Return failures
            End If
            Dim sample = Resource.Values.FirstOrDefault(Function(e) e.Matrix2 IsNot Nothing AndAlso e.Temperature1 < 3000.0 AndAlso e.Temperature2 > 6000.0)
            If sample Is Nothing Then
                failures.Add("kein Eintrag mit A und D65")
                Return failures
            End If

            Dim neutralD65 = Apply(sample.Matrix2, D65White)
            Dim daylight = SolveNeutral(sample, neutralD65)
            Dim libRaw = CameraColorMatrixTable.FromXyzToCamera(sample.Matrix2)
            If daylight Is Nothing OrElse libRaw Is Nothing Then
                failures.Add("D65: keine Loesung")
            Else
                For i = 0 To 8
                    If Math.Abs(daylight.RgbFromCamera(i) - libRaw.RgbFromCamera(i)) > 0.0005 Then
                        failures.Add($"D65: Glied {i} {daylight.RgbFromCamera(i):F4} statt LibRaws {libRaw.RgbFromCamera(i):F4}")
                        Exit For
                    End If
                Next
                If Math.Abs(daylight.Kelvin - 6504.0) > 5.0 Then failures.Add($"D65: {daylight.Kelvin:F0} K statt 6504")
                If daylight.Weight <> 0.0 Then failures.Add($"D65: Gewicht {daylight.Weight:F3} statt 0")
                CheckRows(daylight, "D65", failures)
            End If

            Dim illuminantA = {1.0985, 1.0, 0.35585}
            Dim tungsten = SolveNeutral(sample, Apply(sample.Matrix1, illuminantA))
            If tungsten Is Nothing Then
                failures.Add("A: keine Loesung")
            Else
                If Math.Abs(tungsten.Kelvin - 2856.0) > 30.0 Then failures.Add($"A: {tungsten.Kelvin:F0} K statt 2856")
                If tungsten.Weight < 0.95 Then failures.Add($"A: Gewicht {tungsten.Weight:F3} statt fast 1")
                CheckRows(tungsten, "A", failures)
            End If
            Return failures
        End Function

        Private Shared Sub CheckRows(solution As Solution, name As String, failures As List(Of String))
            For row = 0 To 2
                Dim sum = solution.RgbFromCamera(row * 3) + solution.RgbFromCamera(row * 3 + 1) + solution.RgbFromCamera(row * 3 + 2)
                If Math.Abs(sum - 1.0F) > 0.0001F Then failures.Add($"{name}: Zeile {row} summiert {sum:F5}")
            Next
        End Sub

        Private Shared Function Apply(m As Double(), v As Double()) As Double()
            Return {
                m(0) * v(0) + m(1) * v(1) + m(2) * v(2),
                m(3) * v(0) + m(4) * v(1) + m(5) * v(2),
                m(6) * v(0) + m(7) * v(1) + m(8) * v(2)}
        End Function

        Private Shared Function Multiply(a As Double(), b As Double()) As Double()
            Dim result(8) As Double
            For row = 0 To 2
                For column = 0 To 2
                    Dim sum = 0.0
                    For k = 0 To 2
                        sum += a(row * 3 + k) * b(k * 3 + column)
                    Next
                    result(row * 3 + column) = sum
                Next
            Next
            Return result
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
