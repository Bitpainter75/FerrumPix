Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace Services

    ''' <summary>Der Matrixteil eines ICC-Bildschirmprofils, selbst gelesen: die Primärfarben
    ''' (<c>rXYZ</c>, <c>gXYZ</c>, <c>bXYZ</c>, im Profil schon auf D50 angepasst) und je Kanal die
    ''' Tonkurve (<c>rTRC</c>, <c>gTRC</c>, <c>bTRC</c>) als Tabelle oder Formel.
    '''
    ''' <para>WARUM: Skia nimmt ein Profil nur an, wenn es die drei Tonkurven als EINE gemeinsame
    ''' Formel darstellen kann. Ein gemessenes Profil (DisplayCAL, ArgyllCMS) trägt je Kanal eine
    ''' eigene Tabelle und fällt dort durch, schon eine reine Gamma-Tabelle tut das. Gemessen mit
    ''' <c>Diagnostics/IccProbe</c>, siehe <see cref="DisplayProfileService"/>.</para>
    '''
    ''' <para>Die Tabellen für das eigentliche Farbmanagement (<c>A2B0</c> und Verwandte) bleiben
    ''' ungenutzt: DisplayCAL legt die Matrix ausdrücklich für Programme mit hinein, die nur sie
    ''' verwenden („XYZLUT+MTX“).</para></summary>
    Public NotInheritable Class IccMatrixProfile

        ''' <summary>Gerät nach XYZ D50, Zeile X, Y, Z, Spalte Rot, Grün, Blau.</summary>
        Public ReadOnly Property ToXyzD50 As Double(,)

        ''' <summary>Die Tonkurven Rot, Grün, Blau: Gerätewert nach linearem Licht, beide 0 bis 1.</summary>
        Public ReadOnly Property Curves As IccToneCurve()

        Private Sub New(matrix As Double(,), curves As IccToneCurve())
            ToXyzD50 = matrix
            Me.Curves = curves
        End Sub

        Public Enum ParseResult
            Ok
            NotAnIccProfile
            NoMatrixWithCurves
        End Enum

        ''' <summary>Liest den Matrixteil. <see cref="ParseResult.NotAnIccProfile"/> heißt: kein
        ''' RGB-Profil oder kaputte Datei; <see cref="ParseResult.NoMatrixWithCurves"/>: ein
        ''' gültiges Profil ohne vollständigen Matrixteil (reines Tabellenprofil).</summary>
        Public Shared Function TryParse(data As Byte(), ByRef profile As IccMatrixProfile) As ParseResult
            profile = Nothing
            If data Is Nothing OrElse data.Length < 132 Then Return ParseResult.NotAnIccProfile
            If Encoding.ASCII.GetString(data, 36, 4) <> "acsp" Then Return ParseResult.NotAnIccProfile
            If Encoding.ASCII.GetString(data, 16, 4) <> "RGB " Then Return ParseResult.NotAnIccProfile

            Dim tags As New Dictionary(Of String, (Offset As Integer, Length As Integer))(StringComparer.Ordinal)
            Dim count = ReadUInt32(data, 128)
            If count > 1000 OrElse 132L + count * 12L > data.Length Then Return ParseResult.NotAnIccProfile
            For i = 0 To CInt(count) - 1
                Dim p = 132 + i * 12
                Dim offset = ReadUInt32(data, p + 4)
                Dim length = ReadUInt32(data, p + 8)
                If offset + CLng(length) > data.Length Then Continue For
                tags(Encoding.ASCII.GetString(data, p, 4)) = (CInt(offset), CInt(length))
            Next

            Dim matrix(2, 2) As Double
            Dim columns = {"rXYZ", "gXYZ", "bXYZ"}
            For column = 0 To 2
                Dim xyz As Double() = Nothing
                If Not TryReadXyz(data, tags, columns(column), xyz) Then Return ParseResult.NoMatrixWithCurves
                For row = 0 To 2
                    matrix(row, column) = xyz(row)
                Next
            Next

            Dim curves(2) As IccToneCurve
            Dim names = {"rTRC", "gTRC", "bTRC"}
            For channel = 0 To 2
                Dim tag As (Offset As Integer, Length As Integer)
                If Not tags.TryGetValue(names(channel), tag) Then Return ParseResult.NoMatrixWithCurves
                curves(channel) = IccToneCurve.TryRead(data, tag.Offset, tag.Length)
                If curves(channel) Is Nothing Then Return ParseResult.NoMatrixWithCurves
            Next

            profile = New IccMatrixProfile(matrix, curves)
            Return ParseResult.Ok
        End Function

        Private Shared Function TryReadXyz(data As Byte(), tags As Dictionary(Of String, (Offset As Integer, Length As Integer)),
                                           name As String, ByRef xyz As Double()) As Boolean
            Dim tag As (Offset As Integer, Length As Integer)
            If Not tags.TryGetValue(name, tag) OrElse tag.Length < 20 Then Return False
            If Encoding.ASCII.GetString(data, tag.Offset, 4) <> "XYZ " Then Return False
            xyz = {ReadS15Fixed16(data, tag.Offset + 8), ReadS15Fixed16(data, tag.Offset + 12), ReadS15Fixed16(data, tag.Offset + 16)}
            Return True
        End Function

        Friend Shared Function ReadUInt32(data As Byte(), p As Integer) As UInteger
            Return (CUInt(data(p)) << 24) Or (CUInt(data(p + 1)) << 16) Or (CUInt(data(p + 2)) << 8) Or CUInt(data(p + 3))
        End Function

        Friend Shared Function ReadUInt16(data As Byte(), p As Integer) As Integer
            Return (CInt(data(p)) << 8) Or CInt(data(p + 1))
        End Function

        Friend Shared Function ReadS15Fixed16(data As Byte(), p As Integer) As Double
            Dim unsigned = CLng(ReadUInt32(data, p))
            Return If(unsigned >= 2147483648L, unsigned - 4294967296L, unsigned) / 65536.0
        End Function

    End Class

    ''' <summary>Eine Tonkurve aus einem ICC-Profil: <c>curv</c> (Identität, Gamma oder Tabelle)
    ''' oder <c>para</c> (die fünf Formeltypen der Norm). Abgebildet wird Gerätewert auf lineares
    ''' Licht, beides 0 bis 1.</summary>
    Public NotInheritable Class IccToneCurve

        Private ReadOnly _table As Double()
        Private ReadOnly _gamma As Double
        Private ReadOnly _parameters As Double()
        Private ReadOnly _functionType As Integer

        Private Sub New(table As Double(), gamma As Double, functionType As Integer, parameters As Double())
            _table = table
            _gamma = gamma
            _functionType = functionType
            _parameters = parameters
        End Sub

        Friend Shared Function TryRead(data As Byte(), offset As Integer, length As Integer) As IccToneCurve
            If length < 12 Then Return Nothing
            Dim type = Encoding.ASCII.GetString(data, offset, 4)
            If type = "curv" Then
                Dim count = CLng(IccMatrixProfile.ReadUInt32(data, offset + 8))
                If 12L + count * 2L > length Then Return Nothing
                If count = 0 Then Return New IccToneCurve(Nothing, 1.0, -1, Nothing)
                If count = 1 Then Return New IccToneCurve(Nothing, IccMatrixProfile.ReadUInt16(data, offset + 12) / 256.0, -1, Nothing)
                Dim table(CInt(count) - 1) As Double
                For i = 0 To table.Length - 1
                    table(i) = IccMatrixProfile.ReadUInt16(data, offset + 12 + i * 2) / 65535.0
                Next
                Return New IccToneCurve(table, 1.0, -1, Nothing)
            End If
            If type = "para" Then
                Dim functionType = IccMatrixProfile.ReadUInt16(data, offset + 8)
                Dim needed = {1, 3, 4, 5, 7}
                If functionType < 0 OrElse functionType > 4 Then Return Nothing
                Dim n = needed(functionType)
                If 12 + n * 4 > length Then Return Nothing
                Dim parameters(n - 1) As Double
                For i = 0 To n - 1
                    parameters(i) = IccMatrixProfile.ReadS15Fixed16(data, offset + 12 + i * 4)
                Next
                Return New IccToneCurve(Nothing, 1.0, functionType, parameters)
            End If
            Return Nothing
        End Function

        ''' <summary>Gerätewert nach linearem Licht. Tabellen werden linear zwischen ihren
        ''' Stützstellen gelesen, wie es die Norm vorsieht.</summary>
        Public Function Evaluate(x As Double) As Double
            x = Math.Max(0.0, Math.Min(1.0, x))
            If _table IsNot Nothing Then
                Dim position = x * (_table.Length - 1)
                Dim index = CInt(Math.Floor(position))
                If index >= _table.Length - 1 Then Return _table(_table.Length - 1)
                Dim t = position - index
                Return _table(index) * (1 - t) + _table(index + 1) * t
            End If
            If _parameters Is Nothing Then Return Math.Pow(x, _gamma)
            Dim p = _parameters
            Dim g = p(0)
            Select Case _functionType
                Case 0
                    Return Math.Pow(x, g)
                Case 1
                    Return If(x >= -p(2) / p(1), Math.Pow(Math.Max(0.0, p(1) * x + p(2)), g), 0.0)
                Case 2
                    Return If(x >= -p(2) / p(1), Math.Pow(Math.Max(0.0, p(1) * x + p(2)), g) + p(3), p(3))
                Case 3
                    Return If(x >= p(4), Math.Pow(Math.Max(0.0, p(1) * x + p(2)), g), p(3) * x)
                Case Else
                    Return If(x >= p(4), Math.Pow(Math.Max(0.0, p(1) * x + p(2)), g) + p(5), p(3) * x + p(6))
            End Select
        End Function

        ''' <summary>Die Umkehrung: zu linearem Licht der Gerätewert. Halbierung über
        ''' <see cref="Evaluate"/>; eine gemessene Kurve steigt, kann aber stellenweise flach sein,
        ''' dann wird der kleinste passende Wert gefunden.</summary>
        Public Function Invert(y As Double) As Double
            y = Math.Max(0.0, Math.Min(1.0, y))
            Dim low = 0.0
            Dim high = 1.0
            If Evaluate(high) <= y Then Return 1.0
            If Evaluate(low) >= y Then Return 0.0
            For iteration = 1 To 40
                Dim middle = (low + high) / 2
                If Evaluate(middle) < y Then low = middle Else high = middle
            Next
            Return (low + high) / 2
        End Function

    End Class

End Namespace
