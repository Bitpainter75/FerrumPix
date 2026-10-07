Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif
Imports MetadataExtractor.Formats.Exif.Makernotes

Namespace Services

    ''' <summary>Loest die Objektivnummer im Herstellerteil von Sony-A- und Pentax-Gehaeusen zu
    ''' einem Namen auf. Beide schreiben kein EXIF-LensModel, sondern nur eine Kennung; die
    ''' Tabellen dazu liegen als <c>Resources/SonyAMountLensIds.tsv</c> und
    ''' <c>Resources/PentaxLensIds.tsv</c> bei. Unter einer Kennung, die mehrere Objektive
    ''' meint, steht dort nichts, und dann liefert die Abfrage nichts.</summary>
    Public NotInheritable Class MakerLensIdService

        Private Sub New()
        End Sub

        Private Const SonyTableResource As String = "SonyAMountLensIds.tsv"
        Private Const PentaxTableResource As String = "PentaxLensIds.tsv"

        ' Pentax legt den Objektivtyp unter 0x003F ab: zwei Bytes, Serie und Modell, bei neueren
        ' Gehaeusen gefolgt von weiteren, die fuer die Kennung nicht zaehlen. MetadataExtractor
        ' kennt das Feld nicht und fuehrt den Herstellerteil aelterer Gehaeuse ausserdem unter
        ' dem Namen "Casio" (gleicher Aufbau).
        Private Const PentaxLensTypeTag As Integer = &H3F

        Private Shared ReadOnly _sonyLenses As New Lazy(Of Dictionary(Of String, String))(Function() LoadTable(SonyTableResource))
        Private Shared ReadOnly _pentaxLenses As New Lazy(Of Dictionary(Of String, String))(Function() LoadTable(PentaxTableResource))
        Private Shared ReadOnly _tableStamp As New Lazy(Of String)(AddressOf ComputeTableStamp)

        ''' <summary>Fingerabdruck beider Tabellen, acht Zeichen. Steht wie der der Nikon-Tabelle
        ''' im Stempel jeder Katalogzeile, damit eine neue Tabelle die Objektivnamen nachzieht.</summary>
        Public Shared ReadOnly Property TableStamp As String
            Get
                Return _tableStamp.Value
            End Get
        End Property

        Public Shared ReadOnly Property SonyLensCount As Integer
            Get
                Return _sonyLenses.Value.Count
            End Get
        End Property

        Public Shared ReadOnly Property PentaxLensCount As Integer
            Get
                Return _pentaxLenses.Value.Count
            End Get
        End Property

        ''' <summary>Der Objektivname aus der Kennung, oder leer.</summary>
        Public Shared Function TryGetLensName(metaDirectories As IEnumerable(Of Directory)) As String
            If metaDirectories Is Nothing Then Return ""
            Dim sony = TryGetSonyLensId(metaDirectories)
            Dim result As String = Nothing
            If sony.Length > 0 AndAlso _sonyLenses.Value.TryGetValue(sony, result) Then Return result
            Dim pentax = TryGetPentaxLensId(metaDirectories)
            If pentax.Length > 0 AndAlso _pentaxLenses.Value.TryGetValue(pentax, result) Then Return result
            Return ""
        End Function

        ''' <summary>Die Nummer aus dem LensID-Feld eines Sony-Gehaeuses mit A-Bajonett, als Text.
        ''' NUR dort: Kompakte und E-Bajonett-Gehaeuse fuellen dasselbe Feld mit 0 oder 65535, und
        ''' 0 ist in der Tabelle ein Minolta AF 28-85mm.</summary>
        Public Shared Function TryGetSonyLensId(metaDirectories As IEnumerable(Of Directory)) As String
            Dim model = ExifService.GetTagDescAcross(Of ExifIfd0Directory)(metaDirectories, ExifDirectoryBase.TagModel).Trim()
            If Not (model.StartsWith("DSLR-", StringComparison.OrdinalIgnoreCase) OrElse
                    model.StartsWith("SLT-", StringComparison.OrdinalIgnoreCase) OrElse
                    model.StartsWith("ILCA-", StringComparison.OrdinalIgnoreCase)) Then Return ""
            For Each sony In metaDirectories.OfType(Of SonyType1MakernoteDirectory)()
                Dim value As Integer
                If sony.TryGetInt32(SonyType1MakernoteDirectory.TagLensId, value) Then Return value.ToString(Globalization.CultureInfo.InvariantCulture)
            Next
            Return ""
        End Function

        ''' <summary>Serie und Modell aus dem Objektivtyp eines Pentax-Gehaeuses ("4 39"). Nur bei
        ''' einer Pentax- oder Ricoh-Kamera: dieselbe Feldnummer bedeutet bei Casio anderes.</summary>
        Public Shared Function TryGetPentaxLensId(metaDirectories As IEnumerable(Of Directory)) As String
            Dim make = ExifService.GetTagDescAcross(Of ExifIfd0Directory)(metaDirectories, ExifDirectoryBase.TagMake)
            If Not (make.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) OrElse
                    make.StartsWith("RICOH IMAGING", StringComparison.OrdinalIgnoreCase)) Then Return ""
            For Each directory In metaDirectories
                ' Neuere Gehaeuse (Kennung "PENTAX" im Kopf) landen in einer eigenen Klasse
                ' (PentaxType2MakernoteDirectory), aeltere unter Pentax oder Casio. Gefiltert wird
                ' deshalb ueber den Namen, die Marke ist oben schon geprueft.
                If Not (directory.Name.Contains("Pentax", StringComparison.OrdinalIgnoreCase) OrElse
                        TypeOf directory Is CasioType1MakernoteDirectory OrElse
                        TypeOf directory Is CasioType2MakernoteDirectory) Then Continue For
                If Not directory.ContainsTag(PentaxLensTypeTag) Then Continue For
                Dim bytes = ToBytes(directory.GetObject(PentaxLensTypeTag))
                If bytes Is Nothing OrElse bytes.Length < 2 Then Continue For
                Return $"{bytes(0)} {bytes(1)}"
            Next

            ' In einer DNG steht der Herstellerteil nicht im EXIF, sondern im Feld DNGPrivateData
            ' von IFD0, und MetadataExtractor liefert ihn nur roh.
            For Each ifd0 In metaDirectories.OfType(Of ExifIfd0Directory)()
                Dim data = ifd0.GetByteArray(DngPrivateDataTag)
                Dim lensType = ReadPentaxLensTypeFromPrivateData(data)
                If lensType IsNot Nothing Then Return $"{lensType(0)} {lensType(1)}"
            Next
            Return ""
        End Function

        Private Const DngPrivateDataTag As Integer = &HC634

        ''' <summary>Das Feld 0x003F aus einem Pentax-Herstellerteil in DNGPrivateData. Zwei
        ''' Formen: die Kamera schreibt den Block direkt ("PENTAX \0", Byte-Reihenfolge, dann
        ''' Eintragszahl und Eintraege zu je 12 Bytes); ein Wandler legt ihn hinter einen Kopf
        ''' "Adobe\0MakN" mit Laenge, Byte-Reihenfolge und urspruenglichem Versatz. Die Kennung ist
        ''' vier Bytes lang und steht deshalb im Eintrag selbst, ohne Versatz. Nothing, wenn der
        ''' Block anders aussieht.</summary>
        Private Shared Function ReadPentaxLensTypeFromPrivateData(data As Byte()) As Byte()
            If data Is Nothing Then Return Nothing
            Dim start = 0
            If data.Length > 20 AndAlso System.Text.Encoding.ASCII.GetString(data, 0, 10) = "Adobe" & ChrW(0) & "MakN" Then
                ' Laenge (4), Byte-Reihenfolge des Originals (2), urspruenglicher Versatz (4).
                start = 20
            End If
            If data.Length < start + 12 OrElse
               System.Text.Encoding.ASCII.GetString(data, start, 8) <> "PENTAX " & ChrW(0) Then Return Nothing
            Dim bigEndian = data(start + 8) = AscW("M"c) AndAlso data(start + 9) = AscW("M"c)
            Dim littleEndian = data(start + 8) = AscW("I"c) AndAlso data(start + 9) = AscW("I"c)
            If Not bigEndian AndAlso Not littleEndian Then Return Nothing

            Dim count = ReadUInt16(data, start + 10, bigEndian)
            For i = 0 To count - 1
                Dim entry = start + 12 + i * 12
                If entry + 12 > data.Length Then Return Nothing
                If ReadUInt16(data, entry, bigEndian) <> PentaxLensTypeTag Then Continue For
                ' Typ 1 (Byte) mit hoechstens vier Werten: sie stehen im Wertfeld des Eintrags.
                If ReadUInt16(data, entry + 2, bigEndian) <> 1 Then Return Nothing
                Return {data(entry + 8), data(entry + 9)}
            Next
            Return Nothing
        End Function

        Private Shared Function ReadUInt16(data As Byte(), offset As Integer, bigEndian As Boolean) As Integer
            Return If(bigEndian,
                      (CInt(data(offset)) << 8) Or data(offset + 1),
                      (CInt(data(offset + 1)) << 8) Or data(offset))
        End Function

        Private Shared Function ToBytes(value As Object) As Integer()
            Select Case True
                Case TypeOf value Is Byte()
                    Return DirectCast(value, Byte()).Select(Function(b) CInt(b)).ToArray()
                Case TypeOf value Is UShort()
                    Return DirectCast(value, UShort()).Select(Function(b) CInt(b)).ToArray()
                Case TypeOf value Is Short()
                    Return DirectCast(value, Short()).Select(Function(b) CInt(b)).ToArray()
                Case TypeOf value Is Integer()
                    Return DirectCast(value, Integer())
                Case Else
                    Return Nothing
            End Select
        End Function

        ''' <summary>Je Zeile Kennung und Name, durch einen Tab getrennt, Zeilen mit # sind Kopf.
        ''' Fehlt die Datei oder ist sie kaputt, bleibt die Tabelle leer.</summary>
        Private Shared Function LoadTable(resource As String) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Try
                Dim assembly = GetType(MakerLensIdService).Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(resource, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Return result
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Return result
                    Using reader As New IO.StreamReader(stream, System.Text.Encoding.UTF8)
                        Dim line = reader.ReadLine()
                        While line IsNot Nothing
                            Dim tab = line.IndexOf(ControlChars.Tab)
                            If Not line.StartsWith("#"c) AndAlso tab > 0 Then
                                Dim name = line.Substring(tab + 1).Trim()
                                If name.Length > 0 Then result(line.Substring(0, tab)) = name
                            End If
                            line = reader.ReadLine()
                        End While
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("MakerLensIdService.LoadTable", ex)
            End Try
            Return result
        End Function

        Private Shared Function ComputeTableStamp() As String
            Dim content = String.Join(vbLf, _sonyLenses.Value.OrderBy(Function(p) p.Key, StringComparer.Ordinal).
                                                          Select(Function(p) "s" & p.Key & "=" & p.Value)) &
                          vbLf &
                          String.Join(vbLf, _pentaxLenses.Value.OrderBy(Function(p) p.Key, StringComparer.Ordinal).
                                                            Select(Function(p) "p" & p.Key & "=" & p.Value))
            Using sha = Security.Cryptography.SHA1.Create()
                Dim hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content))
                Return Convert.ToHexString(hash, 0, 4).ToLowerInvariant()
            End Using
        End Function
    End Class
End Namespace
