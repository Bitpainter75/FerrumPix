Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Text

Namespace Services
    ''' <summary>Leser für die PostScript-artigen Textdaten in Photoshop-Textebenen.</summary>
    Public NotInheritable Class PsdEngineData
        Public Property FontSize As Double
        Public Property FontName As String = ""
        Public Property FillColor As String = ""
        ''' Zusätzlicher Zeichenabstand in Tausendsteln des Gevierts; NaN, wenn keiner dasteht.
        Public Property Tracking As Double = Double.NaN
        ''' Die Ausrichtung des ersten Absatzes: 0 links, 1 rechts, 2 Mitte, 3 bis 6 Blocksatz;
        ''' -1, wenn keine dasteht.
        Public Property Justification As Integer = -1

        ''' <summary>Die Abbildung aus dem Textraum ins Dokument (xx, xy, yx, yy, tx, ty) und der
        ''' Rahmen im Textraum, beides aus dem TySh-Block (siehe <see cref="PsdTextReader.ExtractStyle"/>).
        ''' Nothing bzw. False, wenn sie fehlen oder nicht lesbar sind.</summary>
        Public Property Matrix As Double()
        Public Property HasBounds As Boolean
        Public Property BoundsLeft As Double
        Public Property BoundsTop As Double
        Public Property BoundsRight As Double
        Public Property BoundsBottom As Double

        Private Sub New()
        End Sub

        Public Shared Function Parse(data As Byte()) As PsdEngineData
            If data Is Nothing OrElse data.Length = 0 OrElse data.Length > 8_000_000 Then Return Nothing
            Try
                Dim parser As New Parser(data)
                Dim root = TryCast(parser.ReadValue(0), Dictionary(Of String, Object))
                If root Is Nothing Then Return Nothing
                Dim engine = Dict(root, "EngineDict")
                Dim run = Dict(engine, "StyleRun")
                Dim runs = Arr(run, "RunArray")
                If runs Is Nothing OrElse runs.Count <> 1 Then Return Nothing
                Dim style = Dict(Dict(TryCast(runs(0), Dictionary(Of String, Object)), "StyleSheet"), "StyleSheetData")
                If style Is Nothing Then Return Nothing
                Dim result As New PsdEngineData()
                Dim size = Number(style, "FontSize")
                If size > 0 AndAlso size <= 100_000 Then result.FontSize = size
                If style.ContainsKey("Tracking") AndAlso TypeOf style("Tracking") Is Double Then
                    Dim tracking = CDbl(style("Tracking"))
                    If Double.IsFinite(tracking) AndAlso Math.Abs(tracking) <= 10_000 Then result.Tracking = tracking
                End If
                Dim paragraphs = Arr(Dict(engine, "ParagraphRun"), "RunArray")
                If paragraphs IsNot Nothing AndAlso paragraphs.Count > 0 Then
                    Dim properties = Dict(Dict(TryCast(paragraphs(0), Dictionary(Of String, Object)), "ParagraphSheet"), "Properties")
                    Dim justification = Number(properties, "Justification")
                    If justification >= 0 AndAlso justification <= 6 AndAlso justification = Math.Truncate(justification) Then
                        result.Justification = CInt(justification)
                    End If
                End If
                Dim index = Number(style, "Font")
                Dim fonts = Arr(Dict(root, "ResourceDict"), "FontSet")
                If fonts IsNot Nothing AndAlso index >= 0 AndAlso index = Math.Truncate(index) AndAlso index < fonts.Count Then
                    Dim font = TryCast(fonts(CInt(index)), Dictionary(Of String, Object))
                    If font IsNot Nothing AndAlso font.ContainsKey("Name") Then result.FontName = TryCast(font("Name"), String)
                End If
                Dim values = Arr(Dict(style, "FillColor"), "Values")
                If values IsNot Nothing AndAlso values.Count = 4 Then
                    Dim c(3) As Integer
                    For i = 0 To 3
                        If Not TypeOf values(i) Is Double Then Return result
                        Dim v = CDbl(values(i))
                        If Double.IsNaN(v) OrElse v < 0 OrElse v > 1 Then Return result
                        c(i) = CInt(Math.Round(v * 255))
                    Next
                    result.FillColor = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c(0), c(1), c(2), c(3))
                End If
                Return result
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function Dict(d As Dictionary(Of String, Object), key As String) As Dictionary(Of String, Object)
            If d Is Nothing OrElse Not d.ContainsKey(key) Then Return Nothing
            Return TryCast(d(key), Dictionary(Of String, Object))
        End Function
        Private Shared Function Arr(d As Dictionary(Of String, Object), key As String) As List(Of Object)
            If d Is Nothing OrElse Not d.ContainsKey(key) Then Return Nothing
            Return TryCast(d(key), List(Of Object))
        End Function
        Private Shared Function Number(d As Dictionary(Of String, Object), key As String) As Double
            If d Is Nothing OrElse Not d.ContainsKey(key) OrElse Not TypeOf d(key) Is Double Then Return -1
            Return CDbl(d(key))
        End Function

        Private NotInheritable Class Parser
            Private ReadOnly bytes As Byte()
            Private pos As Integer
            Private count As Integer
            Public Sub New(data As Byte())
                bytes = data
            End Sub
            Private Sub Space()
                While pos < bytes.Length
                    If bytes(pos) = 37 Then
                        While pos < bytes.Length AndAlso bytes(pos) <> 10 AndAlso bytes(pos) <> 13
                            pos += 1
                        End While
                    ElseIf bytes(pos) <= 32 Then
                        pos += 1
                    Else
                        Exit While
                    End If
                End While
            End Sub
            Public Function ReadValue(depth As Integer) As Object
                count += 1
                If depth > 32 OrElse count > 100_000 Then Throw New FormatException()
                Space()
                If pos >= bytes.Length Then Throw New FormatException()
                If bytes(pos) = 60 AndAlso pos + 1 < bytes.Length AndAlso bytes(pos + 1) = 60 Then
                    pos += 2
                    Dim d As New Dictionary(Of String, Object)(StringComparer.Ordinal)
                    Do
                        Space()
                        If pos + 1 < bytes.Length AndAlso bytes(pos) = 62 AndAlso bytes(pos + 1) = 62 Then
                            pos += 2
                            Return d
                        End If
                        If pos >= bytes.Length OrElse bytes(pos) <> 47 Then Throw New FormatException()
                        pos += 1
                        Dim key = Token()
                        d(key) = ReadValue(depth + 1)
                    Loop
                End If
                If bytes(pos) = 91 Then
                    pos += 1
                    Dim a As New List(Of Object)()
                    Do
                        Space()
                        If pos >= bytes.Length Then Throw New FormatException()
                        If bytes(pos) = 93 Then
                            pos += 1
                            Return a
                        End If
                        a.Add(ReadValue(depth + 1))
                    Loop
                End If
                If bytes(pos) = 40 Then Return ReadString()
                If bytes(pos) = 47 Then pos += 1
                Dim word = Token()
                Dim number As Double
                If Double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, number) Then Return number
                Return word
            End Function
            Private Function Token() As String
                Dim start = pos
                While pos < bytes.Length AndAlso bytes(pos) > 32 AndAlso Array.IndexOf(New Byte() {47, 60, 62, 91, 93, 40, 41, 37}, bytes(pos)) < 0
                    pos += 1
                End While
                If pos = start Then Throw New FormatException()
                Return Encoding.ASCII.GetString(bytes, start, pos - start)
            End Function
            Private Function ReadString() As String
                pos += 1
                Dim result As New List(Of Byte)()
                Dim nesting = 1
                While pos < bytes.Length
                    Dim b = bytes(pos) : pos += 1
                    If b = 92 Then
                        If pos >= bytes.Length Then Throw New FormatException()
                        b = bytes(pos) : pos += 1
                    ElseIf b = 40 Then
                        nesting += 1
                    ElseIf b = 41 Then
                        nesting -= 1
                        If nesting = 0 Then
                            Dim raw = result.ToArray()
                            If raw.Length >= 2 AndAlso raw(0) = 254 AndAlso raw(1) = 255 Then Return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2).TrimEnd(ChrW(0))
                            Return Encoding.UTF8.GetString(raw)
                        End If
                    End If
                    result.Add(b)
                End While
                Throw New FormatException()
            End Function
        End Class
    End Class
End Namespace
