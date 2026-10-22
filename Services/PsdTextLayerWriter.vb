Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Baut den Zusatzblock "TySh", mit dem eine Ebene in einer PSD zur Textebene wird. Das
    ''' Gegenstück zu <see cref="PsdTextReader"/>.
    '''
    ''' Der Block beschreibt den Text, die Bildpunkte bleiben trotzdem in der Ebene: ein Leser ohne
    ''' eigene Textmaschine zeigt sie, und auch Photoshop zeigt sie, bis der Text bearbeitet wird.
    '''
    ''' Aufbau: Version 1 (u16), die Matrix aus sechs f64 (xx, xy, yx, yy, tx, ty), Textversion 50
    ''' (u16), eine Beschreibungsstruktur "TxLr" mit Version 16, Verformungsversion 1 (u16), die
    ''' Struktur "warp" mit Version 16 und am Ende vier f32 (ohne Verformung Nullen).
    '''
    ''' Geschrieben wird PUNKTTEXT: der Ursprung des Textraums liegt auf der Grundlinie der ersten
    ''' Zeile, waagerecht an der Ausrichtung (links, Mitte oder rechts). Das passt zum eigenen
    ''' Renderer, der nur an ausdrücklichen Zeilenenden umbricht; ein Absatztext mit Rahmen würde
    ''' bei Photoshop selbst umbrechen und die erste Grundlinie nach der Oberlänge der Schrift
    ''' setzen statt nach dem Schriftgrad.
    '''
    ''' Die Bauform der Textdaten (EngineData) folgt dem, was Photoshop selbst schreibt: eine
    ''' PostScript-artige Schreibweise mit Tabs, ein Schlüssel je Zeile, Zeichenketten als UTF-16
    ''' Big-Endian mit vorangestelltem FE FF.
    ''' </summary>
    Public NotInheritable Class PsdTextLayerWriter

        Private Sub New()
        End Sub

        ''' <summary>Was in den Block gehört. Alle Längen in Bildpunkten des Dokuments.</summary>
        Public Class TextLayerSpec
            ''' Der Wortlaut, Zeilen mit LF getrennt.
            Public Property Text As String = ""
            ''' Der PostScript-Name des Schriftschnitts, so wie Photoshop ihn in der Schriftliste führt.
            Public Property FontPostScriptName As String = ""
            Public Property FontSize As Double
            ''' Abstand zweier Grundlinien.
            Public Property Leading As Double
            ''' Zusätzlicher Zeichenabstand in Tausendsteln des Gevierts.
            Public Property Tracking As Integer
            ''' 0 links, 1 rechts, 2 Mitte.
            Public Property Justification As Integer
            Public Property ColorA As Byte = 255
            Public Property ColorR As Byte
            Public Property ColorG As Byte
            Public Property ColorB As Byte
            ''' Die Abbildung aus dem Textraum ins Dokument: x' = xx x + yx y + tx, y' = xy x + yy y + ty.
            Public Property Matrix As Double() = {1, 0, 0, 1, 0, 0}
            ''' Der Rahmen des Objekts im Textraum, also relativ zum Ursprung auf der Grundlinie.
            Public Property BoundsLeft As Double
            Public Property BoundsTop As Double
            Public Property BoundsRight As Double
            Public Property BoundsBottom As Double
            ''' Die Nummer des Textobjekts. Jede Textebene der Datei braucht eine eigene, sonst
            ''' beanspruchen alle das Objekt 0 im globalen Textblock eines Programms, das einen führt.
            Public Property TextIndex As Integer
        End Class

        Public Shared Function Build(spec As TextLayerSpec) As Byte()
            If spec Is Nothing OrElse String.IsNullOrEmpty(spec.Text) Then Return Nothing
            If spec.Matrix Is Nothing OrElse spec.Matrix.Length <> 6 Then Return Nothing
            For Each value In spec.Matrix
                If Not Double.IsFinite(value) Then Return Nothing
            Next

            ' Photoshop schliesst jede Zeile mit einem Wagenruecklauf ab.
            Dim lines = spec.Text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
            Dim text = String.Join(vbCr, lines)

            Using ms As New MemoryStream()
                WriteU16(ms, 1)
                For Each value In spec.Matrix
                    WriteDouble(ms, value)
                Next
                WriteU16(ms, 50)
                WriteU32(ms, 16)
                WriteTextDescriptor(ms, spec, text, lines)
                WriteU16(ms, 1)
                WriteU32(ms, 16)
                WriteWarpDescriptor(ms)
                For i = 1 To 4
                    WriteU32(ms, 0)   ' vier f32 Null: keine Verformung
                Next
                Return ms.ToArray()
            End Using
        End Function

        ' ── Die Beschreibungsstrukturen ──────────────────────────────────────────

        Private Shared Sub WriteTextDescriptor(ms As Stream, spec As TextLayerSpec, text As String, lines As String())
            BeginDescriptor(ms, "TxLr", 8)

            WriteKey(ms, "Txt ")
            WriteType(ms, "TEXT")
            WriteUnicodeString(ms, text, withNull:=True)

            WriteKey(ms, "textGridding")
            WriteEnum(ms, "textGridding", "None")

            WriteKey(ms, "Ornt")
            WriteEnum(ms, "Ornt", "Hrzn")

            WriteKey(ms, "AntA")
            WriteEnum(ms, "Annt", "AnSm")

            WriteKey(ms, "bounds")
            WriteBounds(ms, "bounds", spec)

            WriteKey(ms, "boundingBox")
            WriteBounds(ms, "boundingBox", spec)

            WriteKey(ms, "TextIndex")
            WriteType(ms, "long")
            WriteU32(ms, spec.TextIndex)

            WriteKey(ms, "EngineData")
            WriteType(ms, "tdta")
            Dim engine = BuildEngineData(spec, text, lines)
            WriteU32(ms, engine.Length)
            ms.Write(engine, 0, engine.Length)
        End Sub

        Private Shared Sub WriteBounds(ms As Stream, classId As String, spec As TextLayerSpec)
            WriteType(ms, "Objc")
            BeginDescriptor(ms, classId, 4)
            For Each item In {("Left", spec.BoundsLeft), ("Top ", spec.BoundsTop),
                              ("Rght", spec.BoundsRight), ("Btom", spec.BoundsBottom)}
                WriteKey(ms, item.Item1)
                WriteType(ms, "UntF")
                WriteAscii(ms, "#Pnt")
                WriteDouble(ms, item.Item2)
            Next
        End Sub

        ''' <summary>Die Verformung "keine". Der Klassenname "warp" steht als Zeichenkette mit
        ''' Länge 4, nicht als Kürzel: Photoshop hält beides für verschiedene Namen.</summary>
        Private Shared Sub WriteWarpDescriptor(ms As Stream)
            WriteUnicodeString(ms, "", withNull:=False)
            WriteU32(ms, 4)
            WriteAscii(ms, "warp")
            WriteU32(ms, 5)

            WriteKey(ms, "warpStyle")
            WriteEnum(ms, "warpStyle", "warpNone")
            WriteKey(ms, "warpValue")
            WriteType(ms, "doub")
            WriteDouble(ms, 0)
            WriteKey(ms, "warpPerspective")
            WriteType(ms, "doub")
            WriteDouble(ms, 0)
            WriteKey(ms, "warpPerspectiveOther")
            WriteType(ms, "doub")
            WriteDouble(ms, 0)
            WriteKey(ms, "warpRotate")
            WriteEnum(ms, "Ornt", "Hrzn")
        End Sub

        ''' Leerer Name, Klasse, Zahl der Einträge.
        Private Shared Sub BeginDescriptor(ms As Stream, classId As String, count As Integer)
            WriteUnicodeString(ms, "", withNull:=False)
            WriteKey(ms, classId)
            WriteU32(ms, count)
        End Sub

        Private Shared Sub WriteEnum(ms As Stream, typeId As String, value As String)
            WriteType(ms, "enum")
            WriteKey(ms, typeId)
            WriteKey(ms, value)
        End Sub

        ''' Ein Schlüssel oder Klassenname: aus genau vier Zeichen als Länge 0 und das Kürzel,
        ''' sonst Länge und Zeichen.
        Private Shared Sub WriteKey(ms As Stream, key As String)
            WriteU32(ms, If(key.Length = 4, 0, key.Length))
            WriteAscii(ms, key)
        End Sub

        Private Shared Sub WriteType(ms As Stream, osType As String)
            WriteAscii(ms, osType)
        End Sub

        ''' Länge in Zeichen, danach UTF-16 Big-Endian.
        Private Shared Sub WriteUnicodeString(ms As Stream, value As String, withNull As Boolean)
            Dim text = If(value, "")
            WriteU32(ms, text.Length + If(withNull, 1, 0))
            For Each ch In text
                WriteU16(ms, AscW(ch) And &HFFFF)
            Next
            If withNull Then WriteU16(ms, 0)
        End Sub

        ' ── EngineData ───────────────────────────────────────────────────────────

        ''' <summary>Die Textdaten: Wortlaut, ein Stillauf über alles, ein Absatz je Zeile, die
        ''' Darstellungsform Punkttext und die Schriftliste.</summary>
        Private Shared Function BuildEngineData(spec As TextLayerSpec, text As String, lines As String()) As Byte()
            ' Photoshop zählt hinter der letzten Zeile noch einen Absatzumbruch mit.
            Dim engineText = text & vbCr
            Dim paragraphs As New List(Of Object)()
            Dim paragraphLengths As New List(Of Object)()
            For i = 0 To lines.Length - 1
                paragraphs.Add(Dict(("ParagraphSheet", Dict(
                    ("DefaultStyleSheet", CObj(0)),
                    ("Properties", ParagraphProperties(spec.Justification))))))
                paragraphLengths.Add(CObj(lines(i).Length + 1))
            Next

            Dim style = StyleSheetData(spec)
            Dim engineDict = Dict(
                ("Editor", Dict(("Text", CObj(New EngineString(engineText))))),
                ("ParagraphRun", Dict(
                    ("RunArray", CObj(paragraphs)),
                    ("RunLengthArray", CObj(paragraphLengths)),
                    ("IsJoinable", CObj(1)))),
                ("StyleRun", Dict(
                    ("RunArray", CObj(New List(Of Object) From {
                        Dict(("StyleSheet", Dict(("StyleSheetData", style))))})),
                    ("RunLengthArray", CObj(New List(Of Object) From {CObj(engineText.Length)})),
                    ("IsJoinable", CObj(2)))),
                ("AntiAlias", CObj(3)),
                ("UseFractionalGlyphWidths", CObj(True)),
                ("Rendered", Dict(
                    ("Version", CObj(1)),
                    ("Shapes", Dict(
                        ("WritingDirection", CObj(0)),
                        ("Children", CObj(New List(Of Object) From {
                            Dict(("ShapeType", CObj(0)),
                                 ("Procession", CObj(0)),
                                 ("Lines", Dict(("WritingDirection", CObj(0)),
                                                ("Children", CObj(New List(Of Object)())))),
                                 ("Cookie", Dict(("Photoshop", Dict(
                                     ("ShapeType", CObj(0)),
                                     ("PointBase", CObj(New List(Of Object) From {CObj(0.0), CObj(0.0)})),
                                     ("Base", Dict(
                                         ("ShapeType", CObj(0)),
                                         ("TransformPoint0", CObj(New List(Of Object) From {CObj(1.0), CObj(0.0)})),
                                         ("TransformPoint1", CObj(New List(Of Object) From {CObj(0.0), CObj(1.0)})),
                                         ("TransformPoint2", CObj(New List(Of Object) From {CObj(0.0), CObj(0.0)})))))))))
                        })))))))

            Dim root = Dict(
                ("EngineDict", engineDict),
                ("ResourceDict", ResourceDict(spec, style)),
                ("DocumentResources", ResourceDict(spec, style)))

            Using ms As New MemoryStream()
                WriteAscii(ms, vbLf & vbLf)
                WriteEngineValue(ms, root, 0)
                WriteAscii(ms, vbLf)
                Return ms.ToArray()
            End Using
        End Function

        Private Shared Function StyleSheetData(spec As TextLayerSpec) As List(Of (String, Object))
            Dim values As New List(Of Object) From {
                CObj(spec.ColorA / 255.0), CObj(spec.ColorR / 255.0), CObj(spec.ColorG / 255.0), CObj(spec.ColorB / 255.0)}
            ' Den Zeilenabstand ausdrücklich: der eigene Renderer nimmt ihn aus der Schrift, die
            ' Vorgabe von Photoshop wären 120 Prozent des Grads.
            Return Dict(
                ("Font", CObj(0)),
                ("FontSize", CObj(spec.FontSize)),
                ("FauxBold", CObj(False)),
                ("FauxItalic", CObj(False)),
                ("AutoLeading", CObj(False)),
                ("Leading", CObj(spec.Leading)),
                ("HorizontalScale", CObj(1.0)),
                ("VerticalScale", CObj(1.0)),
                ("Tracking", CObj(spec.Tracking)),
                ("AutoKerning", CObj(True)),
                ("Kerning", CObj(0)),
                ("BaselineShift", CObj(0.0)),
                ("FontCaps", CObj(0)),
                ("FontBaseline", CObj(0)),
                ("Underline", CObj(False)),
                ("Strikethrough", CObj(False)),
                ("Ligatures", CObj(True)),
                ("DLigatures", CObj(False)),
                ("FillFlag", CObj(True)),
                ("FillColor", Dict(("Type", CObj(1)), ("Values", CObj(values)))))
        End Function

        Private Shared Function ParagraphProperties(justification As Integer) As List(Of (String, Object))
            Return Dict(
                ("Justification", CObj(justification)),
                ("FirstLineIndent", CObj(0.0)),
                ("StartIndent", CObj(0.0)),
                ("EndIndent", CObj(0.0)),
                ("SpaceBefore", CObj(0.0)),
                ("SpaceAfter", CObj(0.0)),
                ("AutoHyphenate", CObj(False)),
                ("AutoLeading", CObj(1.2)),
                ("LeadingType", CObj(0)))
        End Function

        Private Shared Function ResourceDict(spec As TextLayerSpec, style As List(Of (String, Object))) As List(Of (String, Object))
            Return Dict(
                ("FontSet", CObj(New List(Of Object) From {
                    Dict(("Name", CObj(New EngineString(spec.FontPostScriptName))),
                         ("Script", CObj(0)),
                         ("FontType", CObj(1)),
                         ("Synthetic", CObj(0)))})),
                ("StyleSheetSet", CObj(New List(Of Object) From {
                    Dict(("Name", CObj(New EngineString("Normal RGB"))),
                         ("StyleSheetData", style))})),
                ("ParagraphSheetSet", CObj(New List(Of Object) From {
                    Dict(("Name", CObj(New EngineString("Normal RGB"))),
                         ("DefaultStyleSheet", CObj(0)),
                         ("Properties", ParagraphProperties(0)))})),
                ("TheNormalStyleSheet", CObj(0)),
                ("TheNormalParagraphSheet", CObj(0)))
        End Function

        ''' Eine Zeichenkette der Textdaten; ein gewöhnlicher String wäre dort ein Name.
        Private NotInheritable Class EngineString
            Public ReadOnly Value As String
            Public Sub New(value As String)
                Me.Value = If(value, "")
            End Sub
        End Class

        ''' Ein Wörterbuch der Textdaten in fester Reihenfolge.
        Private Shared Function Dict(ParamArray items As (String, Object)()) As List(Of (String, Object))
            Return New List(Of (String, Object))(items)
        End Function

        Private Shared Sub WriteEngineValue(ms As Stream, value As Object, depth As Integer)
            Dim dictionary = TryCast(value, List(Of (String, Object)))
            If dictionary IsNot Nothing Then
                WriteAscii(ms, "<<" & vbLf)
                For Each item In dictionary
                    Indent(ms, depth + 1)
                    WriteAscii(ms, "/" & item.Item1)
                    If IsContainer(item.Item2) Then
                        WriteAscii(ms, vbLf)
                        Indent(ms, depth + 1)
                    Else
                        WriteAscii(ms, " ")
                    End If
                    WriteEngineValue(ms, item.Item2, depth + 1)
                    WriteAscii(ms, vbLf)
                Next
                Indent(ms, depth)
                WriteAscii(ms, ">>")
                Return
            End If

            Dim array = TryCast(value, List(Of Object))
            If array IsNot Nothing Then
                If IsContainer(array) Then
                    WriteAscii(ms, "[" & vbLf)
                    For Each element In array
                        Indent(ms, depth)
                        WriteEngineValue(ms, element, depth)
                        WriteAscii(ms, vbLf)
                    Next
                    Indent(ms, depth)
                    WriteAscii(ms, "]")
                Else
                    WriteAscii(ms, "[")
                    For Each element In array
                        WriteAscii(ms, " ")
                        WriteEngineValue(ms, element, depth)
                    Next
                    WriteAscii(ms, " ]")
                End If
                Return
            End If

            Dim engineString = TryCast(value, EngineString)
            If engineString IsNot Nothing Then
                ' Erst kodieren, dann maskieren: der Rückstrich steht vor jedem BYTE, das eine
                ' Klammer oder ein Rückstrich ist, auch mitten in einem UTF-16-Zeichen.
                Dim raw As New List(Of Byte) From {&HFE, &HFF}
                raw.AddRange(Encoding.BigEndianUnicode.GetBytes(engineString.Value))
                ms.WriteByte(40)
                For Each b In raw
                    If b = 40 OrElse b = 41 OrElse b = 92 Then ms.WriteByte(92)
                    ms.WriteByte(b)
                Next
                ms.WriteByte(41)
                Return
            End If

            If TypeOf value Is Boolean Then
                WriteAscii(ms, If(CBool(value), "true", "false"))
            ElseIf TypeOf value Is Integer Then
                WriteAscii(ms, CInt(value).ToString(CultureInfo.InvariantCulture))
            ElseIf TypeOf value Is Double Then
                WriteAscii(ms, FormatReal(CDbl(value)))
            Else
                Throw New ArgumentException(NameOf(value))
            End If
        End Sub

        Private Shared Function IsContainer(value As Object) As Boolean
            If TypeOf value Is List(Of (String, Object)) Then Return True
            Dim array = TryCast(value, List(Of Object))
            If array Is Nothing Then Return False
            For Each element In array
                If TypeOf element Is List(Of (String, Object)) OrElse TypeOf element Is List(Of Object) Then Return True
            Next
            Return False
        End Function

        ''' Photoshops Schreibweise: "1.0", ".5", "-.25".
        Private Shared Function FormatReal(value As Double) As String
            If Not Double.IsFinite(value) Then Return "0.0"
            If value = Math.Truncate(value) AndAlso Math.Abs(value) < 1.0E+15 Then
                Return value.ToString("0.0", CultureInfo.InvariantCulture)
            End If
            Dim text = value.ToString("0.#####", CultureInfo.InvariantCulture)
            If text = "0" OrElse text = "-0" Then Return "0.0"
            If text.StartsWith("0.", StringComparison.Ordinal) Then Return text.Substring(1)
            If text.StartsWith("-0.", StringComparison.Ordinal) Then Return "-" & text.Substring(2)
            Return text
        End Function

        Private Shared Sub Indent(ms As Stream, depth As Integer)
            For i = 1 To depth
                ms.WriteByte(9)
            Next
        End Sub

        ' ── Schreib-Helfer ───────────────────────────────────────────────────────

        Private Shared Sub WriteAscii(ms As Stream, text As String)
            Dim bytes = Encoding.ASCII.GetBytes(text)
            ms.Write(bytes, 0, bytes.Length)
        End Sub

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und lieferte still 0.
        Private Shared Sub WriteU16(ms As Stream, value As Integer)
            ms.WriteByte(CByte((value >> 8) And &HFF))
            ms.WriteByte(CByte(value And &HFF))
        End Sub

        Private Shared Sub WriteU32(ms As Stream, value As Long)
            ms.WriteByte(CByte((value >> 24) And &HFF))
            ms.WriteByte(CByte((value >> 16) And &HFF))
            ms.WriteByte(CByte((value >> 8) And &HFF))
            ms.WriteByte(CByte(value And &HFF))
        End Sub

        Private Shared Sub WriteDouble(ms As Stream, value As Double)
            Dim bits = BitConverter.DoubleToInt64Bits(value)
            For shift = 56 To 0 Step -8
                ms.WriteByte(CByte((bits >> shift) And &HFFL))
            Next
        End Sub

    End Class

End Namespace
