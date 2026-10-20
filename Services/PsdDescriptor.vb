Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Eine Beschreibungsstruktur aus einer Photoshop-Datei, gelesen als Baum aus Schlüsseln und
    ''' Werten.
    '''
    ''' Photoshop legt fast alles, was über Bildpunkte hinausgeht, in dieser Bauform ab: Effekte,
    ''' Korrekturebenen, Füllungen, Smartobjekte, den Text einer Textebene. Jeder Eintrag ist ein
    ''' Schlüssel, eine Typkennung aus vier Zeichen und der Wert. Werte können selbst wieder
    ''' Strukturen oder Listen sein.
    '''
    ''' Gelesen wird ganz oder gar nicht: trifft der Leser auf eine Typkennung, die er nicht kennt,
    ''' liefert er Nothing. Weiterzuraten verschöbe den Lesezeiger und machte aus dem Rest Unsinn,
    ''' und eine halb gelesene Struktur sähe aus wie eine vollständige. Wer mit Nothing umgehen
    ''' muss, entscheidet selbst, was dann gilt.
    '''
    ''' Die Werte im Baum:
    ''' - "bool" als Boolean, "long" als Long, "comp" als Long, "doub" als Double
    ''' - "UntF" als <see cref="PsdUnitValue"/>, "UnFl" als Liste von <see cref="PsdUnitValue"/>
    ''' - "TEXT" als String
    ''' - "enum" als <see cref="PsdEnumValue"/>
    ''' - "type" und "GlbC" als String (der Klassenname)
    ''' - "Objc" und "GlbO" als PsdDescriptor
    ''' - "VlLs" als List(Of Object)
    ''' - "alis" und "tdta" als Byte()
    ''' Verweise ("obj ") und Objektfelder ("ObAr") kennt der Leser nicht.
    ''' </summary>
    Public NotInheritable Class PsdDescriptor

        ''' Schutz gegen absichtlich oder versehentlich verbogene Längenangaben.
        Private Const MaxStringChars As Integer = 100_000
        Private Const MaxItems As Integer = 4_096
        Private Const MaxDepth As Integer = 24
        Private Const MaxRawBytes As Integer = 64_000_000

        Public Property ClassId As String = ""
        Public ReadOnly Property Items As New List(Of KeyValuePair(Of String, Object))()

        Private Sub New()
        End Sub

        ''' <summary>Der Wert unter diesem Schlüssel, oder Nothing. Schlüssel aus vier Zeichen
        ''' tragen ihre Leerzeichen mit ("Clr ").</summary>
        Public Function GetValue(key As String) As Object
            For Each item In Items
                If String.Equals(item.Key, key, StringComparison.Ordinal) Then Return item.Value
            Next
            Return Nothing
        End Function

        ''' <summary>Liest eine Struktur ab <paramref name="offset"/>, OHNE vorangestellte
        ''' Versionsnummer. Nothing, wenn sie sich nicht vollständig lesen lässt.</summary>
        Public Shared Function Parse(data As Byte(), offset As Integer) As PsdDescriptor
            If data Is Nothing OrElse offset < 0 OrElse offset >= data.Length Then Return Nothing
            Dim position = offset
            Try
                Return ReadDescriptor(data, position, 0)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Wie <see cref="Parse"/>, für die übliche Form mit vorangestellter Version (vier
        ''' Byte, bei Photoshop immer 16).</summary>
        Public Shared Function ParseVersioned(data As Byte(), offset As Integer) As PsdDescriptor
            If data Is Nothing OrElse offset < 0 OrElse offset + 4 > data.Length Then Return Nothing
            If ReadU32(data, offset) <> 16 Then Return Nothing
            Return Parse(data, offset + 4)
        End Function

        Private Shared Function ReadDescriptor(data As Byte(), ByRef position As Integer, depth As Integer) As PsdDescriptor
            If depth > MaxDepth Then Return Nothing
            ' Der Name der Struktur (Unicode) wird nicht gebraucht, die Klasse schon.
            If ReadUnicodeString(data, position) Is Nothing Then Return Nothing
            Dim classId = ReadKey(data, position)
            If classId Is Nothing Then Return Nothing

            Dim count = ReadU32Checked(data, position)
            If count < 0 OrElse count > MaxItems Then Return Nothing

            Dim result As New PsdDescriptor With {.ClassId = classId}
            For i = 1L To count
                Dim key = ReadKey(data, position)
                If key Is Nothing Then Return Nothing
                Dim osType = ReadFourCC(data, position)
                If osType Is Nothing Then Return Nothing
                Dim ok As Boolean
                Dim value = ReadValue(data, position, osType, depth, ok)
                If Not ok Then Return Nothing
                result.Items.Add(New KeyValuePair(Of String, Object)(key, value))
            Next
            Return result
        End Function

        Private Shared Function ReadValue(data As Byte(), ByRef position As Integer, osType As String,
                                          depth As Integer, ByRef ok As Boolean) As Object
            ok = False
            Select Case osType
                Case "Objc", "GlbO"
                    Dim inner = ReadDescriptor(data, position, depth + 1)
                    ok = inner IsNot Nothing
                    Return inner
                Case "VlLs"
                    Dim n = ReadU32Checked(data, position)
                    If n < 0 OrElse n > MaxItems Then Return Nothing
                    Dim list As New List(Of Object)()
                    For i = 1L To n
                        Dim inner = ReadFourCC(data, position)
                        If inner Is Nothing Then Return Nothing
                        Dim innerOk As Boolean
                        Dim element = ReadValue(data, position, inner, depth + 1, innerOk)
                        If Not innerOk Then Return Nothing
                        list.Add(element)
                    Next
                    ok = True
                    Return list
                Case "doub"
                    If Not Has(data, position, 8) Then Return Nothing
                    Dim value = ReadDouble(data, position)
                    ok = True
                    Return value
                Case "UntF"
                    Dim unit = ReadFourCC(data, position)
                    If unit Is Nothing OrElse Not Has(data, position, 8) Then Return Nothing
                    Dim value = ReadDouble(data, position)
                    ok = True
                    Return New PsdUnitValue(unit, value)
                Case "UnFl"
                    Dim unit = ReadFourCC(data, position)
                    If unit Is Nothing Then Return Nothing
                    Dim n = ReadU32Checked(data, position)
                    If n < 0 OrElse n > MaxItems OrElse Not Has(data, position, CInt(n) * 8) Then Return Nothing
                    Dim values As New List(Of PsdUnitValue)()
                    For i = 1L To n
                        values.Add(New PsdUnitValue(unit, ReadDouble(data, position)))
                    Next
                    ok = True
                    Return values
                Case "TEXT"
                    Dim text = ReadUnicodeString(data, position)
                    ok = text IsNot Nothing
                    Return text
                Case "enum"
                    Dim typeId = ReadKey(data, position)
                    If typeId Is Nothing Then Return Nothing
                    Dim valueId = ReadKey(data, position)
                    If valueId Is Nothing Then Return Nothing
                    ok = True
                    Return New PsdEnumValue(typeId, valueId)
                Case "long"
                    Dim raw = ReadU32Checked(data, position)
                    If raw < 0 Then Return Nothing
                    ' Mit Vorzeichen: ein Versatz oder Winkel darf negativ sein.
                    ok = True
                    Return If(raw > Integer.MaxValue, raw - 4_294_967_296L, raw)
                Case "comp"
                    If Not Has(data, position, 8) Then Return Nothing
                    Dim high = ReadU32(data, position)
                    Dim low = ReadU32(data, position + 4)
                    position += 8
                    ok = True
                    Return (high << 32) Or low
                Case "bool"
                    If Not Has(data, position, 1) Then Return Nothing
                    Dim flag = data(position) <> 0
                    position += 1
                    ok = True
                    Return flag
                Case "type", "GlbC"
                    If ReadUnicodeString(data, position) Is Nothing Then Return Nothing
                    Dim classId = ReadKey(data, position)
                    ok = classId IsNot Nothing
                    Return classId
                Case "alis", "tdta"
                    Dim length = ReadU32Checked(data, position)
                    If length < 0 OrElse length > MaxRawBytes OrElse Not Has(data, position, CInt(length)) Then Return Nothing
                    Dim bytes(CInt(length) - 1) As Byte
                    Buffer.BlockCopy(data, position, bytes, 0, CInt(length))
                    position += CInt(length)
                    ok = True
                    Return bytes
                Case Else
                    Return Nothing
            End Select
        End Function

        ' ── Bausteine ────────────────────────────────────────────────────────────

        Private Shared Function Has(data As Byte(), position As Integer, count As Integer) As Boolean
            Return count >= 0 AndAlso position >= 0 AndAlso CLng(position) + count <= data.Length
        End Function

        ''' Eine Zeichenkette: Länge in ZEICHEN, danach UTF-16 Big-Endian.
        Private Shared Function ReadUnicodeString(data As Byte(), ByRef position As Integer) As String
            Dim length = ReadU32Checked(data, position)
            If length < 0 OrElse length > MaxStringChars OrElse Not Has(data, position, CInt(length) * 2) Then Return Nothing
            Dim sb As New StringBuilder(CInt(length))
            For i = 0 To CInt(length) - 1
                sb.Append(ChrW(CInt(data(position + i * 2)) * 256 + data(position + i * 2 + 1)))
            Next
            position += CInt(length) * 2
            Return sb.ToString().TrimEnd(ChrW(0))
        End Function

        ''' Schlüssel und Klassennamen: eine Länge, und ist sie 0, folgen genau vier Zeichen.
        Private Shared Function ReadKey(data As Byte(), ByRef position As Integer) As String
            Dim length = ReadU32Checked(data, position)
            If length < 0 OrElse length > 1024 Then Return Nothing
            Dim count = If(length = 0, 4, CInt(length))
            If Not Has(data, position, count) Then Return Nothing
            Dim key = Encoding.ASCII.GetString(data, position, count)
            position += count
            Return key
        End Function

        Private Shared Function ReadFourCC(data As Byte(), ByRef position As Integer) As String
            If Not Has(data, position, 4) Then Return Nothing
            Dim code = Encoding.ASCII.GetString(data, position, 4)
            position += 4
            Return code
        End Function

        Private Shared Function ReadDouble(data As Byte(), ByRef position As Integer) As Double
            Dim bits = (ReadU32(data, position) << 32) Or ReadU32(data, position + 4)
            position += 8
            Return BitConverter.Int64BitsToDouble(bits)
        End Function

        ''' Liest vier Byte und rückt vor; -1, wenn sie nicht mehr da sind.
        Private Shared Function ReadU32Checked(data As Byte(), ByRef position As Integer) As Long
            If Not Has(data, position, 4) Then Return -1
            Dim value = ReadU32(data, position)
            position += 4
            Return value
        End Function

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und ergäbe still 0.
        Private Shared Function ReadU32(data As Byte(), position As Integer) As Long
            Return (CLng(data(position)) << 24) Or (CLng(data(position + 1)) << 16) Or
                   (CLng(data(position + 2)) << 8) Or CLng(data(position + 3))
        End Function

    End Class

    ''' <summary>Ein Wert mit Einheit ("UntF"), etwa "#Pxl" für Bildpunkte, "#Prc" für Prozent,
    ''' "#Ang" für Grad.</summary>
    Public NotInheritable Class PsdUnitValue
        Public ReadOnly Property Unit As String
        Public ReadOnly Property Value As Double

        Public Sub New(unit As String, value As Double)
            Me.Unit = unit
            Me.Value = value
        End Sub
    End Class

    ''' <summary>Ein Aufzählungswert ("enum"): der Typ und der gewählte Eintrag, etwa "BlnM" und
    ''' "Mltp" für die Mischmethode Multiplizieren.</summary>
    Public NotInheritable Class PsdEnumValue
        Public ReadOnly Property TypeId As String
        Public ReadOnly Property ValueId As String

        Public Sub New(typeId As String, valueId As String)
            Me.TypeId = typeId
            Me.ValueId = valueId
        End Sub
    End Class

End Namespace
