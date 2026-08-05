Imports System
Imports System.IO
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Holt den Wortlaut aus einer Photoshop-Textebene.
    '''
    ''' Text steht in einer PSD im Zusatzblock "TySh". Der beginnt mit einer Abbildungsmatrix und
    ''' enthält danach eine verschachtelte Beschreibungsstruktur, in der unter dem Schlüssel "Txt "
    ''' die Zeichen selbst liegen. Diese Struktur wird hier durchlaufen, bis der Schlüssel gefunden
    ''' ist; alles andere wird übersprungen.
    '''
    ''' Bewusst NUR der Wortlaut. Schrift, Grad, Laufweite und Farbe stecken daneben in "EngineData",
    ''' einem eigenen, verschachtelten Datenblock, dessen Aufbau nirgends festgeschrieben ist. Was
    ''' man daraus mit Raten gewänne, wäre ein Text, der anders aussieht als im Original - und der
    ''' Fehler fiele erst auf, wenn jemand genau hinsieht. Lage und Größe kommen deshalb aus dem
    ''' Ebenenrechteck, die Farbe wird aus den gerasterten Bildpunkten gemessen, und die Schrift ist
    ''' die Vorgabe. Wer das nicht will, lässt die Ebene als Bild kommen - dann stimmt jeder Punkt.
    ''' </summary>
    Public NotInheritable Class PsdTextReader

        Private Sub New()
        End Sub

        ''' Schutz gegen absichtlich oder versehentlich verbogene Längenangaben.
        Private Const MaxStringChars As Integer = 100_000
        Private Const MaxDescriptorItems As Integer = 4_096
        Private Const MaxDepth As Integer = 24

        ''' <summary>Liest den Wortlaut aus einem TySh-Block. "" wenn keiner darin steht oder der
        ''' Aufbau nicht verstanden wird - dann bleibt die Ebene ein Bild.</summary>
        Public Shared Function ExtractText(block As Byte()) As String
            If block Is Nothing OrElse block.Length < 100 Then Return ""

            Try
                Using ms As New MemoryStream(block, False)
                    ' Version (2), Abbildungsmatrix (6 mal 8), Textversion (2), Version der
                    ' Beschreibungsstruktur (4). Danach beginnt die Struktur selbst.
                    If ms.Length < 2 + 48 + 2 + 4 Then Return ""
                    ms.Seek(2 + 48 + 2 + 4, SeekOrigin.Begin)

                    Dim found = ""
                    ReadDescriptor(ms, found, 0)
                    Return found
                End Using
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Läuft eine Beschreibungsstruktur ab und merkt sich den Wert unter "Txt ".
        ''' Liefert False, sobald etwas nicht mehr gelesen werden kann - dann bricht der Aufrufer ab
        ''' und behält, was bis dahin gefunden wurde.</summary>
        Private Shared Function ReadDescriptor(ms As MemoryStream, ByRef found As String, depth As Integer) As Boolean
            If depth > MaxDepth Then Return False

            ' Name der Struktur (Unicode) und ihre Klasse - beides wird nicht gebraucht.
            If Not SkipUnicodeString(ms) Then Return False
            If Not SkipKeyOrClass(ms) Then Return False

            Dim count = ReadU32(ms)
            If count < 0 OrElse count > MaxDescriptorItems Then Return False

            For i = 1L To count
                Dim key = ReadKeyOrClass(ms)
                If key Is Nothing Then Return False
                Dim osType = ReadFourCC(ms)
                If osType Is Nothing Then Return False

                If key = "Txt " AndAlso osType = "TEXT" Then
                    Dim text = ReadUnicodeString(ms)
                    If text Is Nothing Then Return False
                    ' Photoshop schliesst Zeilen mit einem Wagenruecklauf ab; im Editor ist der
                    ' Zeilenumbruch das uebliche Zeichen.
                    found = text.Replace(vbCr, vbLf).TrimEnd(ChrW(0))
                    Return True
                End If

                If Not SkipValue(ms, osType, found, depth) Then Return False
                ' Wurde der Text in einer verschachtelten Struktur gefunden, ist die Suche vorbei.
                If found.Length > 0 Then Return True
            Next
            Return True
        End Function

        Private Shared Function SkipValue(ms As MemoryStream, osType As String, ByRef found As String, depth As Integer) As Boolean
            Select Case osType
                Case "Objc", "GlbO"
                    Return ReadDescriptor(ms, found, depth + 1)
                Case "VlLs"
                    Dim n = ReadU32(ms)
                    If n < 0 OrElse n > MaxDescriptorItems Then Return False
                    For i = 1L To n
                        Dim inner = ReadFourCC(ms)
                        If inner Is Nothing Then Return False
                        If Not SkipValue(ms, inner, found, depth + 1) Then Return False
                        If found.Length > 0 Then Return True
                    Next
                    Return True
                Case "doub", "comp"
                    Return Skip(ms, 8)
                Case "UntF"
                    Return Skip(ms, 12)   ' Einheit (4) und Wert (8)
                Case "TEXT"
                    Return ReadUnicodeString(ms) IsNot Nothing
                Case "enum"
                    Return SkipKeyOrClass(ms) AndAlso SkipKeyOrClass(ms)
                Case "long"
                    Return Skip(ms, 4)
                Case "bool"
                    Return Skip(ms, 1)
                Case "type", "GlbC"
                    Return SkipUnicodeString(ms) AndAlso SkipKeyOrClass(ms)
                Case "alis", "tdta"
                    Dim len = ReadU32(ms)
                    If len < 0 Then Return False
                    Return Skip(ms, len)
                Case Else
                    ' Verweise ("obj ") und alles Unbekannte: hier ist Schluss. Weiterzuraten
                    ' verschoebe den Lesezeiger und machte aus dem Rest Unsinn.
                    Return False
            End Select
        End Function

        ' ── Bausteine der Struktur ───────────────────────────────────────────────

        ''' Eine Zeichenkette: Länge in ZEICHEN, danach UTF-16 Big-Endian.
        Private Shared Function ReadUnicodeString(ms As MemoryStream) As String
            Dim len = ReadU32(ms)
            If len < 0 OrElse len > MaxStringChars Then Return Nothing
            If ms.Position + len * 2 > ms.Length Then Return Nothing
            Dim sb As New StringBuilder(CInt(len))
            For i = 1L To len
                Dim hi = ms.ReadByte()
                Dim lo = ms.ReadByte()
                If hi < 0 OrElse lo < 0 Then Return Nothing
                sb.Append(ChrW(hi * 256 + lo))
            Next
            Return sb.ToString()
        End Function

        Private Shared Function SkipUnicodeString(ms As MemoryStream) As Boolean
            Dim len = ReadU32(ms)
            If len < 0 OrElse len > MaxStringChars Then Return False
            Return Skip(ms, len * 2)
        End Function

        ''' Schlüssel und Klassennamen: eine Länge, und ist sie 0, folgen genau vier Zeichen.
        Private Shared Function ReadKeyOrClass(ms As MemoryStream) As String
            Dim len = ReadU32(ms)
            If len < 0 OrElse len > 1024 Then Return Nothing
            Dim count = If(len = 0, 4L, len)
            If ms.Position + count > ms.Length Then Return Nothing
            Dim buf(CInt(count) - 1) As Byte
            If ms.Read(buf, 0, buf.Length) <> buf.Length Then Return Nothing
            Return Encoding.ASCII.GetString(buf)
        End Function

        Private Shared Function SkipKeyOrClass(ms As MemoryStream) As Boolean
            Return ReadKeyOrClass(ms) IsNot Nothing
        End Function

        Private Shared Function ReadFourCC(ms As MemoryStream) As String
            If ms.Position + 4 > ms.Length Then Return Nothing
            Dim buf(3) As Byte
            If ms.Read(buf, 0, 4) <> 4 Then Return Nothing
            Return Encoding.ASCII.GetString(buf)
        End Function

        Private Shared Function Skip(ms As MemoryStream, count As Long) As Boolean
            If count < 0 OrElse ms.Position + count > ms.Length Then Return False
            ms.Seek(count, SeekOrigin.Current)
            Return True
        End Function

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und ergäbe still 0.
        Private Shared Function ReadU32(ms As MemoryStream) As Long
            If ms.Position + 4 > ms.Length Then Return -1
            Dim b(3) As Byte
            If ms.Read(b, 0, 4) <> 4 Then Return -1
            Return (CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3))
        End Function

    End Class

End Namespace
